using System.Security.Cryptography;
using System.Text.Json;
using InGlorious.Core;

var failures = 0;
void Check(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Require(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Rejected(Action action)
{
    try { action(); } catch (Exception ex) when (ex is InvalidDataException or JsonException) { return; }
    throw new Exception("Input was incorrectly accepted");
}
var temp = Path.Combine(Path.GetTempPath(), "INGLORIOUS-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    Check("Generic USB snapshot accepts physical roots independent of model and rejects interfaces", () =>
    {
        var physical = new[]
        {
            @"USB\VID_1234&PID_ABCD\OTHER1", @"usb\vid_a001&pid_0002\7&12345&0&1",
            @"USB\VID_0483&PID_DF11\SERIAL", @"USB\VID_342D&PID_DFA0\5&PORT",
            @"USB\VID_FFFF&PID_FFFF&REV_0001\SERIAL", @"USB\VID_ABCD&PID_0000\SERIAL&MI_00"
        };
        foreach (var id in physical) Require(DeviceScanner.IsPhysicalUsbInstanceId(id), id);
        foreach (var id in new string?[] { null, "", @"HID\VID_1234&PID_ABCD\ONE", @"USB\VID_1234&PID_ABCD&MI_00\ONE", @"usb\vid_1234&pid_abcd&mi_02\one", @"USB\ROOT_HUB30\ONE", @"USB\VID_123&PID_ABCD\ONE", @"USB\VID_1234&PID_G000\ONE", @"USB\VID_1234&PID_ABCD\ONE\CHILD", @"USB\VID_1234&PID_ABCD\", "USB\\VID_1234&PID_ABCD\\ONE\n", "USB\\VID_1234&PID_ABCD\\ONE\0" })
            Require(!DeviceScanner.IsPhysicalUsbInstanceId(id), "not a physical USB root: " + id);
        var source = physical.Concat([physical[0].ToLowerInvariant()]).ToArray();
        var parsed = DeviceScanner.ParsePresentUsbInstanceIds(JsonSerializer.Serialize(source));
        Require(parsed.Count == physical.Length && physical.All(id => parsed.Contains(id, StringComparer.OrdinalIgnoreCase)), "parser retains arbitrary roots and collapses only case-equivalent duplicate IDs");
        Require(DeviceScanner.ParsePresentUsbInstanceIds("[]").Count == 0, "a successful empty snapshot is distinct from a failed scan");
        foreach (var invalid in new[] { "null", "{}", "\"USB\\\\VID_1234&PID_ABCD\\\\ONE\"", "[null]", "[123]", "[{}]", "[\"HID\\\\VID_1234&PID_ABCD\\\\ONE\"]", "[\"USB\\\\VID_1234&PID_ABCD&MI_00\\\\ONE\"]", "[broken" })
            Rejected(() => DeviceScanner.ParsePresentUsbInstanceIds(invalid));
    });
    Check("USB disconnect tracking requires positive runtime baseline and never treats errors as absence", () =>
    {
        const string selected = @"USB\VID_9876&PID_5432\DYNAMIC-ONE";
        const string other = @"USB\VID_5678&PID_1000\OTHER-MODEL";
        var tracker = new UsbConnectionTracker(selected);
        Require(tracker.InstanceId == selected && tracker.State == UsbConnectionState.Unknown && !tracker.ObservedConnected);
        Require(tracker.ObserveSnapshot([]) == UsbConnectionState.Unknown && !tracker.ObservedConnected, "no baseline means no disconnect claim");
        Require(tracker.ObserveFailure() == UsbConnectionState.Unknown && !tracker.ObservedConnected);
        Require(tracker.ObserveSnapshot([other]) == UsbConnectionState.Unknown);
        var snapshot = new[] { other, selected.ToLowerInvariant() };
        var original = snapshot.ToArray();
        Require(tracker.ObserveSnapshot(snapshot) == UsbConnectionState.Connected && tracker.ObservedConnected, "exact ID comparison is case insensitive");
        Require(snapshot.SequenceEqual(original), "tracking does not mutate source snapshots");
        Require(tracker.ObserveFailure() == UsbConnectionState.Unknown && tracker.ObservedConnected, "a scan failure clears the current conclusion, not the successful baseline");
        Require(tracker.ObserveSnapshot([other]) == UsbConnectionState.Disconnected, "only a valid later snapshot without the selected model establishes absence");
        Require(tracker.ObserveFailure() == UsbConnectionState.Unknown, "even a previously disconnected state cannot turn a current scan failure into disconnection");
        Require(tracker.ObserveSnapshot([]) == UsbConnectionState.Disconnected);
        Require(tracker.ObserveSnapshot([selected]) == UsbConnectionState.Connected, "normal reappearance updates state");
        foreach (var invalid in new IReadOnlyList<string>?[] { null, ["bad"], [selected, "bad"], [@"HID\VID_9876&PID_5432\ONE"], [null!] })
            Require(tracker.ObserveSnapshot(invalid!) == UsbConnectionState.Unknown && tracker.ObservedConnected, "malformed snapshots cannot establish disconnection");
        var fresh = new UsbConnectionTracker(selected);
        Require(fresh.ObserveSnapshot([]) == UsbConnectionState.Unknown && !fresh.ObservedConnected, "new UI flow does not inherit old observations");
        Rejected(() => new UsbConnectionTracker(@"USB\VID_9876&PID_5432&MI_00\ONE"));
        Rejected(() => new UsbConnectionTracker("not-a-device"));
    });
    Check("USB disconnect tracking stays unknown when the same dynamic VID/PID reenumerates", () =>
    {
        const string selected = @"USB\VID_AB12&PID_CD34\RUNTIME-ID";
        var tracker = new UsbConnectionTracker(selected);
        Require(tracker.ObserveSnapshot([selected]) == UsbConnectionState.Connected);
        foreach (var alternate in new[] { @"USB\VID_AB12&PID_CD34\ANOTHER-SERIAL", @"usb\vid_ab12&pid_cd34\different-port", @"USB\VID_AB12&PID_CD34&REV_9999\DIFFERENT-REV" })
        {
            Require(tracker.ObserveSnapshot([alternate]) == UsbConnectionState.Unknown && tracker.ObservedConnected, "same-model ambiguity cannot masquerade as a removed cable");
            Require(tracker.ObserveSnapshot([selected, alternate]) == UsbConnectionState.Connected, "the actual selected root remains positively observable");
        }
        Require(tracker.ObserveSnapshot([@"USB\VID_AB12&PID_CD35\NEIGHBOUR"]) == UsbConnectionState.Disconnected, "different PID is not falsely grouped by substring");
        Require(tracker.ObserveSnapshot([@"USB\VID_AB13&PID_CD34\NEIGHBOUR"]) == UsbConnectionState.Disconnected, "different VID is not falsely grouped by substring");
    });
    Check("Custom palette persistence, validation and file preservation", () => ColorPaletteChecks.Run(temp));
    Check("New projects use the neutral device name", () =>
    {
        foreach (var device in DeviceCatalog.All)
            Require(ProjectService.Create(device.Id).Name == device.Name);
    });
    Check("Custom and similar project names remain byte-for-byte unchanged", () =>
    {
        foreach (var id in new[] { "pro-iso", "numpad" })
        foreach (var name in new[] { "Custom colors", "Profile 2", "Profile ", "profile", DeviceCatalog.Get(id).Name })
        {
            var project = ProjectService.Create(id); project.Name = name;
            var file = Path.Combine(temp, "custom-name.json");
            ProjectService.Save(file, project);
            var before = File.ReadAllBytes(file);
            var loaded = ProjectService.Load(file);
            Require(loaded.Name == name);
            Require(JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(project));
            Require(File.ReadAllBytes(file).SequenceEqual(before));
        }
    });
    Check("Layout keys, physical counts, unique IDs and LED maps", () =>
    {
        Require(DeviceCatalog.Get("pro-iso").Keys.Count == 86, "83 keys + knob + 2 directions expected");
        Require(DeviceCatalog.Get("numpad").Keys.Count == 20, "17 keys + knob + 2 directions expected");
        Require(DeviceCatalog.Get("pro-iso").Keys.Single(k => k.IsIsoEnter).Id == "r10c4", "Only physical PRO ISO Enter has a notched keycap");
        Require(DeviceCatalog.Get("numpad").Keys.All(k => !k.IsIsoEnter), "Numpad Enter remains rectangular");
        Require(DeviceCatalog.Get("pro-iso").LeftSideLedIndices.SequenceEqual(new[] { 68, 71, 74, 77, 81, 84, 88, 92 }));
        Require(DeviceCatalog.Get("pro-iso").RightSideLedIndices.SequenceEqual(new[] { 69, 72, 75, 78, 82, 85, 89, 93 }));
        Require(DeviceCatalog.Get("numpad").LeftSideLedIndices.SequenceEqual(Enumerable.Range(17, 7)));
        Require(DeviceCatalog.Get("numpad").RightSideLedIndices.SequenceEqual(Enumerable.Range(24, 7)));
        foreach (var device in DeviceCatalog.All)
        {
            Require(device.Keys.DistinctBy(k => k.Id).Count() == device.Keys.Count);
            Require(device.Keys.Where(k => k.LedIndex.HasValue).DistinctBy(k => k.LedIndex).Count() == device.Keys.Count(k => k.LedIndex.HasValue));
            Require(device.Keys.Where(k => !k.IsEncoder).All(k => k.X >= 0 && k.Y >= 0 && k.X + k.W <= device.Width && k.Y + k.H <= device.Height));
            Require(device.Keys.All(k => k.X >= 0 && k.Y + device.PreviewTopInset >= 0 && k.X + k.W <= device.PreviewWidth && k.Y + k.H + device.PreviewTopInset <= device.PreviewHeight));
            Require(ProjectService.Validate(ProjectService.Create(device.Id)).Count == 0);
        }
    });
    Check("Round-trip preserves RGB, layers, macro timing and source isolation", () =>
    {
        var project = ProjectService.Create("pro-iso");
        var macro = new MacroDefinition { Id = "copy", Name = "Kopieren", Steps = [new() { Kind = "down", Key = "KC_LCTL" }, new() { Kind = "down", Key = "KC_C" }, new() { Kind = "delay", DelayMs = 80 }, new() { Kind = "up", Key = "KC_C" }, new() { Kind = "up", Key = "KC_LCTL" }] };
        project.Macros.Add(macro);
        project.Layers[2].Keys["r1c3"].Binding = "MACRO:copy";
        project.Layers[2].Keys["r1c3"].Color = "#0011FF";
        var file = Path.Combine(temp, "roundtrip.json");
        ProjectService.Save(file, project);
        var loaded = ProjectService.Load(file);
        Require(JsonSerializer.Serialize(project) == JsonSerializer.Serialize(loaded));
        var clone = ProjectService.Clone(loaded);
        clone.Layers[2].Keys["r1c3"].Color = "#FF0000";
        Require(loaded.Layers[2].Keys["r1c3"].Color == "#0011FF");
        Require(loaded.Macros[0].Steps[2].DelayMs == 80);
    });
    Check("Side synchronization persists independently for each layer", () =>
    {
        var project = ProjectService.Create("numpad");
        Require(project.Layers.All(layer => !layer.SynchronizeSides));
        project.Layers[0].SynchronizeSides = true;
        project.Layers[2].SynchronizeSides = true;
        var file = Path.Combine(temp, "side-sync.json");
        ProjectService.Save(file, project);
        var loaded = ProjectService.Load(file);
        Require(loaded.Layers.Select(layer => layer.SynchronizeSides).SequenceEqual(new[] { true, false, true, false }));
        Require(JsonSerializer.Serialize(project) == JsonSerializer.Serialize(loaded));
    });
    Check("Clone preserves side synchronization without sharing layer preferences", () =>
    {
        var project = ProjectService.Create("pro-iso");
        project.Layers[1].SynchronizeSides = true;
        var clone = ProjectService.Clone(project);
        Require(clone.Layers.Select(layer => layer.SynchronizeSides).SequenceEqual(new[] { false, true, false, false }));
        clone.Layers[1].SynchronizeSides = false;
        clone.Layers[3].SynchronizeSides = true;
        Require(project.Layers[1].SynchronizeSides && !project.Layers[3].SynchronizeSides);
    });
    Check("Older projects without side synchronization default to independent sides", () =>
    {
        var project = ProjectService.Create("pro-iso");
        foreach (var layer in project.Layers) layer.SynchronizeSides = true;
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(project, ProjectService.JsonOptions))!;
        foreach (var layer in json["layers"]!.AsArray()) layer!.AsObject().Remove("synchronizeSides");
        var file = Path.Combine(temp, "legacy-side-sync.json");
        File.WriteAllText(file, json.ToJsonString());
        var loaded = ProjectService.Load(file);
        Require(loaded.Layers.All(layer => !layer.SynchronizeSides));
        Require(loaded.Layers.Select(layer => layer.SideLedColor).SequenceEqual(project.Layers.Select(layer => layer.SideLedColor)));
    });
    Check("Lighting effect catalog exposes all supported modes and directions", () =>
    {
        Require(LightingEffects.All.Select(effect => effect.Id).SequenceEqual(Enumerable.Range(0, 21)));
        Require(LightingEffects.All.All(effect => !string.IsNullOrWhiteSpace(effect.Label) && !string.IsNullOrWhiteSpace(effect.Description)));
        Require(LightingEffects.All.Where(effect => effect.HasDirection).Select(effect => effect.Id).SequenceEqual(new[] { 3, 4, 5, 6, 7, 12, 13, 16, 17 }));
    });
    Check("New and legacy projects use independent static lighting defaults", () =>
    {
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            var project = ProjectService.Create(id);
            Require(project.SchemaVersion == 1);
            Require(project.Layers.All(layer => layer.LightingEffect is { Mode: 0, Speed: 128, Brightness: 255, Target: 3, Reverse: false }));
            Require(!ReferenceEquals(project.Layers[0].LightingEffect, project.Layers[1].LightingEffect));
            project.Layers[2].SynchronizeSides = true;
            var sideLed = DeviceCatalog.Get(id).LeftSideLedIndices[0];
            project.Layers[2].ExtraLedColors[sideLed] = "#123456";
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(project, ProjectService.JsonOptions))!;
            foreach (var layer in json["layers"]!.AsArray()) layer!.AsObject().Remove("lightingEffect");
            var file = Path.Combine(temp, id + "-legacy-effects.json");
            File.WriteAllText(file, json.ToJsonString());
            var original = File.ReadAllBytes(file);
            var loaded = ProjectService.Load(file);
            Require(loaded.SchemaVersion == 1);
            Require(loaded.Layers.All(layer => layer.LightingEffect is { Mode: 0, Speed: 128, Brightness: 255, Target: 3, Reverse: false }));
            Require(JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(project), "Missing effects must not alter colors or side synchronization");
            Require(File.ReadAllBytes(file).SequenceEqual(original), "Loading a legacy project must not rewrite it");
            loaded.Layers[0].LightingEffect.Mode = 1;
            Require(loaded.Layers.Skip(1).All(layer => layer.LightingEffect.Mode == 0), "Layer effects must not share their settings object");
        }
    });
    Check("Lighting effects persist per layer and clones do not share settings", () =>
    {
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            var project = ProjectService.Create(id);
            for (var layer = 0; layer < 4; layer++)
            {
                project.Layers[layer].LightingEffect = new()
                {
                    Mode = layer * 4, Speed = layer * 85, Brightness = 255 - layer * 85,
                    Target = layer % 3 + 1, Reverse = layer % 2 == 1
                };
                project.Layers[layer].SynchronizeSides = layer % 2 == 0;
            }
            var file = Path.Combine(temp, id + "-effects.json");
            ProjectService.Save(file, project);
            var loaded = ProjectService.Load(file);
            var savedState = JsonSerializer.Serialize(loaded);
            Require(savedState == JsonSerializer.Serialize(project));
            var clone = ProjectService.Clone(loaded);
            Require(JsonSerializer.Serialize(clone) == savedState);
            for (var layer = 0; layer < 4; layer++)
                Require(!ReferenceEquals(clone.Layers[layer].LightingEffect, loaded.Layers[layer].LightingEffect));
            clone.Layers[1].LightingEffect.Mode = 9;
            clone.Layers[1].LightingEffect.Speed = 255;
            clone.Layers[1].LightingEffect.Brightness = 0;
            clone.Layers[1].LightingEffect.Target = 3;
            clone.Layers[1].LightingEffect.Reverse = false;
            Require(JsonSerializer.Serialize(loaded) == savedState, "Editing clone effects must preserve the source project");
            Require(clone.Layers[0].LightingEffect.Mode == 0 && clone.Layers[2].LightingEffect.Mode == 8 && clone.Layers[3].LightingEffect.Mode == 12);
        }
    });
    Check("Lighting effects accept all modes, target masks and numeric boundaries", () =>
    {
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            var project = ProjectService.Create(id);
            foreach (var mode in Enumerable.Range(0, 21))
            foreach (var target in Enumerable.Range(1, 3))
            foreach (var speed in new[] { 0, 255 })
            foreach (var brightness in new[] { 0, 255 })
            {
                project.Layers[3].LightingEffect = new() { Mode = mode, Target = target, Speed = speed, Brightness = brightness, Reverse = true };
                Require(ProjectService.Validate(project).Count == 0, $"Rejected valid lighting settings for {id}, mode {mode}");
            }
        }
    });
    Check("Invalid lighting effects reject imports and saves without changing existing files", () =>
    {
        var mutations = new Action<LayerProfile>[]
        {
            layer => layer.LightingEffect = null!,
            layer => layer.LightingEffect.Mode = -1,
            layer => layer.LightingEffect.Mode = 21,
            layer => layer.LightingEffect.Speed = -1,
            layer => layer.LightingEffect.Speed = 256,
            layer => layer.LightingEffect.Brightness = -1,
            layer => layer.LightingEffect.Brightness = 256,
            layer => layer.LightingEffect.Target = 0,
            layer => layer.LightingEffect.Target = 4
        };
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            var valid = ProjectService.Create(id);
            var file = Path.Combine(temp, id + "-effects-preserved.json");
            ProjectService.Save(file, valid);
            var original = File.ReadAllBytes(file);
            for (var i = 0; i < mutations.Length; i++)
            {
                var invalid = ProjectService.Clone(valid);
                mutations[i](invalid.Layers[2]);
                var input = JsonSerializer.Serialize(invalid, ProjectService.JsonOptions);
                Require(ProjectService.Validate(invalid).Count > 0, $"Invalid effect case {i} was accepted");
                Require(JsonSerializer.Serialize(invalid, ProjectService.JsonOptions) == input, "Validation must not repair or mutate effect settings");
                Rejected(() => ProjectService.Save(file, invalid));
                Require(File.ReadAllBytes(file).SequenceEqual(original), "Rejected effect save overwrote the valid project");
                var badFile = Path.Combine(temp, id + "-bad-effect-" + i + ".json");
                File.WriteAllText(badFile, input);
                Rejected(() => ProjectService.Load(badFile));
                Require(File.ReadAllText(badFile) == input, "Rejected effect import changed its source file");
            }
        }
    });
    Check("Individual LED effects and timing survive save, load and independent clones", () =>
    {
        foreach (var device in DeviceCatalog.All)
        {
            var project = ProjectService.Create(device.Id);
            Require(project.Layers.All(layer => layer.LedEffects.Count == 0));
            Require(project.DeviceSettings is { UsbPollingIntervalMs: 1, DebounceMs: 5 });
            for (var layer = 0; layer < 4; layer++)
            {
                project.Layers[layer].LedEffects[0] = new() { Mode = layer * 5, Speed = layer * 85, Brightness = 255 - layer * 85, Reverse = layer % 2 == 1 };
                project.Layers[layer].LedEffects[device.LedCount - 1] = new() { Mode = 20, Speed = 0, Brightness = 255 };
            }
            project.DeviceSettings = new() { UsbPollingIntervalMs = 8, DebounceMs = 40 };
            var file = Path.Combine(temp, device.Id + "-individual-effects.json");
            ProjectService.Save(file, project);
            var loaded = ProjectService.Load(file);
            var state = JsonSerializer.Serialize(project);
            Require(JsonSerializer.Serialize(loaded) == state);
            var clone = ProjectService.Clone(loaded);
            clone.Layers[0].LedEffects[0].Mode = 11;
            clone.Layers[1].LedEffects.Clear();
            clone.DeviceSettings.UsbPollingIntervalMs = 2;
            Require(JsonSerializer.Serialize(loaded) == state && JsonSerializer.Serialize(project) == state);
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(ProjectService.Create(device.Id), ProjectService.JsonOptions))!;
            json.AsObject().Remove("deviceSettings");
            foreach (var layer in json["layers"]!.AsArray()) layer!.AsObject().Remove("ledEffects");
            File.WriteAllText(file, json.ToJsonString());
            var legacy = ProjectService.Load(file);
            Require(legacy.Layers.All(layer => layer.LedEffects.Count == 0) && legacy.DeviceSettings is { UsbPollingIntervalMs: 1, DebounceMs: 5 });
        }
    });
    Check("Individual LED effects and timing reject invalid imports without data loss", () =>
    {
        foreach (var device in DeviceCatalog.All)
        {
            var valid = ProjectService.Create(device.Id);
            valid.Layers[0].LedEffects[0] = new();
            var file = Path.Combine(temp, device.Id + "-strict-settings.json");
            ProjectService.Save(file, valid);
            var original = File.ReadAllBytes(file);
            foreach (var mutate in new Action<AppProject>[] {
                p => p.Layers[0].LedEffects = null!,
                p => p.Layers[0].LedEffects[-1] = new(),
                p => p.Layers[0].LedEffects[device.LedCount] = new(),
                p => p.Layers[0].LedEffects[0] = null!,
                p => p.Layers[0].LedEffects[0].Mode = -1,
                p => p.Layers[0].LedEffects[0].Mode = 21,
                p => p.Layers[0].LedEffects[0].Mode = 255,
                p => p.Layers[0].LedEffects[0].Speed = -1,
                p => p.Layers[0].LedEffects[0].Speed = 256,
                p => p.Layers[0].LedEffects[0].Brightness = -1,
                p => p.Layers[0].LedEffects[0].Brightness = 256,
                p => p.DeviceSettings = null!,
                p => p.DeviceSettings.UsbPollingIntervalMs = 0,
                p => p.DeviceSettings.UsbPollingIntervalMs = 3,
                p => p.DeviceSettings.UsbPollingIntervalMs = 16,
                p => p.DeviceSettings.DebounceMs = -1,
                p => p.DeviceSettings.DebounceMs = 41
            })
            {
                var invalid = ProjectService.Clone(valid); mutate(invalid);
                Require(ProjectService.Validate(invalid).Count != 0);
                Rejected(() => ProjectService.Save(file, invalid));
                Require(File.ReadAllBytes(file).SequenceEqual(original));
                var badFile = Path.Combine(temp, device.Id + "-invalid-settings.json");
                File.WriteAllText(badFile, JsonSerializer.Serialize(invalid, ProjectService.JsonOptions));
                Rejected(() => ProjectService.Load(badFile));
            }
            foreach (var mode in Enumerable.Range(0, 21))
            foreach (var led in new[] { 0, device.LedCount - 1 })
            {
                valid.Layers[0].LedEffects[led] = new() { Mode = mode, Speed = mode % 2 == 0 ? 0 : 255, Brightness = mode % 2 == 0 ? 255 : 0, Reverse = true };
                Require(ProjectService.Validate(valid).Count == 0);
            }
            foreach (var interval in new[] { 1, 2, 4, 8 }) foreach (var debounce in new[] { 0, 5, 40 })
            {
                valid.DeviceSettings = new() { UsbPollingIntervalMs = interval, DebounceMs = debounce };
                Require(ProjectService.Validate(valid).Count == 0);
            }
        }
    });
    Check("Reject malformed imports and unknown schema fields", () =>
    {
        foreach (var body in new[] { "null", "{}", "{\"schemaVersion\":99}", "{\"deviceId\":\"other\"}", "{\"layers\":null}", "{\"macros\":null}", "{\"hardwareWritesEnabled\":true}" })
        { var file = Path.Combine(temp, "bad.json"); File.WriteAllText(file, body); Rejected(() => ProjectService.Load(file)); }
    });
    Check("Reject cross-device maps, dangling macros, invalid colors and layer injection", () =>
    {
        foreach (var mutate in new Action<AppProject>[] {
            p => p.DeviceId = "numpad",
            p => p.Layers[0].Keys["r1c3"].Binding = "MACRO:missing",
            p => p.Layers[0].Keys["r1c3"].Binding = "MO(4)",
            p => p.Layers[0].Keys["r1c3"].Binding = "MO(1)\n",
            p => p.Layers[0].Keys["r1c3"].Binding = "KC_TRNS",
            p => p.Layers[0].Keys["r1c3"].Color = "#112233\n",
            p => p.Layers[0].Keys["r1c3"].Color = "tomato",
            p => p.Layers[0].Keys["r1c3"] = null!
        }) { var p = ProjectService.Create("pro-iso"); mutate(p); Require(ProjectService.Validate(p).Count > 0); }
    });
    Check("Macros reject stuck keys, orphan releases and excessive duration", () =>
    {
        foreach (var steps in new List<MacroStep>[] {
            [new() { Kind = "down", Key = "KC_A" }],
            [new() { Kind = "up", Key = "KC_A" }],
            [new() { Kind = "delay", DelayMs = 10001 }],
            [new() { Kind = "delay", DelayMs = -1 }],
            [new() { Kind = "launch", Key = "cmd.exe" }],
            Enumerable.Range(0, 4).Select(_ => new MacroStep { Kind = "delay", DelayMs = 10000 }).ToList(),
            [new() { Kind = "down", Key = "KC_A" }, new() { Kind = "down", Key = "KC_A" }, new() { Kind = "up", Key = "KC_A" }]
        }) { var p = ProjectService.Create("pro-iso"); p.Macros.Add(new() { Steps = steps }); Require(ProjectService.Validate(p).Count > 0); }
    });
    Check("Rejected saves preserve the last valid project", () =>
    {
        var p = ProjectService.Create("numpad"); var file = Path.Combine(temp, "atomic.json"); ProjectService.Save(file, p);
        var original = File.ReadAllText(file); p.Layers.Clear(); Rejected(() => ProjectService.Save(file, p));
        Require(File.ReadAllText(file) == original);
    });
    Check("Backup hash verification detects corruption and never unlocks writes", () =>
    {
        var dir = Path.Combine(temp, "backups"); Directory.CreateDirectory(dir);
        var data = new byte[] { 1, 2, 3, 4 }; File.WriteAllBytes(Path.Combine(dir, "firmware.bin"), data);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new { deviceDump = new { performed = true, restorable = true }, artifacts = new[] { new { path = "firmware.bin", bytes = 4, sha256 = Convert.ToHexString(SHA256.HashData(data)) } } }));
        var report = BackupService.Inspect(dir); Require(report.VerifiedCount == 1 && !report.HasVerifiedDeviceDump);
        File.WriteAllBytes(Path.Combine(dir, "firmware.bin"), [1, 2, 3, 5]);
        var corrupted = BackupService.Inspect(dir); Require(corrupted.VerifiedCount == 0 && corrupted.Issues.Any(i => i.Contains("SHA-256")));
    });
    Check("Backup paths reject traversal, absolute paths and NTFS streams", () =>
    {
        foreach (var path in new[] { "../outside.bin", "C:\\outside.bin", "file.bin:secret", "" }) Rejected(() => BackupService.ResolveInside(temp, path));
    });
    Check("Malformed backup sizes are reported instead of crashing inspection", () =>
    {
        var dir = Path.Combine(temp, "malformed-backups"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"artifacts\":[{\"path\":\"firmware.bin\",\"bytes\":1e100,\"sha256\":\"bad\"}]}");
        var report = BackupService.Inspect(dir);
        Require(report.VerifiedCount == 0 && report.FileCount == 1 && !report.HasVerifiedDeviceDump && report.Issues.Any(issue => issue.StartsWith("firmware.bin:", StringComparison.Ordinal)));
    });
    Check("Matching flash copies are counted separately from full recovery", () =>
    {
        var dir = Path.Combine(temp, "flash-capture"); Directory.CreateDirectory(dir);
        var data = new byte[32768]; data[0] = 42;
        var digest = Convert.ToHexString(SHA256.HashData(data));
        File.WriteAllBytes(Path.Combine(dir, "read-1.bin"), data); File.WriteAllBytes(Path.Combine(dir, "read-2.bin"), data);
        var manifest = new { captureStatus = "two-matching-internal-flash-reads-not-restore-tested", range = new { start = "0x08000000", bytes = data.Length }, artifacts = new[] { new { path = "read-1.bin", bytes = data.Length, sha256 = digest }, new { path = "read-2.bin", bytes = data.Length, sha256 = digest } } };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
        var report = BackupService.Inspect(dir);
        Require(report.MatchingInternalFlashCaptures == 1 && !report.HasVerifiedDeviceDump);
        data[4] = 1; File.WriteAllBytes(Path.Combine(dir, "read-2.bin"), data);
        Require(BackupService.Inspect(dir).MatchingInternalFlashCaptures == 0);
    });
    if (args.Contains("--hardware"))
    {
        try
        {
            var devices = await DeviceScanner.ScanAsync();
            Directory.CreateDirectory(Path.Combine(Paths.WorkspaceRoot, "artifacts"));
            var record = new { capturedAt = DateTimeOffset.UtcNow, method = "Windows PnP read-only; no HID reports", devices };
            File.WriteAllText(Path.Combine(Paths.WorkspaceRoot, "artifacts", "hardware-inventory.json"), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var d in devices) Console.WriteLine($"DEVICE {d.Name} {d.VendorId}:{d.ProductId} USB bcdDevice={d.UsbRelease}; {d.RevisionStatus}");
            var report = await BackupService.InspectAsync();
            Console.WriteLine($"BACKUP {report.VerifiedCount}/{report.FileCount} file hashes valid; device dump verified={report.HasVerifiedDeviceDump}");
            if (report.VerifiedCount != report.FileCount || report.FileCount == 0) failures++;
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL live inventory: " + ex.Message); }
    }
}
finally { Directory.Delete(temp, true); }
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} checks failed.");
return failures == 0 ? 0 : 1;
