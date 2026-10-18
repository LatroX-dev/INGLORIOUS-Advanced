using System.Text;
using System.Globalization;
using System.Text.Json;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string message) { ++checks; if (!value) throw new InvalidOperationException(message); }
void Reject(Action action, string message)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Check(true, message); return; }
    throw new InvalidOperationException(message);
}
var root = Path.Combine(Path.GetTempPath(), "inglorious-preferences-" + Guid.NewGuid().ToString("N"));
var path = Path.Combine(root, "data", "app-settings.json");
var startupUiCulture = CultureInfo.CurrentUICulture.Name;
var startupNumericCulture = CultureInfo.CurrentCulture.Name;
try
{
    Check(L.SystemLanguage == "system" && L.IsLanguagePreference(L.SystemLanguage) && !L.IsSupportedLanguage(L.SystemLanguage), "system is a preference, not an extra translation language");
    foreach (var (cultureName, expected) in new (string? Culture, string Expected)[]
    {
        (null, "en"), ("", "en"), (" ", "en"), ("not a locale!", "en"), ("de/AT", "en"), ("--", "en"),
        ("de", "de"), ("de-AT", "de"), ("de-CH", "de"), ("DE-at", "de"),
        ("en-US", "en"), ("en-GB", "en"), ("es-MX", "es"), ("es-ES", "es"),
        ("fr-CA", "fr"), ("it-CH", "it"), ("pt", "pt-BR"), ("pt-PT", "pt-BR"), ("pt-BR", "pt-BR"),
        ("pl-PL", "pl"), ("tr-TR", "tr"), ("ru-RU", "ru"), ("ja-JP", "ja"), ("ko-KR", "ko"),
        ("zh-CN", "zh-Hans"), ("zh-SG", "zh-Hans"), ("zh-Hans", "zh-Hans"), ("zh-Hans-CN", "zh-Hans"),
        ("zh-TW", "en"), ("zh-HK", "en"), ("zh-MO", "en"), ("zh-Hant", "en"), ("zh-Hant-TW", "en"),
        ("cs-CZ", "en"), ("nl-NL", "en"), ("ar-SA", "en"), ("uk-UA", "en")
    }) Check(L.ResolveSystemLanguage(cultureName) == expected, "system culture resolution: " + (cultureName ?? "<null>"));
    Check(L.LanguageChoices.Count == L.SupportedLanguages.Count + 1 && L.LanguageChoices[0].Code == L.SystemLanguage && L.LanguageChoices.Skip(1).Select(l => l.Code).SequenceEqual(L.SupportedLanguages.Select(l => l.Code)), "system appears once before all explicit language choices");
    Check(L.LanguageChoices.All(choice => L.IsLanguagePreference(choice.Code)) && !L.IsLanguagePreference(null) && !L.IsLanguagePreference("") && !L.IsLanguagePreference("de-AT") && !L.IsLanguagePreference("unknown"), "only system and supported explicit codes are valid stored preferences");
    var systemLanguage = L.ResolveSystemLanguage(startupUiCulture);
    L.SetLanguage(L.SystemLanguage);
    Check(L.CurrentLanguage == systemLanguage && L.IsSupportedLanguage(L.CurrentLanguage), "system uses the startup UI culture and publishes a concrete translation code");
    var manualLanguage = systemLanguage == "de" ? "en" : "de";
    L.SetLanguage(manualLanguage);
    Check(L.CurrentLanguage == manualLanguage, "manual language takes effect immediately");
    L.SetLanguage(L.SystemLanguage);
    Check(L.CurrentLanguage == systemLanguage && CultureInfo.CurrentCulture.Name == startupNumericCulture, "returning to system does not stick to the most recent manual language or alter numeric culture");

    var initial = AppPreferencesStore.Load(path);
    Check(initial.Preferences.Language == L.SystemLanguage && new AppPreferences().Language == L.SystemLanguage, "missing preferences default to system language");
    Check(initial.Warning is null && initial.Preferences.StartDevice == "last" && initial.Preferences.LastDeviceId == "pro-iso", "default start device");
    Check(initial.Preferences.RememberWindowSize && initial.Preferences.ShowHelp, "window memory and help on by default");
    Check(initial.Preferences.CheckForSoftwareUpdates, "software update checks are enabled by default");
    Check(initial.Preferences.ResolveStartDevice() == "pro-iso", "last-device fallback is deterministic");
    var session = AppPreferencesStore.BeginEdit(initial.Preferences, path);
    Check(!Directory.Exists(root), "read and edit session create no files or directories");
    Check(!ReferenceEquals(session.Preferences, initial.Preferences) && session.Preferences == initial.Preferences, "edit session owns an immutable copy");
    var saved = initial.Preferences with { StartDevice = "numpad", LastDeviceId = "numpad", RememberWindowSize = false, ShowHelp = true, CheckForSoftwareUpdates = false };
    AppPreferencesStore.Save(session, saved);
    Check(AppPreferencesStore.Load(path).Preferences == saved, "explicit edit save roundtrips all preferences");
    using (var savedJson = JsonDocument.Parse(File.ReadAllBytes(path)))
        Check(savedJson.RootElement.GetProperty("Language").GetString() == L.SystemLanguage, "save persists the system preference rather than its resolved current language");
    var legacyPath = Path.Combine(root, "legacy.json"); File.WriteAllText(legacyPath, "{}");
    Check(AppPreferencesStore.Load(legacyPath).Preferences.Language == L.SystemLanguage && File.ReadAllText(legacyPath) == "{}", "legacy settings without Language default to system without rewriting the source");
    File.WriteAllText(legacyPath, "{\"Language\":\"de\",\"ShowHelp\":false}");
    var legacyBytes = File.ReadAllBytes(legacyPath);
    var explicitGerman = AppPreferencesStore.Load(legacyPath);
    Check(explicitGerman.Warning is null && explicitGerman.Preferences.Language == "de" && !explicitGerman.Preferences.ShowHelp && File.ReadAllBytes(legacyPath).SequenceEqual(legacyBytes), "existing explicit German stays German without migration writes");
    Check(AppPreferencesStore.SaveWindowState("numpad", 1280, 820, legacyPath).Language == "de", "legacy explicit language survives a window-state save");
    L.SetLanguage(manualLanguage);
    Check(AppPreferencesStore.SaveWindowState("pro-iso", 1280, 820, path).Language == L.SystemLanguage && AppPreferencesStore.Load(path).Preferences.Language == L.SystemLanguage, "window-state save retains stored system even while the running UI uses a manual language");
    AppPreferencesStore.Save(saved, path);
    Check(initial.Preferences.StartDevice == "last" && initial.Preferences.ShowHelp, "editing cannot mutate the original preferences");
    Check(!Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Any(), "atomic save leaves no temporary file");
    Check(AppPreferencesStore.Load(path).Preferences.ResolveStartDevice() == "numpad", "fixed start device resolves exactly");
    AppPreferencesStore.Save(saved with { StartDevice = "last", LastDeviceId = "pro-iso" }, path);
    Check(AppPreferencesStore.Load(path).Preferences.ResolveStartDevice() == "pro-iso", "last-device update survives reload");
    var stale = AppPreferencesStore.BeginEdit(saved, path);
    AppPreferencesStore.Save(saved with { ShowHelp = false }, path);
    var externallyChanged = File.ReadAllBytes(path);
    Reject(() => AppPreferencesStore.Save(stale, saved), "stale settings dialog cannot overwrite a later save");
    Check(File.ReadAllBytes(path).SequenceEqual(externallyChanged), "conflict preserves newer settings bytes");

    // Instance A keeps its startup preferences while B changes options before A closes.
    var instanceA = AppPreferencesStore.Load(path).Preferences;
    var instanceB = instanceA with { StartDevice = "pro-iso", ShowHelp = true, RememberWindowSize = false, WindowWidth = 1600, WindowHeight = 1000 };
    AppPreferencesStore.Save(instanceB, path);
    var closedA = AppPreferencesStore.SaveWindowState("numpad", 1200, 800, path);
    Check(closedA == instanceB with { LastDeviceId = "numpad" }, "older instance closing preserves latest options and observes newly disabled size memory");
    Check(AppPreferencesStore.Load(path).Preferences == closedA, "merged window state is the state actually persisted");
    var instanceC = closedA with { StartDevice = "last", ShowHelp = false, RememberWindowSize = true };
    AppPreferencesStore.Save(instanceC, path);
    var closedB = AppPreferencesStore.SaveWindowState("pro-iso", 1340, 870, path);
    Check(closedB == instanceC with { LastDeviceId = "pro-iso", WindowWidth = 1340, WindowHeight = 870 }, "older instance closing observes newly enabled size memory without reverting other options");

    // A second process writes between the fresh state snapshot and the atomic save.
    var closingSession = AppPreferencesStore.BeginEdit(path);
    var concurrentChange = closedB with { StartDevice = "numpad", ShowHelp = true };
    AppPreferencesStore.Save(concurrentChange, path);
    var concurrentBytes = File.ReadAllBytes(path);
    Reject(() => AppPreferencesStore.SaveWindowState(closingSession, "numpad", 1400, 900), "mid-save concurrent preference change rejects the stale state snapshot");
    Check(File.ReadAllBytes(path).SequenceEqual(concurrentBytes), "rejected window update leaves concurrent preference bytes untouched");
    Check(closingSession.Preferences == closedB, "fresh edit session contains the values read with its fingerprint");
    Reject(() => AppPreferencesStore.SaveWindowState("unknown", 1400, 900, path), "window state rejects unknown device identifiers");

    foreach (var bad in new[] { "{broken", "null", "{\"SchemaVersion\":2}", "{\"StartDevice\":\"unknown\"}", "{\"LastDeviceId\":null}", "{\"WindowWidth\":4}", "{\"WindowHeight\":9000}", "{\"Language\":null}", "{\"Language\":\"de-AT\"}", "{\"Language\":\"unknown\"}",
        "{\"FeedbackRequestedModels\":null}", "{\"FeedbackRequestedModels\":[\"unknown\"]}", "{\"FeedbackRequestedModels\":[\"numpad\",\"numpad\"]}" })
    {
        var bytes = Encoding.UTF8.GetBytes(bad); File.WriteAllBytes(path, bytes);
        var fallback = AppPreferencesStore.Load(path);
        Check(fallback.Warning is not null && fallback.Preferences == new AppPreferences(), "corrupt or unsupported settings fall back visibly");
        Reject(() => AppPreferencesStore.Save(saved, path), "automatic preference save refuses corrupt source");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "automatic save preserves corrupt source exactly");
        Reject(() => AppPreferencesStore.SaveWindowState("pro-iso", 1200, 800, path), "window shutdown refuses corrupt or unsupported preference files");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "window shutdown cannot repair or overwrite malformed preferences");
        var before = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.bak").ToHashSet();
        var repair = AppPreferencesStore.BeginEdit(fallback.Preferences, path);
        AppPreferencesStore.Save(repair, saved);
        var backup = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.bak").Single(file => !before.Contains(file));
        Check(File.ReadAllBytes(backup).SequenceEqual(bytes), "explicit repair preserves the original invalid bytes in a unique backup");
        Check(AppPreferencesStore.Load(path).Preferences == saved, "explicit repair installs validated preferences");
    }
    var bounded = AppPreferencesStore.CaptureWindow(new(), 500, 99999);
    Check(bounded.WindowWidth == 1150 && bounded.WindowHeight == 4320, "window state clamps to supported bounds");
    Check(AppPreferencesStore.CaptureWindow(saved, 1400, 850) == saved, "disabled size memory preserves stored size");
    var defaults = new AppPreferences();
    Check(AppPreferencesStore.CaptureWindow(defaults, double.NaN, 850) == defaults && AppPreferencesStore.CaptureWindow(defaults, 1400, double.PositiveInfinity) == defaults, "nonfinite window dimensions are ignored");
    var marked = defaults with { FeedbackRequestedModels = ["numpad", "pro-iso"] };
    AppPreferencesStore.Save(marked, path);
    var reloaded = AppPreferencesStore.Load(path).Preferences;
    Check(reloaded == marked && reloaded.GetHashCode() == marked.GetHashCode(), "per-model markers preserve preference value equality and hashing after reload");
    var markedCopy = marked.ClonePreferences();
    Check(markedCopy == marked && !ReferenceEquals(markedCopy.FeedbackRequestedModels, marked.FeedbackRequestedModels), "cloning owns its marker list while retaining value equality");
    markedCopy.FeedbackRequestedModels[0] = "pro-iso";
    Check(marked.FeedbackRequestedModels[0] == "numpad" && markedCopy != marked, "editing the clone cannot mutate the source marker list");
    foreach (var invalid in new[] { defaults with { SchemaVersion = 3 }, defaults with { StartDevice = "other" }, defaults with { LastDeviceId = "other" }, defaults with { WindowWidth = double.NaN }, defaults with { WindowHeight = 0 }, defaults with { Language = "unknown" }, defaults with { Language = "de-AT" }, defaults with { Language = null! },
        defaults with { FeedbackRequestedModels = null! }, defaults with { FeedbackRequestedModels = ["unknown"] }, defaults with { FeedbackRequestedModels = ["numpad", "numpad"] } })
        Reject(() => AppPreferencesStore.Save(invalid, path), "invalid application preferences cannot be written");
    var large = new byte[65537]; File.WriteAllBytes(path, large);
    Check(AppPreferencesStore.Load(path).Warning is not null, "oversized file reports a warning");
    Reject(() => AppPreferencesStore.BeginEdit(defaults, path), "oversized file cannot be silently repaired");
    Check(File.ReadAllBytes(path).SequenceEqual(large), "oversized settings file remains intact");
    Console.WriteLine($"PASS app preferences: {checks} checks; temporary files only, no app preferences, processes or device access.");
}
finally
{
    L.SetLanguage(L.SystemLanguage);
    var resolved = Path.GetFullPath(root);
    if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(resolved).StartsWith("inglorious-preferences-", StringComparison.Ordinal))
        throw new InvalidOperationException("Cleanup target is outside the generated test directory.");
    if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
}

// Every preference operation above uses its explicit temporary path.
// The real device catalog is linked so stored model IDs follow the same registry as the app.
