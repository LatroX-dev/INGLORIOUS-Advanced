using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InGlorious.Core;

var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
void Reject(Action action, string label) { try { action(); } catch { checks++; return; } throw new Exception(label); }
var root = Path.Combine(Path.GetTempPath(), "inglorious-integration-" + Guid.NewGuid().ToString("N"));
var startupUiCulture = CultureInfo.CurrentUICulture.Name;
try
{
    var library = new ProfileLibrary(root);
    Check(library.Discover(out var warning).Count == 0 && warning is null && !Directory.Exists(root), "Discovery is side-effect free");
    Directory.CreateDirectory(root);
    var profile = ProjectService.Create("pro-iso"); profile.Name = "Büro · 日本語";
    var path = library.SuggestPath(profile.Name); ProjectService.Save(path, profile);
    Check(Path.GetFileName(path) == "Büro · 日本語.inglorious.json" && profile.Name == "Büro · 日本語",
        "Portable filename preserves the custom name without a product prefix or profile mutation");
    foreach (var (input, expected) in new (string?, string)[] {
        ("GMMK Numpad", "GMMK Numpad"), ("GMMK PRO ISO", "GMMK PRO ISO"),
        ("Gaming.json", "Gaming"), ("Gaming.INGLORIOUS.JSON", "Gaming"), ("Gaming.json.inglorious.json", "Gaming"),
        ("Gaming.inglorious.json.json", "Gaming"), ("Gaming. . ", "Gaming"), ("My: Profile/Work\\Home?*<>|\"", "My_ Profile_Work_Home______"),
        ("a\0\n\tb", "a___b"), (null, "Profile"), ("", "Profile"), (".. ", "Profile"),
        ("CON", "CON_"), ("con.txt", "con_.txt"), ("AUX.json", "AUX_"), ("NUL.backup", "NUL_.backup"),
        ("COM1", "COM1_"), ("COM¹.txt", "COM¹_.txt"), ("lpt³", "lpt³_"), ("CONIN$", "CONIN$_"),
        ("COM10", "COM10"), ("Console", "Console"), ("PRName", "PRName"), (new string('a', 80), new string('a', 80)) })
        Check(ProfileLibrary.SuggestFileName(input) == expected + ".inglorious.json", "Portable filename normalizes suffix, characters and reserved names: " + input);
    var longUnicodeName = ProfileLibrary.SuggestFileName(string.Concat(Enumerable.Repeat("🦊", 100)));
    Check(System.Text.Encoding.UTF8.GetByteCount(longUnicodeName) <= 255 && !longUnicodeName.Contains('\uFFFD')
        && longUnicodeName.EndsWith(".inglorious.json", StringComparison.Ordinal), "Long Unicode suggestions fit portable filename limits without splitting a character");
    foreach (var id in new[] { "pro-iso", "numpad" })
    {
        var decoded = OnboardSerializer.Decode(OnboardSerializer.Prepare(ProjectService.Create(id)).Payload);
        Check(decoded.Name == DeviceCatalog.Get(id).Name, id + ": device read uses the device name without provenance as the profile name");
    }
    var original = File.ReadAllBytes(path);
    File.WriteAllText(Path.Combine(root, "unrelated.json"), "{\"message\":\"hello\"}");
    File.WriteAllText(Path.Combine(root, "broken.json"), "{oops");
    foreach (var (name, json) in new[] { ("array", "[]"), ("null", "null"), ("number", "42"), ("string", "\"text\""), ("boolean", "true"), ("bad-profile", "{\"deviceId\":\"numpad\",\"layers\":null}") })
        File.WriteAllText(Path.Combine(root, name + ".json"), json);
    Check(library.Discover(out warning) is [{ Name: "Büro · 日本語", DeviceId: "pro-iso" }] && warning is null, "Discovery validates actual camel-case profile files and ignores unrelated JSON");
    Check(File.ReadAllBytes(path).SequenceEqual(original), "Discovery leaves profiles untouched");
    library.Remember(path, "pro-iso");
    Check(library.ActivePath("pro-iso") == path && library.Discover(out _).Count == 1, "Recent paths deduplicate default-folder entries");
    Check(ProfileLibrary.PathsEqual(path, Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path))), "Profile path comparison normalizes dot segments");
    Check(ProfileLibrary.PathsEqual(path, path.ToUpperInvariant()) == OperatingSystem.IsWindows(), "Case-distinct paths are kept separate on Unix, including case-sensitive APFS");
    Check(!ProfileLibrary.PathsEqual(null, path) && !ProfileLibrary.PathsEqual("\0", path), "Invalid recent paths never alias a profile");
    library.Remember(Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)), "pro-iso");
    Check(library.Discover(out _).Count == 1, "Remember normalizes equivalent paths before deduplication");
    var second = library.SuggestPath(profile.Name); Check(Path.GetFileName(second) == "Büro · 日本語 (2).inglorious.json", "Collisions use a readable parenthesized number without overwriting a file");
    ProjectService.Save(second, profile); library.Remember(second, "pro-iso", activate: false);
    Check(library.ActivePath("pro-iso") == path && library.Discover(out _).Count == 2, "Exported copies join the library without replacing the active profile");
    var directoryCollision = library.SuggestPath(profile.Name); Directory.CreateDirectory(directoryCollision);
    Check(Path.GetFileName(library.SuggestPath(profile.Name)) == "Büro · 日本語 (4).inglorious.json" && File.ReadAllBytes(path).SequenceEqual(original),
        "Filename suggestions skip both files and directories and preserve existing profile bytes");
    File.WriteAllText(Path.Combine(root, "profiles-index.json"), "{bad");
    Reject(() => library.Remember(second, "pro-iso"), "Malformed index must not be overwritten");
    Check(File.ReadAllText(Path.Combine(root, "profiles-index.json")) == "{bad" && library.Discover(out warning).Count == 2 && warning is not null, "Malformed index preserves bytes and valid folder profiles remain visible");
    foreach (var name in new[] { "..", "CON", "a/b\\c", "" }) Check(Path.GetDirectoryName(library.SuggestPath(name)) == library.Folder, "Suggested profile filename stays in profile folder");
    var blockedRoot = Path.Combine(root, "file-instead-of-folder"); File.WriteAllText(blockedRoot, "keep");
    Reject(() => new ProfileLibrary(blockedRoot).Remember(path, "pro-iso"), "Profile index reports an inaccessible destination");
    Check(File.ReadAllText(blockedRoot) == "keep" && File.ReadAllBytes(path).SequenceEqual(original), "Failed profile-index write preserves blocker and profile bytes");

    foreach (var os in new[] { "windows", "macos", "linux" }) foreach (var layout in new[] { "de", "en", "fr" }) foreach (var action in OsActionCatalog.For(os, layout))
    {
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            var p = ProjectService.Create(id);
            if (action.DirectBinding is { } direct) p.Layers[0].Keys.Values.First().Binding = direct;
            else { var macro = OsActionCatalog.CreateMacro(action); p.Macros.Add(macro); p.Layers[0].Keys.Values.First().Binding = "MACRO:" + macro.Id; }
            var image = OnboardSerializer.Prepare(p);
            Check(OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.SequenceEqual(image.Payload), os + "/" + layout + "/" + action.Id + " survives real wire format");
        }
    }
    Check(OsActionCatalog.For("windows", "de").Single(a => a.Id == "undo").Keys.Last() == "KC_Y", "German logical Z maps to physical USB Y position");
    Check(OsActionCatalog.For("macos", "en").Single(a => a.Id == "copy").Keys.First() == "KC_LGUI", "Mac Copy uses Command");
    Check(OsActionCatalog.For("windows", "en").Single(a => a.Id == "files").Keys.SequenceEqual(new[] { "KC_LGUI", "KC_E" }), "Explorer uses Win+E");
    Check(OsActionCatalog.For("linux").All(a => a.System == "linux"), "Target OS actions stay explicit");

    var preferencesPath = Path.Combine(root, "preferences.json");
    var originalProfile = JsonSerializer.Serialize(profile);
    var culture = CultureInfo.CurrentCulture.Name;
    foreach (var language in L.LanguageChoices)
    {
        L.SetLanguage(language.Code);
        var prefs = new AppPreferences { Language = language.Code, SharedBrightness = true, SharedBrightnessNoticeDismissed = true };
        AppPreferencesStore.Save(prefs, preferencesPath);
        Check(AppPreferencesStore.Load(preferencesPath).Preferences == prefs, language.Code + " preference roundtrip");
        Check(AppPreferencesStore.SaveWindowState("numpad", 1200, 800, preferencesPath).Language == language.Code, language.Code + " survives window-state save");
        Check(L.CurrentLanguage == (language.Code == L.SystemLanguage ? L.ResolveSystemLanguage(startupUiCulture) : language.Code), language.Code + " resolves to the correct UI language without changing the stored preference");
        Check(JsonSerializer.Serialize(profile) == originalProfile && CultureInfo.CurrentCulture.Name == culture, "Language changes preserve profile data and numeric culture");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Language and settings saves preserve the original profile file byte for byte");
        Check(L.Format("Profil „{0}“ geöffnet", "test {9}").Contains("test {9}"), "Interpolation arguments never become translation patterns");
    }
    var systemLanguage = L.ResolveSystemLanguage(startupUiCulture);
    var explicitLanguage = systemLanguage == "de" ? "en" : "de";
    L.SetLanguage(L.SystemLanguage);
    Check(L.CurrentLanguage == systemLanguage, "return from the explicit-language loop restores startup system language");
    var systemPreferences = new AppPreferences { Language = L.SystemLanguage, StartDevice = "numpad", SharedBrightness = true };
    AppPreferencesStore.Save(systemPreferences, preferencesPath);
    var beforeLanguageChanges = File.ReadAllBytes(preferencesPath);
    L.SetLanguage(explicitLanguage);
    Check(L.CurrentLanguage == explicitLanguage, "explicit selection overrides the system choice for UI rendering");
    L.SetLanguage(L.SystemLanguage);
    Check(L.CurrentLanguage == systemLanguage && File.ReadAllBytes(preferencesPath).SequenceEqual(beforeLanguageChanges), "language switching alone neither rewrites preferences nor sticks to the manual culture");
    Check(JsonSerializer.Serialize(profile) == originalProfile && File.ReadAllBytes(path).SequenceEqual(original) && CultureInfo.CurrentCulture.Name == culture, "system/manual/system preserves profile, serialized bytes and numeric culture");
    var savedSystemWindow = AppPreferencesStore.SaveWindowState("pro-iso", 1320, 840, preferencesPath);
    Check(savedSystemWindow.Language == L.SystemLanguage && savedSystemWindow.StartDevice == systemPreferences.StartDevice && savedSystemWindow.SharedBrightness, "window state merges leave the system choice and other options intact");
    using (var settingsJson = JsonDocument.Parse(File.ReadAllBytes(preferencesPath)))
        Check(settingsJson.RootElement.GetProperty("Language").GetString() == L.SystemLanguage, "serialized settings store system, not the resolved language code");
    L.SetLanguage("de");
    File.WriteAllText(preferencesPath, "{}");
    Check(AppPreferencesStore.Load(preferencesPath).Preferences is { Language: L.SystemLanguage, ShowHelp: true, SharedBrightness: false } && File.ReadAllText(preferencesPath) == "{}", "Legacy preferences without language get system defaults without file migration");
    File.WriteAllText(preferencesPath, "{\"Language\":\"de\",\"ShowHelp\":false}");
    var explicitLegacyBytes = File.ReadAllBytes(preferencesPath);
    Check(AppPreferencesStore.Load(preferencesPath).Preferences is { Language: "de", ShowHelp: false } && File.ReadAllBytes(preferencesPath).SequenceEqual(explicitLegacyBytes), "Legacy explicit German remains unchanged");
    File.WriteAllText(preferencesPath, "{\"ShowHelp\":false}");
    Check(!AppPreferencesStore.Load(preferencesPath).Preferences.ShowHelp, "Explicit help preference remains respected");
    foreach (var (source, translations) in L.Catalog)
    {
        var sourceFields = Regex.Matches(source, @"\{\d+(?:,[^:}]*)?(?::[^}]*)?\}").Select(m => m.Value).Order().ToArray();
        foreach (var language in L.SupportedLanguages.Where(l => l.Code != "de"))
        {
            Check(translations.TryGetValue(language.Code, out var translated) && !string.IsNullOrWhiteSpace(translated), "Missing translation: " + language.Code + " " + source);
            var fields = Regex.Matches(translated!, @"\{\d+(?:,[^:}]*)?(?::[^}]*)?\}").Select(m => m.Value).Order().ToArray();
            Check(fields.SequenceEqual(sourceFields), "Translation placeholder mismatch: " + language.Code + " " + source);
        }
    }

    var pad = new FakeDevice("numpad") { Brightness = 87, SliderMode = 3 };
    var pro = new FakeDevice("pro-iso") { Brightness = 255 };
    var devices = new[] { new OnboardDevice("numpad", "Pad", "pad"), new OnboardDevice("pro-iso", "PRO", "pro") };
    var mirror = new SharedBrightnessService(() => devices, d => new OnboardClient(d.DeviceId == "numpad" ? pad : pro, d.DeviceId));
    mirror.Start();
    await WaitUntil(() => pro.Sets.Count == 1);
    Check(pro.Brightness == 87, "Dimmer mirrors initial value");
    await Task.Delay(250);
    Check(pro.Sets.Count == 1, "Stationary slider sends no repeated brightness writes");
    pad.Brightness = 21;
    await WaitUntil(() => pro.Sets.Count == 2);
    Check(pro.Brightness == 21, "Dimmer mirrors movement");
    pad.SliderMode = 0; pad.Brightness = 111;
    await Task.Delay(250);
    Check(pro.Brightness == 21, "Disabled slider is never mirrored");
    await Task.WhenAll(mirror.StopAsync(), mirror.StopAsync());
    Check(!mirror.Running && pad.Commands.All(c => c is 1 or 9) && pro.Commands.All(c => c is 1 or 10), "Mirror stops safely and never reads/writes/commits configuration");
    pro.Flags = 1; pad.Commands.Clear(); pro.Commands.Clear();
    mirror.Start(); await WaitUntil(() => pro.Commands.Count > 0); await Task.Delay(120); await mirror.StopAsync();
    Check(pad.Commands.All(c => c == 1) && pro.Commands.All(c => c == 1), "Legacy firmware receives no runtime commands");
    var guideIds = new[] { "start", "lighting", "bindings", "macros", "layers", "profiles", "device", "standby", "settings" };
    Check(UserGuide.Topics.Select(topic => topic.Id).SequenceEqual(guideIds), "Guide topics are a stable, flat navigation covering the application");
    Check(UserGuide.Topics.All(topic => topic.Sections.Count is >= 2 and <= 3), "Guide articles remain short and structured");
    var guideText = UserGuide.Topics.SelectMany(topic => new[] { topic.Title }.Concat(topic.Sections.SelectMany(section => new[] { section.Heading, section.Body })))
        .Concat(new[] { "Anleitung", "Themen", "Schließen", "Funktion", "Reglerbereich", "Kanal", "Controller", "Minimum", "Maximum",
            "Nur anpassen, wenn der Regler nicht den gesamten Bereich erreicht.", "Steuert einen MIDI-Controller in einer kompatiblen Anwendung.",
            "Ändert die Lautstärke durch Bewegen des Reglers.", "Regelt die Helligkeit der GMMK Numpad-Beleuchtung.", "Keine Aktion beim Bewegen des Reglers." }).Distinct().ToArray();
    var priorLanguage = L.CurrentLanguage;
    foreach (var language in L.SupportedLanguages)
    {
        L.SetLanguage(language.Code);
        foreach (var source in guideText)
        {
            Check(!string.IsNullOrWhiteSpace(source), "Guide and slider texts cannot be empty");
            if (language.Code == "de") Check(L.T(source) == source, "German guide preserves its source text");
            else Check(L.Catalog.TryGetValue(source, out var values) && values.TryGetValue(language.Code, out var value)
                && !string.IsNullOrWhiteSpace(value) && L.T(source) == value, language.Code + ": guide and slider use their explicit translation without fallback");
        }
    }
    L.SetLanguage(priorLanguage);
    Console.WriteLine($"PASS {checks} integration checks; {L.Catalog.Count} localized sources");
}
finally { L.SetLanguage("de"); if (Directory.Exists(root)) Directory.Delete(root, true); }

