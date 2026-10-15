using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using InGlorious.Core;

// These tests have no transport, PnP, firmware service, process runner or UI links.
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
string Json(object value) => JsonSerializer.Serialize(value, ProjectService.JsonOptions);
var workspace = new DirectoryInfo(AppContext.BaseDirectory);
while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "reference", "qmk", "sources.json"))) workspace = workspace.Parent;
if (workspace is null) throw new Exception("QMK references not found");
var references = Path.Combine(workspace.FullName, "reference", "qmk");
using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(references, "sources.json")));
var tempParent = Path.Combine(Path.GetTempPath(), "InGlorious.ModelChecks");
var temp = Path.Combine(tempParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
L.SetLanguage("de");
foreach (var model in DeviceCatalog.All)
    Check(DeviceCatalog.ModelGroupName([model.Id]) == model.Name && model.Name.StartsWith("GMMK ", StringComparison.Ordinal), "Complete official model name in single-device setup");
Check(DeviceCatalog.ModelGroupName(["pro-iso", "pro-rev2-iso"]) == "GMMK PRO ISO", "Ambiguous revision uses full product family without inventing its revision");
Check(DeviceCatalog.ModelGroupName(["pro-rev2-iso"]) == "GMMK PRO ISO · Rev. 2", "Identified second revision keeps its model designation");
Reject(() => DeviceCatalog.ModelGroupName([]), "Empty model selection has no invented name");
var namedProfile = ProjectService.Create("pro-iso"); namedProfile.Name = "My profile"; namedProfile.Layers[0].Name = "My layer";
foreach (var language in L.SupportedLanguages)
{
    L.SetLanguage(language.Code);
    var localized = ProjectService.Create("pro-iso");
    Check(localized.Layers[0].Name == L.T("Basis") && localized.Layers[1].Name == L.T("Funktion") && localized.Layers[2].Name == L.Format("Ebene {0}", 2), "New profiles use localized initial layer labels");
    Check(namedProfile.Name == "My profile" && namedProfile.Layers[0].Name == "My layer", "Language changes preserve existing profile and layer names");
}
L.SetLanguage("de");
try
{
    RunNewProfileColorChecks(temp, Check);
    RunProfileImportChecks(temp, Check);
    (string Id, int Keys, int Leds, int Sides, bool Encoder)[] expected = [
        ("pro-iso",84,99,16,true), ("numpad",18,31,14,true),
        ("pro-ansi",83,98,16,true), ("pro-rev2-ansi",83,98,16,true), ("pro-rev2-iso",84,99,16,true),
        ("gmmk2-65-ansi",67,87,20,false), ("gmmk2-65-iso",68,88,20,false),
        ("gmmk2-96-ansi",99,119,20,false), ("gmmk2-96-iso",100,120,20,false),
        ("gmmk3-65-ansi",66,85,20,true), ("gmmk3-65-iso",67,86,20,true),
        ("gmmk3-75-ansi",81,101,20,true), ("gmmk3-75-iso",82,102,20,true),
        ("gmmk3-100-ansi",105,125,20,true), ("gmmk3-100-iso",106,126,20,true),
        ("gmmk3-pro-100-iso",106,126,20,true)
    ];
    Check(DeviceCatalog.All.Select(d => d.Id).Order().SequenceEqual(expected.Select(d => d.Id).Order()), "Sixteen exact protocol and editor model identities");
    Check(DeviceCatalog.All.Count(d => d.IsExperimentalFirmware) == 13 && DeviceCatalog.All.Count(d => d.SupportsOnboardConfiguration) == 16 && BoardDescriptorRegistry.All.Count == 3, "Only the exact hardware-tested PRO variant joins the existing tested models");
    var library = new ProfileLibrary(Path.Combine(temp, "library"));
    foreach (var facts in expected)
    {
        var model = DeviceCatalog.Get(facts.Id);
        // Shared reference geometry does not alias the PRO firmware or USB identity.
        var referenceId = facts.Id == "gmmk3-pro-100-iso" ? "gmmk3-100-iso" : facts.Id;
        using var reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(references, referenceId + ".json")));
        foreach (var suffix in new[] { ".json", "-default.c" })
        {
            var entry = manifest.RootElement.EnumerateArray().Single(e => e.GetProperty("file").GetString() == referenceId + suffix);
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(references, referenceId + suffix)))) == entry.GetProperty("sha256").GetString(), "External oracle hash: " + referenceId + suffix);
        }
        var root = reference.RootElement;
        var layout = root.GetProperty("layouts").EnumerateObject().Single();
        var rawKeys = layout.Value.GetProperty("layout").EnumerateArray().ToArray();
        var rawLeds = root.GetProperty("rgb_matrix").GetProperty("layout").EnumerateArray().ToArray();
        var upstreamDefaults = BaseArguments(File.ReadAllText(Path.Combine(references, referenceId + "-default.c")), layout.Name);
        Check(rawKeys.Length == facts.Keys && rawLeds.Length == facts.Leds, "Physical oracle dimensions " + facts.Id);
        Check(model.Keys.Count == facts.Keys + (facts.Encoder ? 2 : 0) && model.LedCount == facts.Leds, "No phantom keys/LEDs " + facts.Id);
        Check(model.HasEncoder == facts.Encoder && model.HasSlider == (facts.Id == "numpad"), "Physical controls " + facts.Id);
        Check(upstreamDefaults.Count == facts.Keys, "Upstream base-keymap length " + facts.Id);
        for (var i = 0; i < rawKeys.Length; i++)
        {
            var key = rawKeys[i]; var matrix = key.GetProperty("matrix");
            var id = $"r{matrix[0]}c{matrix[1]}";
            var actual = model.Keys.Single(k => k.Id == id);
            var binding = upstreamDefaults[i] switch { "QK_GESC" => "KC_ESC", "MO(_FL)" => "MO(1)", var literal => literal };
            Check(actual.DefaultBinding == binding, $"Upstream physical key binding {facts.Id}/{id}: expected {binding}, got {actual.DefaultBinding}");
            Check(actual.X == key.GetProperty("x").GetDouble() && actual.Y == key.GetProperty("y").GetDouble() &&
                actual.W == (key.TryGetProperty("w", out var w) ? w.GetDouble() : 1) && actual.H == (key.TryGetProperty("h", out var h) ? h.GetDouble() : 1), "Geometry from upstream " + facts.Id + "/" + id);
            var ledIndices = rawLeds.Select((led, index) => (led, index)).Where(pair => pair.led.TryGetProperty("matrix", out var lm) && lm[0].GetInt32() == matrix[0].GetInt32() && lm[1].GetInt32() == matrix[1].GetInt32()).Select(pair => pair.index).ToArray();
            Check(actual.LedIndex == (ledIndices.Length == 0 ? (int?)null : ledIndices.Single()), "Independent physical LED index " + facts.Id + "/" + id);
        }
        var nonKeyOracle = rawLeds.Select((led, index) => (led, index)).Where(pair => !pair.led.TryGetProperty("matrix", out _)).ToArray();
        var sideOracle = nonKeyOracle.Where(pair => (pair.led.GetProperty("flags").GetInt32() & 2) != 0).ToArray();
        var badgeOracle = nonKeyOracle.Where(pair => (pair.led.GetProperty("flags").GetInt32() & 8) != 0).ToArray();
        Check(model.NonKeyLedIndices.SequenceEqual(nonKeyOracle.Select(p => p.index)) && model.SideLedIndices.Count == facts.Sides, "Every non-key and side LED included separately " + facts.Id);
        Check(model.BadgeLedIndices.SequenceEqual(badgeOracle.Select(p => p.index)) && !model.BadgeLedIndices.Intersect(model.SideLedIndices).Any(), "Badge never enters side-sync pairing " + facts.Id);
        if (model.BadgeGeometry is { } badge)
        {
            Check(facts.Id.StartsWith("gmmk3-", StringComparison.Ordinal), "Only reviewed physical badge housings are added " + facts.Id);
            Check(badge.W > 0 && badge.H > 0 && badge.X >= 0 && badge.Y + model.PreviewTopInset >= 0 && badge.X + badge.W <= model.PreviewWidth && badge.Y + badge.H + model.PreviewTopInset <= model.PreviewHeight, "Complete badge fits preview, including upper housing and right bezel " + facts.Id);
            Check(model.Keys.All(k => Math.Min(k.X + k.W, badge.X + badge.W) <= Math.Max(k.X, badge.X) || Math.Min(k.Y + k.H, badge.Y + badge.H) <= Math.Max(k.Y, badge.Y)), "Badge does not cover a key, rotary knob or rotation control " + facts.Id);
            var compact = facts.Id.StartsWith("gmmk3-65-", StringComparison.Ordinal);
            Check(badge.IsHorizontal == compact && badge.LedIndex.HasValue == !compact, "65% physical badge does not invent an addressable LED " + facts.Id);
            if (badge.LedIndex is { } badgeIndex) Check(model.BadgeLedIndices.SequenceEqual(new[] { badgeIndex }), "Physical badge maps only its own protocol LED " + facts.Id);
            Check(badge.Y < model.KeyAreaHeight && (compact ? badge.X > 15 && badge.Y == 1 : badge.Y < 0), "Badge stays in the photographed housing position, not the bottom control row " + facts.Id);
        }
        else Check(!facts.Id.StartsWith("gmmk3-", StringComparison.Ordinal) && model.PreviewWidth >= model.Width && model.PreviewHeight >= model.Height, "Preview contains the physical keyboard " + facts.Id);
        if (model.HasEncoder)
        {
            var knob = model.Keys.Single(key => key.Label == "Knob");
            var turns = model.Keys.Where(key => key.IsEncoder).ToArray();
            Check(turns.All(key => key.Y + model.PreviewTopInset >= 0 && key.X + key.W <= model.PreviewWidth && key.Y + key.H < knob.Y && knob.Y - key.Y < 1 && Math.Abs(key.X - knob.X) < 1), "Rotation controls stay visible beside their own knob " + facts.Id);
            Check(turns.All(key => key.LedIndex is null && key.DefaultBinding == (key.Id == "encoder:ccw" ? "KC_VOLD" : "KC_VOLU")), "Rotation controls preserve bindings and do not invent LEDs " + facts.Id);
        }
        var rightX = sideOracle.Max(p => p.led.GetProperty("x").GetInt32());
        foreach (var (x, side) in new[] { (0, model.LeftSideLedIndices), (rightX, model.RightSideLedIndices) })
            Check(side.SequenceEqual(sideOracle.Where(p => p.led.GetProperty("x").GetInt32() == x).OrderBy(p => p.led.GetProperty("y").GetInt32()).Select(p => p.index)), "Independent side ordering " + facts.Id);
        if (facts.Id.StartsWith("gmmk3-", StringComparison.Ordinal))
        {
            var physical = model.Keys.Where(key => !key.IsEncoder).ToArray();
            var overlaps = physical.SelectMany((a, at) => physical.Skip(at + 1).Where(b =>
                Math.Min(a.X + a.W, b.X + b.W) > Math.Max(a.X, b.X) &&
                Math.Min(a.Y + a.H, b.Y + b.H) > Math.Max(a.Y, b.Y)).Select(b => a.Id + "/" + b.Id)).ToArray();
            Check(overlaps.Length == 0, "Physical GMMK3 keys never overlap: " + facts.Id + " " + string.Join(",", overlaps));
        }
        var previewLayout = LightingPreviewEngine.Layout(model);
        for (var led = 0; led < facts.Leds; led++)
            Check(previewLayout[led].X == rawLeds[led].GetProperty("x").GetByte() && previewLayout[led].Y == rawLeds[led].GetProperty("y").GetByte(), "Preview uses physical rather than matrix coordinates " + facts.Id);

        RunGlobalEffectSwitchChecks(model, Check);
        var project = ProjectService.Create(facts.Id);
        OnboardSerializer.ValidateEditorProject(project);
        Check(project.Layers.Count == 4 && project.Layers.All(l => l.Keys.Count == model.Keys.Count), "Four complete editable layers " + facts.Id);
        var keys = model.Keys.Where(k => k.LedIndex.HasValue).Take(4).ToArray();
        for (var layer = 0; layer < 4; layer++)
        {
            project.Layers[layer].Name = $"Anwender {layer}";
            project.Layers[layer].Keys[keys[0].Id].Color = "#FFD447";
            project.Layers[layer].Keys[keys[1].Id].Binding = $"MO({layer})";
            project.Layers[layer].Keys[keys[2].Id].Binding = $"TG({layer})";
            project.Layers[layer].Keys[keys[3].Id].Binding = $"TO({layer})";
            project.Layers[layer].SynchronizeSides = true;
            project.Layers[layer].LightingEffect = new() { Mode=2, Speed=164, Brightness=240, Target=3, Colors=["#46C7F4", "#FFD447"] };
            foreach (var led in model.NonKeyLedIndices) project.Layers[layer].ExtraLedColors[led] = "#46C7F4";
            project.Layers[layer].LedEffects[keys[0].LedIndex!.Value] = new() { Mode=18, Speed=90, Brightness=170, Reverse=true, Colors=["#FF0000"] };
        }
        project.Standby = new() { Enabled=true, TimeoutSeconds=300, Mode=2 };
        if (model.HasSlider) project.Slider.Mode = 3;
        foreach (var system in new[] { "windows", "macos", "linux" })
        {
            var action = OsActionCatalog.For(system, "en").Single(a => a.Id == "copy");
            var macro = OsActionCatalog.CreateMacro(action); project.Macros.Add(macro);
            var expectedModifier = system == "macos" ? "KC_LGUI" : "KC_LCTL";
            Check(macro.Steps.Select(s => (s.Kind,s.Key)).Where(s => s.Kind != "delay").SequenceEqual(new[] {("down",expectedModifier),("down","KC_C"),("up","KC_C"),("up",expectedModifier)}), "OS copy chord has explicit balanced events");
        }
        project.Layers[0].Keys[keys[0].Id].Binding = "MACRO:" + project.Macros[0].Id;
        OnboardSerializer.ValidateEditorProject(project);
        var before = Json(project); var clone = ProjectService.Clone(project);
        Check(Json(clone) == before && !ReferenceEquals(clone.Layers[0], project.Layers[0]), "Detached whole-profile round trip " + facts.Id);
        var path = library.SuggestPath(facts.Id);
        ProjectService.Save(path, project); var savedBytes = File.ReadAllBytes(path);
        Check(Json(ProjectService.Load(path)) == before && File.ReadAllBytes(path).SequenceEqual(savedBytes), "Disk profile read preserves data and file " + facts.Id);
        library.Remember(path, facts.Id);
        Check(ProfileLibrary.PathsEqual(library.ActivePath(facts.Id), path), "Each model has its own active profile path");
        var prefsPath = Path.Combine(temp, facts.Id + "-settings.json");
        AppPreferencesStore.Save(new() { StartDevice=facts.Id, LastDeviceId=facts.Id }, prefsPath);
        Check(AppPreferencesStore.Load(prefsPath).Preferences.ResolveStartDevice() == facts.Id, "All editor models can be selected at startup");

        var macroFile = Path.Combine(temp, facts.Id + ".macros.json");
        SharedMacroLibrary.ExportFile(macroFile, project.Macros.Select(SharedMacroLibrary.FromProfile));
        var imported = SharedMacroLibrary.CopyIntoProfile(ProjectService.Create(facts.Id), SharedMacroLibrary.ImportFile(macroFile));
        Check(imported.Project.Macros.Count == 3 && imported.AddedIds.Distinct().Count() == 3, "Portable macro import for all models");
        Check(Json(imported.Project.Macros.Select(m => m.Steps)) == Json(project.Macros.Select(m => m.Steps)), "Macro import preserves events");
        OnboardSerializer.ValidateEditorProject(imported.Project);
        var preset = LightingPresetStore.Capture(project, 0, "Meine Farben");
        var presetPath = Path.Combine(temp, facts.Id + ".lighting.json");
        LightingPresetStore.Export(presetPath, preset);
        var applied = LightingPresetStore.Apply(ProjectService.Create(facts.Id), 2, LightingPresetStore.Load(presetPath));
        Check(applied.Layers[2].LightingEffect.Colors.SequenceEqual(["#46C7F4", "#FFD447"]) && applied.Layers[2].LedEffects.Count == 1, "Lighting preset round trip preserves sequence and override");
        Check(applied.Macros.Count == 0 && applied.Layers[2].Keys.Values.All(k => k.Binding == "KC_TRNS"), "Lighting preset never copies macro/key actions");
        var branded = LightingPresetStore.Apply(ProjectService.Create(facts.Id), 0, LightingPresetStore.CreateInGlorious(facts.Id));
        OnboardSerializer.ValidateEditorProject(branded);
        var redFrame = new PreviewRgb[facts.Leds]; LightingPreviewEngine.CreateScene(model, branded.Layers[0]).Render(1500, [], redFrame);
        Check(redFrame.All(c => c.R > 0 && c.G == 0 && c.B == 0), "Built-in red preset covers all physical and side LEDs " + facts.Id);

        // Every mode must survive profile save/restore and produce the same local frame.
        for (var mode = 0; mode <= 20; mode++)
        {
            var effectProject = ProjectService.Create(facts.Id); var layer = effectProject.Layers[0];
            layer.SynchronizeSides = true; layer.LightingEffect = new() { Mode=mode, Speed=191, Brightness=203, Reverse=true };
            OnboardSerializer.ValidateEditorProject(effectProject);
            var scene = LightingPreviewEngine.CreateScene(model, layer);
            var copy = LightingPreviewEngine.CreateScene(model, ProjectService.Clone(effectProject).Layers[0]);
            PreviewHit[] hits = [new(999, previewLayout[0].X, previewLayout[0].Y, 0)];
            var frame = new PreviewRgb[facts.Leds]; var frameCopy = new PreviewRgb[facts.Leds];
            scene.Render(1200, hits, frame); copy.Render(1200, hits, frameCopy);
            Check(frame.SequenceEqual(frameCopy), $"Effect {mode} round trip for {facts.Id}");
            foreach (var (left,right) in model.LeftSideLedIndices.Zip(model.RightSideLedIndices))
                Check(frame[left] == frame[right], $"Synchronized side phase mode {mode} for {facts.Id}");
        }
        if (model.IsOfflineOnly) Reject(() => OnboardSerializer.Prepare(project), "Engineering editor cannot create a transfer payload");
        else
        {
            var image = OnboardSerializer.Prepare(project); var decoded = OnboardSerializer.Decode(image.Payload);
            Check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(image.Payload), "Every active model encodes/decodes all configured data");
            Check(image.SchemaVersion == 4 && image.Payload[6] == BoardDescriptorRegistry.Get(facts.Id).BoardId, "Every active model has an explicit wire ID");
        }
        Check(model.HasApprovedFirmwareManagement == BoardDescriptorRegistry.All.Any(board => board.DeviceId == facts.Id), "Codec support never invents firmware-management approval");
        var invalid = ProjectService.Clone(project); invalid.Layers[0].LedEffects[facts.Leds] = new();
        Reject(() => OnboardSerializer.ValidateEditorProject(invalid), "Out-of-layout LED rejected for every model");
        invalid = ProjectService.Clone(project); invalid.DeviceId = "not-a-board";
        Reject(() => OnboardSerializer.ValidateEditorProject(invalid), "Unknown profile never acquires editor support");
    }
    var profiles = library.Discover(out var warning);
    Check(warning is null && profiles.Count == 16 && profiles.Select(p => p.DeviceId).Distinct().Count() == 16, "Profile library retains every distinct model");
    Check(BoardDescriptorRegistry.All.Select(b => b.DeviceId).Order().SequenceEqual(new[]{"gmmk3-pro-100-iso","numpad","pro-iso"}), "Profile operations cannot change the three exact hardware-tested models");
    Console.WriteLine($"PASS {checks} model/reference/profile/effects/macro/OS/offline-gate checks across sixteen models. No hardware APIs or firmware tools are linked.");
}
finally
{
    var full = Path.GetFullPath(temp); var parent = Path.GetFullPath(tempParent) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full).Length != 32) throw new Exception("Unsafe temporary cleanup path");
    if (Directory.Exists(full)) Directory.Delete(full, true);
}

