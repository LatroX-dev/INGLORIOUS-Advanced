using System.Text;
using System.Text.Json;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
void Reject(Action action, string name) { checks++; try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException or IOException) { return; } throw new Exception("Accepted: " + name); }
SharedMacro Tap(string name = "Shared {0} #ABCDEF", string key = "KC_A") => new() { Name = name, Steps = [new() { Kind = "down", Key = key, DelayMs = 0 }, new() { Kind = "up", Key = key, DelayMs = 0 }] };
string Serialize(object value) => JsonSerializer.Serialize(value, ProjectService.JsonOptions);
byte[] Document(string macros, string version = "1") => Encoding.UTF8.GetBytes("{\"format\":\"inglorious-macros\",\"version\":" + version + ",\"macros\":" + macros + "}");
var root = Path.Combine(Path.GetTempPath(), "INGLORIOUS-MacroChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var path = Path.Combine(root, "library.macros.json");
    var missing = SharedMacroLibrary.Load(path);
    Check(!File.Exists(path) && missing.Macros.Count == 0, "Read does not create library");
    var first = Tap(); var saved = SharedMacroLibrary.Save(missing, [first]); first.Steps[0].Key = "KC_B";
    Check(saved.Macros[0].Steps[0].Key == "KC_A", "Save detached copy");
    Check(SharedMacroLibrary.Load(path).Macros[0].Name == "Shared {0} #ABCDEF", "Literal user text preserved");
    var originalBytes = File.ReadAllBytes(path);
    var stale = SharedMacroLibrary.Load(path);
    var second = Tap("Second", "KC_B");
    var fresh = SharedMacroLibrary.Save(saved, SharedMacroLibrary.Merge(saved.Macros, [second]));
    Reject(() => SharedMacroLibrary.Save(stale, []), "Stale writer rejected");
    Check(SharedMacroLibrary.Load(path).Macros.Count == 2, "Concurrent content retained");
    Reject(() => SharedMacroLibrary.Save(missing, []), "Missing snapshot cannot overwrite new library");
    var merge = SharedMacroLibrary.Merge(fresh.Macros, [Tap(), second]);
    Check(merge.Count == 2, "Exact duplicates skipped");
    merge[0].Steps[0].Key = "KC_C";
    Check(fresh.Macros[0].Steps[0].Key == "KC_A", "Merge detached copy");
    var export = Path.Combine(root, "portable.macros.json");
    SharedMacroLibrary.ExportFile(export, fresh.Macros);
    var portableText = File.ReadAllText(export);
    Check(!portableText.Contains("deviceId") && !portableText.Contains("binding") && !portableText.Contains("\"id\"") && !portableText.Contains(root), "Portable file omits device, bindings, IDs and paths");
    Check(SharedMacroLibrary.ImportFile(export).Count == 2, "Portable roundtrip");
    var libraryBeforeExport = File.ReadAllBytes(path);
    foreach (var destination in new[] { path, Path.Combine(root, ".", Path.GetFileName(path)) })
    {
        Reject(() => SharedMacroLibrary.ExportFile(destination, [second], overwrite: true, library: fresh), "Subset export cannot overwrite its source library");
        Check(libraryBeforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Rejected subset export preserves every library macro");
    }
    if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
    {
        Reject(() => SharedMacroLibrary.ExportFile(path.ToUpperInvariant(), [second], overwrite: true, library: fresh), "Case variant cannot replace the central library");
        Check(libraryBeforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Case-variant rejection preserves all macros");
    }
    if (!OperatingSystem.IsWindows())
    {
        var linkedFolder = Path.Combine(root, "library-link");
        Directory.CreateSymbolicLink(linkedFolder, root);
        try
        {
            Reject(() => SharedMacroLibrary.ExportFile(Path.Combine(linkedFolder, Path.GetFileName(path)), [second], overwrite: true, library: fresh), "Linked folder cannot bypass library protection");
            Check(libraryBeforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Linked export preserves the full source library");
        }
        finally { Directory.Delete(linkedFolder); }
    }
    Reject(() => SharedMacroLibrary.ExportFile(SharedMacroLibrary.DefaultPath, [second], overwrite: true), "Default central library is protected without a dialog snapshot");
    Reject(() => SharedMacroLibrary.ExportFile(export, [second]), "Export overwrite requires opt-in");
    Check(SharedMacroLibrary.ImportFile(export).Count == 2, "Rejected export preserves bytes");
    SharedMacroLibrary.ExportFile(export, [second], overwrite: true);
    Check(SharedMacroLibrary.ImportFile(export).Count == 1, "Explicit overwrite works");
    Reject(() => SharedMacroLibrary.ExportFile(Path.Combine(root, "empty.json"), []), "Empty export rejected");

    foreach (var device in DeviceCatalog.All)
    {
        var profile = ProjectService.Create(device.Id); profile.Macros.Add(new() { Id = "existing", Name = Tap().Name, Steps = Tap().Steps });
        profile.Layers[0].Keys[device.Keys[0].Id].Binding = "MACRO:existing";
        var before = Serialize(profile);
        var copied = SharedMacroLibrary.CopyIntoProfile(profile, [Tap(), Tap()]);
        Check(before == Serialize(profile), "Source profile untouched " + device.Id);
        Check(copied.Project.Macros.Count == 3 && copied.AddedIds.Distinct().Count() == 2 && !copied.AddedIds.Contains("existing"), "Fresh non-conflicting IDs");
        Check(copied.Project.Macros[1].Name.EndsWith(" (2)") && copied.Project.Macros[2].Name.EndsWith(" (3)"), "Name conflicts explicitly suffixed");
        Check(copied.Project.Layers[0].Keys[device.Keys[0].Id].Binding == "MACRO:existing", "Existing binding preserved");
        Check(Serialize(copied.Project.Layers) == Serialize(profile.Layers), "All layer data preserved");
        Check(!copied.Project.Layers.SelectMany(l => l.Keys.Values).Any(k => copied.AddedIds.Any(id => k.Binding == "MACRO:" + id)), "No new automatic assignment");
        OnboardSerializer.ValidateEditorProject(copied.Project); checks++;
        if (device.IsOfflineOnly) Reject(() => OnboardSerializer.Prepare(copied.Project), "Offline macro edits cannot authorize a transfer");
        var longName = new string('x', 78) + "😀";
        var longProfile = ProjectService.Create(device.Id); longProfile.Macros.Add(new() { Name = longName, Steps = Tap().Steps });
        var longCopy = SharedMacroLibrary.CopyIntoProfile(longProfile, [Tap(longName)]).Project.Macros.Last().Name;
        Check(longCopy.Length <= 80 && !longCopy.Any(c => c == '\uFFFD'), "Safe capped conflicting name");
        var full = ProjectService.Create(device.Id);
        full.Macros = Enumerable.Range(0, 32).Select(i => new MacroDefinition { Name = "M" + i, Steps = Tap().Steps }).ToList();
        var fullBefore = Serialize(full);
        Reject(() => SharedMacroLibrary.CopyIntoProfile(full, [Tap()]), "32-slot cap");
        Check(Serialize(full) == fullBefore, "Rejected copy keeps entire profile");
        full.Macros = Enumerable.Range(0, 6).Select(i => new MacroDefinition { Name = "M" + i, Steps = Enumerable.Range(0, 64).SelectMany(_ => Tap().Steps).ToList() }).ToList();
        Reject(() => SharedMacroLibrary.CopyIntoProfile(full, [Tap()]), "768-step cap");
    }
    var allowed = BindingCatalog.All.Where(b => BindingCatalog.IsBasic(b.Code)).Select(b => b.Code).ToArray();
    var media = new HashSet<string> { "KC_MUTE", "KC_VOLU", "KC_VOLD", "KC_MNXT", "KC_MPRV", "KC_MSTP", "KC_MPLY", "KC_CALC", "KC_MAIL", "KC_MYCM" };
    foreach (var key in allowed)
    {
        if (media.Contains(key)) Reject(() => SharedMacroLibrary.Validate([Tap(key: key)]), "Media macro rejected " + key);
        else { SharedMacroLibrary.Validate([Tap(key: key)]); checks++; }
    }
    foreach (var key in new[] { "KC_MAIL", "KC_MYCM" })
    {
        Reject(() => SharedMacroLibrary.Parse(Document(Serialize(new[] { Tap(key: key) }))), "Consumer key import rejected " + key);
        Reject(() => SharedMacroLibrary.CopyIntoProfile(ProjectService.Create("numpad"), [Tap(key: key)]), "Consumer key copy rejected " + key);
        Reject(() => SharedMacroLibrary.ExportFile(Path.Combine(root, key + ".json"), [Tap(key: key)]), "Consumer key export rejected " + key);
        Check(!File.Exists(Path.Combine(root, key + ".json")), "Rejected consumer export creates no file " + key);
    }
    foreach (var bad in new[] { "KC_NO", "KC_TRNS", "MO(1)", "MACRO:other", "KC_A\n", "KC_A;rm", "unknown" }) Reject(() => SharedMacroLibrary.Validate([Tap(key: bad)]), "Unsafe/unsupported key");
    foreach (var name in new[] { "", " ", "line\nbreak", "nul\0name", new string('x', 81) }) Reject(() => SharedMacroLibrary.Validate([Tap(name)]), "Invalid name");
    var special = Tap("../../CON:宏 {0} #00AAFF"); SharedMacroLibrary.Validate([special]); checks++; // Data, never a path.
    Reject(() => SharedMacroLibrary.Validate(Enumerable.Range(0, 257).Select(i => Tap("M" + i)).ToList()), "256 library cap");
    SharedMacroLibrary.Validate(Enumerable.Range(0, 256).Select(i => Tap("M" + i)).ToList()); checks++;
    var badMacro = Tap(); badMacro.Steps = []; Reject(() => SharedMacroLibrary.Validate([badMacro]), "Empty macro");
    badMacro.Steps = [new() { Kind = "down", Key = "KC_A" }]; Reject(() => SharedMacroLibrary.Validate([badMacro]), "Held key");
    badMacro.Steps = [new() { Kind = "up", Key = "KC_A" }]; Reject(() => SharedMacroLibrary.Validate([badMacro]), "Orphan release");
    badMacro.Steps = [new() { Kind = "down", Key = "KC_A" }, new() { Kind = "down", Key = "KC_A" }, new() { Kind = "up", Key = "KC_A" }]; Reject(() => SharedMacroLibrary.Validate([badMacro]), "Repeated down");
    badMacro.Steps = [new() { Kind = "delay", Key = "", DelayMs = 10000 }, new() { Kind = "delay", Key = "", DelayMs = 10000 }, new() { Kind = "delay", Key = "", DelayMs = 10000 }];
    SharedMacroLibrary.Validate([badMacro]); checks++; badMacro.Steps.Add(new() { Kind = "delay", Key = "", DelayMs = 1 }); Reject(() => SharedMacroLibrary.Validate([badMacro]), "30-second cap");
    foreach (var delay in new[] { int.MinValue, -1, 0, 10001, int.MaxValue })
    { badMacro.Steps = [new() { Kind = "delay", Key = "", DelayMs = delay }]; Reject(() => SharedMacroLibrary.Validate([badMacro]), "Delay bound"); }
    foreach (var badJson in new[] {
        "{}", "null", "[]", "{\"format\":\"other\",\"version\":1,\"macros\":[]}",
        "{\"format\":\"inglorious-macros\",\"version\":1,\"version\":1,\"macros\":[]}",
        "{\"format\":\"inglorious-macros\",\"version\":1,\"macros\":[],\"deviceId\":\"numpad\"}"
    }) Reject(() => SharedMacroLibrary.Parse(Encoding.UTF8.GetBytes(badJson)), "Invalid document");
    Reject(() => SharedMacroLibrary.Parse(Document("[]", "2")), "Unknown version");
    foreach (var body in new[] { "null", "[null]", "[{}]", "[{\"name\":\"bad\",\"steps\":null}]", "[{\"name\":\"bad\",\"name\":\"bad\",\"steps\":[]}]", "[{\"name\":\"bad\",\"steps\":[{\"kind\":\"delay\",\"delayMs\":5}]}]" })
        Reject(() => SharedMacroLibrary.Parse(Document(body)), "Malformed macro");
    var corrupt = File.ReadAllBytes(path); File.WriteAllText(path, "not json");
    Reject(() => SharedMacroLibrary.Save(fresh, []), "Corrupt library never overwritten"); Check(File.ReadAllText(path) == "not json", "Corrupt bytes retained"); File.WriteAllBytes(path, corrupt);
    using (var large = new FileStream(Path.Combine(root, "too-large.json"), FileMode.CreateNew)) large.SetLength(SharedMacroLibrary.MaximumFileBytes + 1L);
    Reject(() => SharedMacroLibrary.ImportFile(Path.Combine(root, "too-large.json")), "Bounded file read");
    var oversized = Enumerable.Range(0, SharedMacroLibrary.MaximumMacros).Select(i => new SharedMacro
    {
        Name = "Large " + i,
        Steps = Enumerable.Range(0, 128).Select(_ => new MacroStep { Kind = "delay", Key = new string('\u4E00', 64), DelayMs = 1 }).ToList()
    }).ToList();
    SharedMacroLibrary.Validate(oversized); checks++;
    Check(Encoding.UTF8.GetByteCount(Serialize(new SharedMacroDocument { Macros = oversized })) > SharedMacroLibrary.MaximumFileBytes,
        "A valid collection can exceed the byte limit when Unicode step text is escaped");
    var beforeLargeSave = File.ReadAllBytes(path); var beforeLargeExport = File.ReadAllBytes(export);
    Reject(() => SharedMacroLibrary.Save(SharedMacroLibrary.Load(path), oversized), "Oversized library save rejected before replacement");
    Check(beforeLargeSave.AsSpan().SequenceEqual(File.ReadAllBytes(path)) && SharedMacroLibrary.Load(path).Macros.Count == 2,
        "Rejected oversized save preserves the complete readable library");
    Reject(() => SharedMacroLibrary.ExportFile(export, oversized, overwrite: true), "Oversized portable export rejected before replacement");
    Check(beforeLargeExport.AsSpan().SequenceEqual(File.ReadAllBytes(export)) && SharedMacroLibrary.ImportFile(export).Count == 1,
        "Rejected oversized export preserves its previous readable file");
    Check(!Directory.EnumerateFiles(root, "*.tmp-*").Any(), "No leftover temporary files");
    Console.WriteLine($"PASS {checks} macro-library checks; no device access.");
}
finally { Directory.Delete(root, recursive: true); }
