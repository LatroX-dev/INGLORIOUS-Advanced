using System.Text;
using System.Text.Json;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
void Reject(Action action, string name) { checks++; try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException or IOException or ArgumentException) { return; } throw new Exception("Accepted: " + name); }
string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectService.JsonOptions);
var root = Path.Combine(Path.GetTempPath(), "INGLORIOUS-LightPresetChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var folder = Path.Combine(root, "LightingPresets");
    var builtins = LightingPresetStore.Discover(out var warning, folder);
    Check(warning is null && builtins.Count == DeviceCatalog.All.Count && builtins.All(x => x.IsBuiltIn), "Only computed builtins in fresh library");
    Check(!Directory.Exists(folder), "Discovery does not create files");
    foreach (var builtin in builtins)
    {
        Reject(() => LightingPresetStore.Delete(builtin, folder), "Protected builtin");
        Reject(() => LightingPresetStore.Delete(builtin with { IsBuiltIn = false }, folder), "Cannot bypass builtin by flag");
        var model = builtin.Preset.DeviceId; var device = DeviceCatalog.Get(model); var project = ProjectService.Create(model);
        project.Macros.Add(new() { Id = "keep", Name = "Keep macro", Steps = [new() { Kind = "down", Key = "KC_A" }, new() { Kind = "up", Key = "KC_A" }] });
        project.Layers[2].Keys[device.Keys[0].Id].Binding = "MACRO:keep";
        project.Layers[2].Name = "Keep layer"; project.Slider.Minimum = 21; project.DeviceSettings.DebounceMs = 17;
        var original = Json(project);
        var changed = LightingPresetStore.Apply(project, 2, builtin.Preset);
        Check(original == Json(project), "Source immutable " + model);
        Check(changed.Layers[2].Name == "Keep layer", "Layer name preserved");
        Check(Json(changed.Macros) == Json(project.Macros), "Macros preserved");
        Check(Json(changed.Slider) == Json(project.Slider) && Json(changed.DeviceSettings) == Json(project.DeviceSettings), "Device settings preserved");
        Check(changed.Name == project.Name && changed.DeviceId == project.DeviceId, "Profile identity preserved");
        for (var i = 0; i < 4; i++)
            if (i != 2) Check(Json(changed.Layers[i]) == Json(project.Layers[i]), "Other layer unchanged");
        foreach (var key in device.Keys) Check(changed.Layers[2].Keys[key.Id].Binding == project.Layers[2].Keys[key.Id].Binding, "Every binding preserved");
        Check(changed.Layers[2].LightingEffect.Mode == 2 && changed.Layers[2].LightingEffect.Colors.Count == 4 && changed.Layers[2].SynchronizeSides, "Builtin has a global red sequence and synchronized sides");
        foreach (var led in device.LeftSideLedIndices.Concat(device.RightSideLedIndices))
            Check(changed.Layers[2].LedEffects[led].Mode == 2 && changed.Layers[2].LedEffects[led].Colors.Count == 4, "Every side LED has its own red sequence");
        Check(device.Keys.Where(k => k.LedIndex.HasValue).Any(k => changed.Layers[2].LedEffects.GetValueOrDefault(k.LedIndex!.Value)?.Mode == 2), "Per-key animated accents exist");
        Check(device.Keys.Where(k => k.LedIndex.HasValue).Any(k => !changed.Layers[2].LedEffects.ContainsKey(k.LedIndex!.Value)), "Some keys inherit the real global animation");
        OnboardSerializer.ValidateEditorProject(changed);
        AppProject decoded;
        if (device.IsOfflineOnly)
        {
            var profilePath = Path.Combine(root, model + ".json");
            ProjectService.Save(profilePath, changed); decoded = ProjectService.Load(profilePath);
            Check(Json(changed) == Json(decoded), "Offline preset survives saving and reopening the profile");
            Reject(() => OnboardSerializer.Prepare(changed), "Offline preset cannot become a transfer payload");
        }
        else
        {
            var image = OnboardSerializer.Prepare(changed);
            decoded = OnboardSerializer.Decode(image.Payload);
            Check(OnboardSerializer.Prepare(decoded).Payload.AsSpan().SequenceEqual(image.Payload), "Applied preset survives the complete onboard codec byte for byte");
        }
        var scene = LightingPreviewEngine.CreateScene(device, changed.Layers[2]);
        var decodedScene = LightingPreviewEngine.CreateScene(device, decoded.Layers[2]);
        var frame = new PreviewRgb[scene.LedCount]; var minimum = Enumerable.Repeat(255, scene.LedCount).ToArray();
        var decodedFrame = new PreviewRgb[scene.LedCount]; var maximum = new int[scene.LedCount];
        var variations = Enumerable.Range(0, scene.LedCount).Select(_ => new HashSet<PreviewRgb>()).ToArray();
        var keyLeds = scene.Leds.Where(led => led.KeyId is not null).ToArray();
        var sideLeds = scene.Leds.Where(led => led.Side).ToArray();
        var keySpatialContrast = 0; var sideSpatialContrast = 0;
        var snapshotBefore = Json(changed);
        Check(scene.Animated && !scene.Reactive, "Preset animates autonomously without key presses");
        Check(device.LeftSideLedIndices.Count == device.RightSideLedIndices.Count, "Preset sides expose matching physical pairs");
        // Every millisecond across the largest possible effect period covers all 256 phase values
        // at every preset speed. Check the actual per-LED engine after brightness scaling.
        for (uint time = 0; time <= 10000; time++)
        {
            scene.Render(time, [], frame);
            decodedScene.Render(time, [], decodedFrame);
            Check(frame.AsSpan().SequenceEqual(decodedFrame), "Restored configuration renders the exact same temporal frame");
            keySpatialContrast = Math.Max(keySpatialContrast, keyLeds.Max(led => frame[led.Index].R) - keyLeds.Min(led => frame[led.Index].R));
            sideSpatialContrast = Math.Max(sideSpatialContrast, sideLeds.Max(led => frame[led.Index].R) - sideLeds.Min(led => frame[led.Index].R));
            foreach (var led in scene.Leds)
            {
                minimum[led.Index] = Math.Min(minimum[led.Index], frame[led.Index].R);
                maximum[led.Index] = Math.Max(maximum[led.Index], frame[led.Index].R);
                variations[led.Index].Add(frame[led.Index]);
                Check(frame[led.Index].R >= 100 && frame[led.Index].G == 0 && frame[led.Index].B == 0,
                    model + ": every key/side LED remains saturated red without any dark phase");
            }
            for (var side = 0; side < device.LeftSideLedIndices.Count; side++)
                Check(frame[device.LeftSideLedIndices[side]] == frame[device.RightSideLedIndices[side]], "Left and right physical side pairs animate identically");
        }
        foreach (var time in new[] { uint.MaxValue - 1, uint.MaxValue, 0u })
        {
            scene.Render(time, [], frame);
            decodedScene.Render(time, [], decodedFrame);
            Check(frame.All(pixel => pixel.R >= 100 && pixel.G == 0 && pixel.B == 0) && frame.AsSpan().SequenceEqual(decodedFrame), "Clock boundary preserves saturated illumination and restored-profile-equivalent frames");
        }
        foreach (var led in scene.Leds)
            Check(variations[led.Index].Count >= 64 && maximum[led.Index] - minimum[led.Index] >= 120,
                "Every inherited key, accent and side LED has a clearly changing red pulse");
        Check(keySpatialContrast >= 100 && sideSpatialContrast >= 100, "Different key and side positions show visibly offset animation phases");
        Check(Json(changed) == snapshotBefore, "Rendering cannot mutate the applied draft");
        Check(changed.Layers[2].LedEffects.Values.Select(e => string.Join(",", e.Colors)).Append(string.Join(",", changed.Layers[2].LightingEffect.Colors)).Distinct().Count() == 4,
            "Global and individual sequences share four palettes within the eight-palette device capacity");
        var colorsOnly = LightingPreviewEngine.CreateScene(device, changed.Layers[2], colorsOnly: true);
        colorsOnly.Render(0, [], frame); colorsOnly.Render(2345, [], decodedFrame);
        Check(!colorsOnly.Animated && frame.AsSpan().SequenceEqual(decodedFrame) && frame.All(pixel => pixel.R >= 200 && pixel.G == 0 && pixel.B == 0),
            "Color editing displays the saturated base colors while the effect preview supplies animation");
        Console.WriteLine($"PIXELS {model}: all {scene.LedCount} LEDs, 10001 restored-profile-identical frames, minimum key R={keyLeds.Min(l => minimum[l.Index])}, minimum side R={sideLeds.Min(l => minimum[l.Index])}, minimum pulse amplitude={scene.Leds.Min(l => maximum[l.Index] - minimum[l.Index])}, key/side spatial contrast={keySpatialContrast}/{sideSpatialContrast}; G=B=0, master brightness 255.");
        var preset = LightingPresetStore.Capture(changed, 2, "../../CON: 🔴 {0}");
        var entry = LightingPresetStore.SaveToLibrary(preset, folder);
        Check(Path.GetDirectoryName(entry.Path) == folder && Path.GetFileName(entry.Path).Length == 46, "Visible name is never path");
        Check(LightingPresetStore.Load(entry.Path).Name == preset.Name, "User name roundtrip");
        var text = File.ReadAllText(entry.Path);
        Check(!text.Contains("\"binding\"") && !text.Contains("\"macros\"") && !text.Contains("Keep layer") && !text.Contains(root), "Export omits bindings/macros/layer names/paths");
        Check(Json(LightingPresetStore.Load(entry.Path)) == Json(preset), "Every lighting field roundtrips");
        changed.Layers[2].LedEffects.Values.First().Colors.Add("#000000");
        Check(Json(LightingPresetStore.Load(entry.Path)) == Json(preset), "No shared references with source");
        var clone = LightingPresetStore.Apply(project, 1, preset); clone.Layers[1].LightingEffect.Speed++;
        Check(preset.Effect.Speed != clone.Layers[1].LightingEffect.Speed, "No shared effect references after apply");
        var other = ProjectService.Create(model == "pro-iso" ? "numpad" : "pro-iso"); var beforeOther = Json(other);
        Reject(() => LightingPresetStore.Apply(other, 0, preset), "Cross-device apply blocked");
        Check(Json(other) == beforeOther, "Cross-device rejection unchanged");
        Reject(() => LightingPresetStore.Apply(project, 4, preset), "Invalid layer");
        var reservedCopy = LightingPresetStore.SaveToLibrary(builtin.Preset, folder);
        Check(reservedCopy.Preset.Name != "INGLORIOUS" && builtin.Preset.Name == "INGLORIOUS" && !reservedCopy.IsBuiltIn, "Builtin exported/imported copy has independent name");
        LightingPresetStore.Delete(reservedCopy, folder); Check(!File.Exists(reservedCopy.Path), "User copy deletable");
        var before = File.ReadAllBytes(entry.Path);
        Reject(() => LightingPresetStore.Export(entry.Path, preset), "No implicit overwrite"); Check(File.ReadAllBytes(entry.Path).SequenceEqual(before), "No overwrite preserves file");
        preset.Effect.Speed++; LightingPresetStore.Export(entry.Path, preset, overwrite: true);
        Reject(() => LightingPresetStore.Delete(entry, folder), "Changed preset cannot be deleted from stale entry");
        Check(File.Exists(entry.Path), "Changed file retained");
        LightingPresetStore.Delete(new(entry.Path, LightingPresetStore.Load(entry.Path)), folder); Check(!File.Exists(entry.Path), "Matching user entry deleted");

        foreach (var name in new[] { "", " ", new string('x', 81), "line\nbreak" })
            Reject(() => LightingPresetStore.Capture(project, 0, name), "Invalid display name");
        var malformed = LightingPresetStore.Capture(project, 0, "Bad"); malformed.KeyColors.Remove(device.Keys[0].Id);
        Reject(() => LightingPresetStore.Validate(malformed), "Missing key");
        malformed = LightingPresetStore.Capture(project, 0, "Bad"); malformed.KeyColors["unknown"] = "#123456";
        Reject(() => LightingPresetStore.Validate(malformed), "Unknown key");
        malformed = LightingPresetStore.Capture(project, 0, "Bad"); malformed.ExtraLedColors[999] = "#123456";
        Reject(() => LightingPresetStore.Validate(malformed), "Invalid side LED");
        malformed = LightingPresetStore.Capture(project, 0, "Bad"); malformed.LedEffects[999] = new();
        Reject(() => LightingPresetStore.Validate(malformed), "Invalid effect LED");
        malformed = LightingPresetStore.Capture(project, 0, "Bad"); malformed.Effect.Colors = ["#zzzzzz"];
        Reject(() => LightingPresetStore.Validate(malformed), "Invalid color sequence");
        var validJson = Json(LightingPresetStore.Capture(project, 0, "Valid"));
        Reject(() => LightingPresetStore.Parse(Encoding.UTF8.GetBytes(validJson.Replace("\"version\": 1", "\"version\": 3"))), "Future schema");
        Reject(() => LightingPresetStore.Parse(Encoding.UTF8.GetBytes(validJson.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"))), "Duplicate property");
        Reject(() => LightingPresetStore.Parse(Encoding.UTF8.GetBytes(validJson.Replace("\"version\": 1", "\"version\": 1, \"bindings\": {}"))), "Unknown/injected data");
        Reject(() => LightingPresetStore.Parse(Encoding.UTF8.GetBytes(validJson.Replace("\"format\": \"inglorious-lighting\",", ""))), "Required field absent");
    }
    File.WriteAllText(Path.Combine(folder, "broken.lighting.json"), "{");
    var broken = LightingPresetStore.Discover(out warning, folder);
    Check(warning is not null && File.ReadAllText(Path.Combine(folder, "broken.lighting.json")) == "{", "Bad file visible as warning and preserved");
    var outside = Path.Combine(root, "outside.lighting.json"); LightingPresetStore.Export(outside, builtins[0].Preset);
    Reject(() => LightingPresetStore.Delete(new(outside, builtins[0].Preset), folder), "Deletion scoped to library"); Check(File.Exists(outside), "Outside file preserved");
    Reject(() => LightingPresetStore.Parse(new byte[LightingPresetStore.MaximumFileBytes + 1]), "Bounded parser");
    Check(!Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.AllDirectories).Any(), "No temporary file leaks");
    Console.WriteLine($"PASS {checks} lighting-preset checks; no device access.");
}
finally { Directory.Delete(root, recursive: true); }