static void RunProfileImportChecks(string root, Action<bool, string> check)
{
    var folder = Path.Combine(root, "profile-imports"); Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "profile.json");
    var project = ProjectService.Create("numpad");
    var valid = JsonSerializer.Serialize(project, ProjectService.JsonOptions);
    void RejectFile(string body)
    {
        File.WriteAllText(path, body); var before = File.ReadAllBytes(path); var rejected = false;
        try { ProjectService.Load(path); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { rejected = true; }
        check(rejected && before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "ambiguous or oversized profile import is rejected without modifying its file");
    }
    RejectFile(valid.Replace("\"deviceId\": \"numpad\"", "\"deviceId\": \"pro-iso\", \"deviceId\": \"numpad\""));
    RejectFile(valid.Replace("\"binding\": \"MO(1)\"", "\"binding\": \"KC_A\", \"binding\": \"MO(1)\""));
    RejectFile(valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99, \"schema\\u0056ersion\": 1"));
    RejectFile(new string(' ', ProjectService.MaximumFileBytes + 1));
    RejectFile(new string('[', 17) + "0" + new string(']', 17));
    foreach (var encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(false), new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode })
    {
        File.WriteAllText(path, valid, encoding);
        check(JsonSerializer.Serialize(ProjectService.Load(path), ProjectService.JsonOptions) == valid, "existing valid JSON encoding preserves the whole profile");
    }
    var library = new ProfileLibrary(folder);
    File.WriteAllText(path, valid + new string(' ', 1_100_000));
    check(library.Discover(out var warning).Count == 1 && warning is null, "profile discovery accepts the same bounded file size as explicit opening");
    ProjectService.Save(path, project); var saved = File.ReadAllBytes(path);
    // Delay keys are unused payload text: even an otherwise valid model cannot produce an unreadable file.
    project.Macros.Add(new() { Steps = [new() { Kind = "delay", DelayMs = 1, Key = new string('x', ProjectService.MaximumFileBytes) }] });
    var refused = false;
    try { ProjectService.Save(path, project); } catch (InvalidDataException) { refused = true; }
    check(refused && saved.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "oversized save preserves the previous readable profile");
    check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "rejected save leaves no temporary files");
    var index = Path.Combine(folder, "profiles-index.json");
    foreach (var malformed in new[] { "{\"Version\":2,\"Version\":1,\"Recent\":[],\"Active\":{}}", new string(' ', 262145) })
    {
        File.WriteAllText(index, malformed); var indexBefore = File.ReadAllBytes(index);
        check(library.Discover(out warning).Count == 1 && warning is not null, "damaged recent-file list is reported while valid local profiles remain visible");
        refused = false;
        try { library.Remember(path, "numpad"); } catch (Exception ex) when (ex is JsonException or InvalidDataException) { refused = true; }
        check(refused && indexBefore.AsSpan().SequenceEqual(File.ReadAllBytes(index)), "remember never overwrites a damaged or ambiguous recent-file list");
    }
    // Every component fits common filesystem limits, while the complete recent list
    // can exceed the index's byte limit before reaching its 100-entry limit.
    var recent = new List<string>();
    var active = new Dictionary<string, string> { ["numpad"] = path, ["pro-iso"] = Path.Combine(folder, "pro.json") };
    byte[] IndexBytes() => JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Recent = recent, Active = active });
    string LongPath(int number) => Path.Combine([folder, .. Enumerable.Repeat(new string('x', 200), 13), "profile-" + number + ".json"]);
    byte[] indexBeforeLimit;
    string nextPath;
    while (true)
    {
        indexBeforeLimit = IndexBytes(); nextPath = LongPath(recent.Count);
        recent.Add(nextPath);
        if (IndexBytes().Length > 262144) { recent.RemoveAt(recent.Count - 1); break; }
    }
    check(recent.Count < 100 && Path.GetFullPath(nextPath) == nextPath, "valid long profile paths reach the byte limit before the recent-entry cap");
    File.WriteAllBytes(index, indexBeforeLimit);
    check(library.ActivePath("numpad") == path && library.ActivePath("pro-iso") == active["pro-iso"], "large but bounded index remains readable");
    refused = false;
    try { library.Remember(nextPath, "numpad"); } catch (InvalidDataException) { refused = true; }
    check(refused && indexBeforeLimit.AsSpan().SequenceEqual(File.ReadAllBytes(index)), "oversized recent-list update preserves its previous readable bytes");
    check(library.ActivePath("numpad") == path && library.ActivePath("pro-iso") == active["pro-iso"], "rejected recent-list update retains every active profile");
    check(!Directory.EnumerateFiles(folder, "*.tmp-*").Any(), "rejected recent-list update leaves no temporary file");
    if (!OperatingSystem.IsWindows())
    {
        var upper = Path.Combine(root, "PresetLibrary"); var lower = Path.Combine(root, "presetlibrary");
        Directory.CreateDirectory(upper);
        if (!Directory.Exists(lower))
        {
            var entry = LightingPresetStore.SaveToLibrary(LightingPresetStore.CreateInGlorious("numpad"), lower);
            var original = File.ReadAllBytes(entry.Path); refused = false;
            try { LightingPresetStore.Delete(entry, upper); } catch (InvalidDataException) { refused = true; }
            check(refused && original.AsSpan().SequenceEqual(File.ReadAllBytes(entry.Path)), "case-sensitive sibling preset library is outside the deletion boundary");
        }
    }
}

