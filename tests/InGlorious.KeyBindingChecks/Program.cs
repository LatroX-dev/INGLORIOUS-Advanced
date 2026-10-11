using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
void Reject(Action action, string name)
{
    checks++;
    try { action(); } catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException) { return; }
    throw new Exception("Accepted invalid input: " + name);
}
string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectService.JsonOptions);
KeyBindingDocument Copy(KeyBindingDocument value) => JsonSerializer.Deserialize<KeyBindingDocument>(Json(value), ProjectService.JsonOptions)!;
MacroDefinition Tap(string id, string key = "KC_A", string name = "Action") => new()
{ Id = id, Name = name, Steps = [new() { Kind = "down", Key = key, DelayMs = 0 }, new() { Kind = "up", Key = key, DelayMs = 0 }] };
string NonBindings(AppProject value) => Json(new
{
    value.Name, value.SchemaVersion, value.DeviceId, value.DeviceSettings, value.Standby,
    Layers = value.Layers.Select(layer => new { Colors = layer.Keys.ToDictionary(key => key.Key, key => key.Value.Color), layer.SideLedColor,
        layer.ExtraLedColors, layer.SynchronizeSides, layer.LightingEffect, layer.LedEffects })
});
var root = Path.Combine(Path.GetTempPath(), "INGLORIOUS-KeyBindingChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var device in DeviceCatalog.All)
    {
        var source = ProjectService.Create(device.Id);
        source.Name = "Private profile name";
        source.Macros.Add(Tap("used", "KC_A", "Used")); source.Macros.Add(Tap("unused", "KC_B", "Unused"));
        var key = device.Keys.First(key => !key.IsEncoder).Id;
        source.Layers[1].Keys[key].Binding = "MACRO:used";
        source.Layers[2].Name = "Function set";
        source.Layers[2].Keys[key].Binding = "TG(3)";
        if (device.HasSlider) source.Slider = new() { Mode = 1, Minimum = 80, Maximum = 980, MidiChannel = 5, MidiController = 20 };
        var before = Json(source);
        var captured = KeyBindingTransfer.Capture(source);
        Check(Json(source) == before, device.Id + ": capture preserves source");
        Check(captured.Macros.Count == 1 && captured.Macros[0].Id == "used", device.Id + ": referenced macros only");
        Check(captured.Layers.Count == 4 && captured.Layers.All(layer => layer.Bindings.Count == device.Keys.Count), device.Id + ": all layers and encoder bindings");
        Check((captured.Slider is not null) == device.HasSlider, device.Id + ": slider model gate");
        var file = Path.Combine(root, device.Id + ".bindings.json");
        KeyBindingTransfer.Export(file, captured);
        var bytes = File.ReadAllBytes(file); var text = Encoding.UTF8.GetString(bytes);
        Check(!text.Contains("Private profile name") && !text.Contains("Unused") && !text.Contains("\"color\"") && !text.Contains("\"standby\"") &&
            !text.Contains("\"deviceSettings\"") && !text.Contains(root), device.Id + ": portable binding subset");
        var loaded = KeyBindingTransfer.Load(file);
        Check(Json(loaded) == Json(captured), device.Id + ": file roundtrip");
        var target = ProjectService.Create(device.Id); target.Name = "Target name";
        target.DeviceSettings = new() { UsbPollingIntervalMs = 4, DebounceMs = 20 };
        target.Standby = new() { Enabled = true, TimeoutSeconds = 90, Mode = 1, DimBrightnessPercent = 25 };
        foreach (var layer in target.Layers)
        {
            layer.LightingEffect = new() { Mode = 2, Speed = 199, Brightness = 143, Colors = ["#001122", "#CCDDEE"] };
            layer.SideLedColor = "#223344"; layer.SynchronizeSides = true;
            foreach (var assignment in layer.Keys.Values) assignment.Color = "#112233";
            if (device.LedCount > 0) layer.LedEffects[0] = new() { Mode = 1, Speed = 150, Brightness = 240 };
        }
        target.Macros.Add(Tap("preserved", "KC_C", "Old macro"));
        var targetBefore = Json(target); var documentBefore = Json(loaded); var settings = NonBindings(target);
        var result = KeyBindingTransfer.Apply(target, loaded);
        Check(Json(target) == targetBefore && Json(loaded) == documentBefore, device.Id + ": apply is transactional and detached");
        Check(NonBindings(result) == settings, device.Id + ": lighting/timing/standby/name preserved");
        Check(result.Macros.Count == 2 && Json(result.Macros[0]) == Json(target.Macros[0]), device.Id + ": existing macros preserved");
        Check(Json(KeyBindingTransfer.Capture(result)) == Json(captured), device.Id + ": exact bindings and names applied");
        result.Layers[1].Keys[key].Binding = "KC_Z"; result.Macros.Last().Steps[0].Key = "KC_D";
        if (result.Slider.Mode != 0) result.Slider.Mode = 0;
        Check(Json(target) == targetBefore && Json(loaded) == documentBefore, device.Id + ": later edits cannot mutate originals");
        Reject(() => KeyBindingTransfer.Export(file, captured), device.Id + ": explicit overwrite required");
        Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes), device.Id + ": failed export preserves old bytes");
        KeyBindingTransfer.Export(file, captured, overwrite: true);
        Check(Json(KeyBindingTransfer.Load(file)) == Json(captured), device.Id + ": atomic overwrite roundtrip");
        captured.Macros[0].Steps[0].Key = "KC_Y";
        Check(Json(source) == before, device.Id + ": capture macro steps detached");
    }

    var project = ProjectService.Create("numpad"); project.Macros.Add(Tap("used"));
    project.Layers[0].Keys["r1c0"].Binding = "MACRO:used";
    var document = KeyBindingTransfer.Capture(project);
    var validJson = Json(document);
    var malformedFile = Path.Combine(root, "malformed.bindings.json");
    void RejectJson(string value, string name) { File.WriteAllText(malformedFile, value, new UTF8Encoding(false)); Reject(() => KeyBindingTransfer.Load(malformedFile), name); }
    void ChangedJson(Action<JsonObject> change, string name)
    { var node = JsonNode.Parse(validJson)!.AsObject(); change(node); RejectJson(node.ToJsonString(), name); }
    foreach (var property in new[] { "format", "version", "deviceId", "layers", "macros", "slider" })
    {
        ChangedJson(node => node.Remove(property), "missing root " + property);
        var node = JsonNode.Parse(validJson)!.AsObject();
        RejectJson("{\"" + property + "\":" + node[property]!.ToJsonString() + "," + validJson[1..], "duplicate root " + property);
    }
    ChangedJson(node => node["path"] = "untrusted", "unknown path property");
    ChangedJson(node => node["serialNumber"] = "untrusted", "unknown serial property");
    ChangedJson(node => node["version"] = 2, "future format");
    ChangedJson(node => node["deviceId"] = "NUMPAD", "device case mismatch");
    ChangedJson(node => node["layers"]!.AsArray().RemoveAt(3), "missing layer");
    ChangedJson(node => node["layers"]![0]!["bindings"]!.AsObject().Remove("r1c0"), "missing key");
    ChangedJson(node => node["layers"]![0]!["bindings"]!["foreign"] = "KC_A", "unknown physical key");
    ChangedJson(node => node["layers"]![0]!["bindings"]!["r1c0"] = "KC_TRNS", "transparent base rejected");
    ChangedJson(node => node["layers"]![0]!["bindings"]!["encoder:ccw"] = "MO(1)", "encoder cannot hold layer");
    ChangedJson(node => node["layers"]![1]!["bindings"]!["r1c0"] = "MO(4)", "out of range Fn layer");
    ChangedJson(node => node["layers"]![0]!["bindings"]!["r1c0"] = "MACRO:missing", "missing referenced macro");
    ChangedJson(node => node["layers"]![0]!["bindings"]!["r1c0"] = "KC_A", "unreferenced macro forbidden");
    ChangedJson(node => node["macros"]!.AsArray().Add(node["macros"]![0]!.DeepClone()), "duplicate macro ID");
    ChangedJson(node => node["macros"]![0]!["steps"]!.AsArray().RemoveAt(1), "unreleased macro key");
    ChangedJson(node => node["macros"]![0]!["steps"]![0]!["key"] = "KC_CALC", "macro media code unsupported");
    ChangedJson(node => node["slider"] = null, "numpad slider required");
    ChangedJson(node => node["slider"]!["mode"] = 4, "invalid slider mode");
    ChangedJson(node => node["slider"]!["minimum"] = 1023, "invalid slider calibration");
    foreach (var (path, fields) in new[]
    {
        ("layer", new[] { "name", "bindings" }), ("macro", new[] { "id", "name", "steps" }),
        ("step", new[] { "kind", "key", "delayMs" }), ("slider", new[] { "mode", "minimum", "maximum", "midiChannel", "midiController" })
    })
    {
        JsonObject Find(JsonObject node) => path switch
        { "layer" => node["layers"]![0]!.AsObject(), "macro" => node["macros"]![0]!.AsObject(), "step" => node["macros"]![0]!["steps"]![0]!.AsObject(), _ => node["slider"]!.AsObject() };
        foreach (var field in fields) ChangedJson(node => Find(node).Remove(field), "missing " + path + "." + field);
        ChangedJson(node => Find(node)["unknown"] = 1, "unknown " + path + " property");
        var duplicate = JsonNode.Parse(validJson)!.AsObject(); var nested = Find(duplicate); var member = nested.First();
        RejectJson(duplicate.ToJsonString().Replace(nested.ToJsonString(), "{\"" + member.Key + "\":" + member.Value!.ToJsonString() + "," + nested.ToJsonString()[1..]), "duplicate " + path + " member");
    }
    RejectJson(validJson.Replace("\"r1c0\": \"MACRO:used\"", "\"r1c0\": \"MACRO:used\", \"r1c0\": \"KC_A\""), "duplicate key binding");
    foreach (var invalid in new[] { "", "null", "[]", validJson + "{}", "/*comment*/" + validJson, validJson[..^1] + ",}", "{\"a\":" + new string('[', 25) + "0" + new string(']', 25) + "}" })
        RejectJson(invalid, "invalid JSON syntax/depth");
    File.WriteAllBytes(malformedFile, new byte[KeyBindingTransfer.MaximumFileBytes + 1]);
    Reject(() => KeyBindingTransfer.Load(malformedFile), "bounded file size");
    File.WriteAllBytes(malformedFile, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(validJson)]);
    Check(Json(KeyBindingTransfer.Load(malformedFile)) == validJson, "UTF8 BOM accepted");

    var targetProject = ProjectService.Create("numpad");
    targetProject.Macros.Add(Tap("used", "KC_B", "Existing conflict"));
    targetProject.Macros.Add(Tap("same", "KC_A", "Different name, identical action"));
    var inputBefore = Json(targetProject);
    var applied = KeyBindingTransfer.Apply(targetProject, document);
    Check(applied.Macros.Count == 2 && applied.Layers[0].Keys["r1c0"].Binding == "MACRO:same", "reuse same content before resolving conflicting IDs");
    Check(Json(targetProject) == inputBefore, "reuse preserves original source");
    var repeated = KeyBindingTransfer.Apply(applied, document);
    Check(Json(repeated) == Json(applied), "repeated imports do not accumulate duplicate macros");
    targetProject.Macros.RemoveAt(1);
    var conflictBefore = Json(targetProject);
    applied = KeyBindingTransfer.Apply(targetProject, document);
    var assignedId = applied.Layers[0].Keys["r1c0"].Binding[6..];
    Check(assignedId != "used" && applied.Macros.Count == 2 && applied.Macros.Single(macro => macro.Id == assignedId).Steps[0].Key == "KC_A", "ID conflict safely remapped");
    Check(Json(targetProject) == conflictBefore && applied.Macros[0].Steps[0].Key == "KC_B", "conflicting old macro retained");
    var equivalent = Copy(document); equivalent.Macros[0].Steps.ForEach(step => step.DelayMs = 500);
    Check(KeyBindingTransfer.Apply(applied, equivalent).Macros.Count == 2, "irrelevant key-step delays do not prevent semantic reuse");
    var duplicateContent = Copy(document); var another = Tap("second-id"); duplicateContent.Macros.Add(another);
    duplicateContent.Layers[2].Bindings["r1c0"] = "MACRO:second-id";
    applied = KeyBindingTransfer.Apply(ProjectService.Create("numpad"), duplicateContent);
    Check(applied.Macros.Count == 1 && applied.Layers[0].Keys["r1c0"].Binding == applied.Layers[2].Keys["r1c0"].Binding, "duplicates within import reuse one slot");
    foreach (var id in new[] { "pro-iso", "pro-ansi", "pro-rev2-iso" })
    {
        var mismatch = ProjectService.Create(id); var before = Json(mismatch);
        Reject(() => KeyBindingTransfer.Apply(mismatch, document), "wrong device/layout " + id);
        Check(Json(mismatch) == before, "mismatch leaves source unchanged");
    }
    var iso = ProjectService.Create("pro-iso"); var ansi = KeyBindingTransfer.Capture(ProjectService.Create("pro-ansi"));
    Reject(() => KeyBindingTransfer.Apply(iso, ansi), "same product different layout");
    var unexpectedSlider = KeyBindingTransfer.Capture(iso); unexpectedSlider.Slider = new();
    Reject(() => KeyBindingTransfer.Apply(iso, unexpectedSlider), "PRO cannot import slider fields");

    var full = ProjectService.Create("numpad");
    for (var index = 0; index < 32; index++) full.Macros.Add(new() { Id = "existing-" + index, Name = "Existing", Steps = [new() { Kind = "delay", Key = "KC_A", DelayMs = index + 1 }] });
    var fullBefore = Json(full);
    Reject(() => KeyBindingTransfer.Apply(full, document), "33 macros cannot fit");
    Check(Json(full) == fullBefore, "slot overflow leaves source unchanged");
    var crowded = ProjectService.Create("numpad");
    for (var index = 0; index < 6; index++) crowded.Macros.Add(new()
    { Id = "crowded-" + index, Name = "Old", Steps = Enumerable.Range(0, 128).Select(_ => new MacroStep { Kind = "delay", Key = "KC_A", DelayMs = 1 }).ToList() });
    OnboardSerializer.ValidateEditorProject(crowded);
    var crowdedBefore = Json(crowded);
    Reject(() => KeyBindingTransfer.Apply(crowded, document), "aggregate event storage overflow");
    Check(Json(crowded) == crowdedBefore, "event overflow leaves original unchanged");
    var standby = ProjectService.Create("numpad"); standby.Standby.Enabled = true;
    var capacity = OnboardSerializer.MacroCapacity("numpad", 4);
    for (var remaining = capacity; remaining > 0; remaining -= Math.Min(remaining, 128))
        standby.Macros.Add(new() { Id = "standby-" + remaining, Name = "Old", Steps = Enumerable.Range(0, Math.Min(remaining, 128)).Select(_ => new MacroStep { Kind = "delay", Key = "KC_A", DelayMs = 1 }).ToList() });
    OnboardSerializer.ValidateEditorProject(standby);
    var standbyBefore = Json(standby);
    Reject(() => KeyBindingTransfer.Apply(standby, document), "standby/color storage capacity respected");
    Check(Json(standby) == standbyBefore, "shared storage failure never removes old data");

    foreach (var name in new[] { "GMMK Numpad", "CON", "LPT1", "../folder/bad:name", "Custom.inglorious.json", "Custom.bindings.json", " ", new string('a', 150), "a\nname" })
    {
        var named = ProjectService.Create("numpad"); named.Name = name;
        var filename = KeyBindingTransfer.SuggestFileName(named);
        Check(filename.EndsWith(".bindings.json", StringComparison.Ordinal) && Path.GetFileName(filename) == filename &&
            !filename.Any(char.IsControl) && !filename.Any(character => "<>:\"/\\|?*".Contains(character)) && filename.Length <= 110,
            "safe suggested name: " + name);
    }
    Check(KeyBindingTransfer.SuggestFileName(new() { DeviceId = "numpad", Name = "GMMK Numpad" }) == "GMMK Numpad.bindings.json", "clean default suggestion");
    var destination = Path.Combine(root, "preserved.json"); File.WriteAllText(destination, "important existing contents");
    var broken = Copy(document); broken.Layers.RemoveAt(3);
    Reject(() => KeyBindingTransfer.Export(destination, broken, overwrite: true), "invalid export never touches existing destination");
    Check(File.ReadAllText(destination) == "important existing contents", "invalid export leaves bytes intact");
    Reject(() => KeyBindingTransfer.Export(destination, document), "default export protects arbitrary existing file");
    Check(File.ReadAllText(destination) == "important existing contents", "default overwrite denial preserves bytes");
    Check(!Directory.EnumerateFiles(root, "*.tmp-*").Any(), "failed and completed writes remove temporary files");

    var realFolder = Path.Combine(root, "real"); Directory.CreateDirectory(realFolder);
    var link = Path.Combine(root, "alias");
    if (OperatingSystem.IsWindows())
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, realFolder }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; process.WaitForExit();
        Check(process.ExitCode == 0, "isolated export-alias junction created");
    }
    else Directory.CreateSymbolicLink(link, realFolder);
    try
    {
        File.WriteAllText(Path.Combine(realFolder, "profile.json"), "keep profile");
        Reject(() => KeyBindingTransfer.Export(Path.Combine(link, "profile.json"), document, overwrite: true), "linked parent cannot redirect export over a profile");
        Reject(() => KeyBindingTransfer.Export(Path.Combine(link, "new.bindings.json"), document), "linked parent rejected for new file");
        Check(File.ReadAllText(Path.Combine(realFolder, "profile.json")) == "keep profile" && !File.Exists(Path.Combine(realFolder, "new.bindings.json")), "alias rejection preserves destination");
    }
    finally { Directory.Delete(link); }
    if (!OperatingSystem.IsWindows())
    {
        var fileLink = Path.Combine(root, "profile-link"); File.CreateSymbolicLink(fileLink, destination);
        try { Reject(() => KeyBindingTransfer.Export(fileLink, document, overwrite: true), "linked leaf rejected"); }
        finally { File.Delete(fileLink); }
        Check(File.ReadAllText(destination) == "important existing contents", "linked leaf rejection preserves original");
    }
    Console.WriteLine($"PASS {checks} key-binding transfer checks; no device, user-data, or firmware access.");
}
finally { Directory.Delete(root, recursive: true); }