static async Task WaitUntil(Func<bool> condition)
{ var until = DateTime.UtcNow.AddSeconds(4); while (!condition()) { if (DateTime.UtcNow > until) throw new Exception("Timed out waiting for fake-device state"); await Task.Delay(10); } }

sealed class FakeDevice(string id) : IOnboardTransport
{
    public byte Brightness, SliderMode;
    public byte Flags = 65;
    public readonly ConcurrentQueue<byte> Commands = new();
    public readonly ConcurrentQueue<byte> Sets = new();
    public Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Commands.Enqueue(request[3]);
        var response = new byte[32]; response[0] = (byte)'I'; response[1] = (byte)'G'; response[2] = 1; response[3] = request[3]; response[4] = request[4];
        switch (request[3])
        {
            case 1:
                response[6] = 24; var shape = OnboardSerializer.Shape(id);
                response[8] = shape.Board; response[9] = (byte)shape.Rows; response[10] = (byte)shape.Cols; response[11] = 4; response[12] = (byte)shape.Leds; response[13] = 32; response[14] = 128; response[15] = (byte)(Flags | (id == "numpad" ? 2 : 0));
                BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(16), (ushort)shape.Size); BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(18), 768); BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(30), 1); break;
            case 9: response[6] = 4; response[8] = Brightness; response[9] = SliderMode; break;
            case 10: Brightness = request[8]; Sets.Enqueue(Brightness); response[6] = 1; response[8] = Brightness; break;
            default: throw new Exception("Mirror used a forbidden command " + request[3]);
        }
        return Task.FromResult(response);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