static void RunNewProfileColorChecks(string temp, Action<bool, string> check)
{
    string Json(object value) => JsonSerializer.Serialize(value, ProjectService.JsonOptions);
    string[] savedColors = ["#8374F8", "#668BFA", "#49BDCA", "#DB86BC"];
    foreach (var device in DeviceCatalog.All)
    {
        var project = ProjectService.Create(device.Id);
        check(project.Layers.Count == 4 && project.Layers.All(layer => layer.SideLedColor == "#FF0000" &&
            layer.Keys.Values.All(key => key.Color == "#FF0000") && device.NonKeyLedIndices.All(led =>
                layer.ExtraLedColors.GetValueOrDefault(led, layer.SideLedColor) == "#FF0000")),
            "Every new layer starts with pure red keys, sides and logo " + device.Id);
        if (device.SupportsOnboardConfiguration)
        {
            var image = OnboardSerializer.Prepare(project);
            var shape = OnboardSerializer.Shape(device.Id);
            var rgbOffset = 32 + 8 * shape.Rows * shape.Cols + 16;
            check(Enumerable.Range(0, 4 * shape.Leds).All(led => image.Payload.AsSpan(rgbOffset + led * 3, 3).SequenceEqual(new byte[] { 255, 0, 0 })),
                "New profile encodes pure red for every physical LED on every layer " + device.Id);
        }
        foreach (var layer in project.Layers)
        {
            var index = 0;
            foreach (var key in layer.Keys.Values) key.Color = savedColors[index++ % savedColors.Length];
            layer.SideLedColor = "#8374F8";
            foreach (var led in device.NonKeyLedIndices) layer.ExtraLedColors[led] = led % 2 == 0 ? "#123456" : "#ABCDEF";
        }
        var file = Path.Combine(temp, device.Id + "-saved-colors.json");
        ProjectService.Save(file, project);
        var bytes = File.ReadAllBytes(file); var before = Json(project);
        var loaded = ProjectService.Load(file);
        check(Json(loaded) == before && Json(ProjectService.Clone(loaded)) == before && File.ReadAllBytes(file).SequenceEqual(bytes),
            "Loading and cloning preserve saved multicolor profiles and source files " + device.Id);
        if (device.SupportsOnboardConfiguration)
        {
            var payload = OnboardSerializer.Prepare(loaded).Payload;
            check(OnboardSerializer.Prepare(OnboardSerializer.Decode(payload)).Payload.SequenceEqual(payload),
                "Device decoding preserves existing colors instead of applying new-profile red " + device.Id);
        }
    }
    var legacy = System.Text.Json.Nodes.JsonNode.Parse(Json(ProjectService.Create("pro-iso")))!;
    foreach (var layer in legacy["layers"]!.AsArray())
    {
        layer!.AsObject().Remove("sideLedColor");
        foreach (var key in layer["keys"]!.AsObject()) key.Value!.AsObject().Remove("color");
    }
    var legacyFile = Path.Combine(temp, "omitted-legacy-colors.json");
    File.WriteAllText(legacyFile, legacy.ToJsonString());
    var legacyBytes = File.ReadAllBytes(legacyFile);
    var legacyLoaded = ProjectService.Load(legacyFile);
    check(legacyLoaded.Layers.All(layer => layer.SideLedColor == "#8374F8" && layer.Keys.Values.All(key => key.Color == "#7367F0")) &&
        File.ReadAllBytes(legacyFile).SequenceEqual(legacyBytes), "Older files with omitted colors retain their original fallback colors without file changes");
}

static void RunGlobalEffectSwitchChecks(DeviceDefinition device, Action<bool, string> check)
{
    static string Json(object value) => JsonSerializer.Serialize(value, ProjectService.JsonOptions);
    var project = ProjectService.Create(device.Id);
    var layer = project.Layers[0]; var keys = device.Keys.Where(key => key.LedIndex.HasValue).Take(3).ToArray();
    layer.Keys[keys[0].Id].Color = "#000000";
    layer.Keys[keys[1].Id].Color = "#24B860";
    layer.SideLedColor = "#001133";
    layer.SynchronizeSides = true;
    layer.LedEffects[keys[2].LedIndex!.Value] = new() { Mode = 2, Speed = 137, Brightness = 179, Reverse = true, Colors = ["#FF0000", "#00FFFF"] };
    layer.LedEffects[device.LeftSideLedIndices[0]] = new() { Mode = 18, Speed = 87, Brightness = 163, Colors = ["#ABCDEF"] };
    var colors = Json(new { layer.Keys, layer.SideLedColor, layer.ExtraLedColors });
    var individual = Json(layer.LedEffects); var otherLayers = Json(project.Layers.Skip(1));
    foreach (var target in new[] { 1, 2, 3 })
    {
        var original = new LightingEffectSettings { Mode = 2, Speed = 71, Brightness = 123, Target = target, Reverse = true, Colors = ["#FF0000", "#550000"] };
        var originalJson = Json(original); var disabled = GlobalEffectSwitch.Disable(original);
        check(GlobalEffectSwitch.IsEnabled(original) && !GlobalEffectSwitch.IsEnabled(disabled), "Global switch distinguishes active effect from static full brightness: " + device.Id);
        check(disabled.Mode == 0 && disabled.Brightness == 255 && disabled.Speed == 71 && disabled.Target == target && disabled.Reverse && disabled.Colors.SequenceEqual(original.Colors), "Off retains target, speed, direction and palette in the existing device format");
        check(Json(original) == originalJson && !ReferenceEquals(disabled, original) && !ReferenceEquals(disabled.Colors, original.Colors), "Disabling does not mutate or share source settings");
        var enabled = GlobalEffectSwitch.Enable(disabled);
        check(enabled.Mode == 1 && enabled.Brightness == 255 && enabled.Speed == disabled.Speed && enabled.Target == target && enabled.Reverse && enabled.Colors.SequenceEqual(disabled.Colors), "Enabling an applied off state starts breathing with retained settings");
        var enabledCopy = GlobalEffectSwitch.Enable(original);
        check(Json(enabledCopy) == originalJson && !ReferenceEquals(enabledCopy, original) && !ReferenceEquals(enabledCopy.Colors, original.Colors), "Enabling an already active effect preserves its exact settings in a detached copy");
        enabled.Colors[0] = "#FFFFFF";
        check(disabled.Colors[0] == "#FF0000" && Json(original) == originalJson, "Enabled copy palette changes cannot leak into off or original settings");
        layer.LightingEffect = disabled;
        check(Json(new { layer.Keys, layer.SideLedColor, layer.ExtraLedColors }) == colors && Json(layer.LedEffects) == individual && Json(project.Layers.Skip(1)) == otherLayers && layer.SynchronizeSides,
            "Turning off preserves all base colors, individual effects, other layers and side synchronization");
        var scene = LightingPreviewEngine.CreateScene(device, layer); var frame = new PreviewRgb[device.LedCount];
        foreach (uint time in new[] { 0u, 800u, 1700u })
        {
            scene.Render(time, [], frame);
            foreach (var led in scene.Leds)
            {
                var basis = PreviewRgb.FromHex(led.KeyId is not null ? layer.Keys[led.KeyId].Color : layer.ExtraLedColors.GetValueOrDefault(led.Index, layer.SideLedColor));
                var expected = layer.LedEffects.TryGetValue(led.Index, out var own)
                    ? LightingPreviewEngine.Pixel(LightingPreviewEngine.SelectedEffect(own, true), basis, led, time, []) : basis;
                check(frame[led.Index] == expected, "Off renders exact stored base colors or the unchanged individual effect for every target: " + device.Id);
            }
            check(frame[keys[0].LedIndex!.Value] == default && frame[keys[1].LedIndex!.Value] == new PreviewRgb(36, 184, 96), "Black remains unlit and a stored color remains static at full brightness");
        }
        if (!device.IsOfflineOnly)
        {
            var payload = OnboardSerializer.Prepare(project).Payload; var decoded = OnboardSerializer.Decode(payload);
            check(!GlobalEffectSwitch.IsEnabled(decoded.Layers[0].LightingEffect) && Json(decoded.Layers[0].LightingEffect) == Json(disabled), "Off and all retained effect parameters survive the device codec");
            check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(payload), "Off configuration is byte-exact after device read and re-encoding");
        }
        var preset = LightingPresetStore.Capture(project, 0, "Static colors");
        var applied = LightingPresetStore.Apply(ProjectService.Create(device.Id), 2, preset);
        check(!GlobalEffectSwitch.IsEnabled(applied.Layers[2].LightingEffect) && Json(applied.Layers[2].LedEffects) == individual, "Lighting presets preserve off and individual effects");
    }
    check(GlobalEffectSwitch.IsEnabled(new() { Mode = 0, Brightness = 0 }) && GlobalEffectSwitch.IsEnabled(new() { Mode = 0, Brightness = 254 }), "An intentional static dimmer is an active global setting, not discarded as off");
}

static List<string> BaseArguments(string source, string layout)
{
    source = Regex.Replace(source, @"/\*.*?\*/|//[^\r\n]*", "", RegexOptions.Singleline);
    var startMatch = Regex.Match(source, @"\b" + Regex.Escape(layout) + @"\s*\(");
    if (!startMatch.Success) throw new Exception("No upstream base layout");
    var result = new List<string>(); var start = startMatch.Index + startMatch.Length; var depth = 1;
    for (var i = start; i < source.Length; i++)
    {
        if (source[i] == '(') depth++;
        else if (source[i] == ')' && --depth == 0) { result.Add(source[start..i].Trim()); return result; }
        else if (source[i] == ',' && depth == 1) { result.Add(source[start..i].Trim()); start = i + 1; }
    }
    throw new Exception("Unterminated upstream base layout");
}
