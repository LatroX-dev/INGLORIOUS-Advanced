using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace InGlorious.Core;

public sealed record AppPreferences
{
    public int SchemaVersion { get; init; } = 1;
    public string StartDevice { get; init; } = "last";
    public string LastDeviceId { get; init; } = "pro-iso";
    public string Language { get; init; } = L.SystemLanguage;
    public bool RememberWindowSize { get; init; } = true;
    public bool ShowHelp { get; init; } = true;
    public bool CheckForSoftwareUpdates { get; init; } = true;
    public bool SharedBrightness { get; init; }
    public bool SharedBrightnessNoticeDismissed { get; init; }
    public double WindowWidth { get; init; } = 1536;
    public double WindowHeight { get; init; } = 960;

    public string ResolveStartDevice() => StartDevice == "last" ? LastDeviceId : StartDevice;
    public AppPreferences ClonePreferences() => this with { };
}

public sealed record AppPreferencesLoadResult(AppPreferences Preferences, string? Warning);

public sealed class AppPreferencesEditSession
{
    internal AppPreferencesEditSession(string path, string fingerprint, AppPreferences preferences, string? warning)
    { Path = path; Fingerprint = fingerprint; Preferences = preferences; Warning = warning; }
    internal string Path { get; }
    internal string Fingerprint { get; }
    public AppPreferences Preferences { get; }
    public string? Warning { get; }
}

public static class AppPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DefaultPath => System.IO.Path.Combine(Paths.DataRoot, "app-settings.json");
    private sealed record Snapshot(byte[]? Bytes, string Fingerprint, AppPreferences? Value, string? Warning);

    public static AppPreferencesLoadResult Load(string? path = null)
    {
        try
        {
            var snapshot = ReadSnapshot(System.IO.Path.GetFullPath(path ?? DefaultPath));
            return new(snapshot.Value ?? new(), snapshot.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(new(), L.T("App-Einstellungen konnten nicht gelesen werden: ") + ex.Message); }
    }

    public static AppPreferencesEditSession BeginEdit(AppPreferences current, string? path = null)
    {
        Validate(current);
        var fullPath = System.IO.Path.GetFullPath(path ?? DefaultPath);
        var snapshot = ReadSnapshot(fullPath);
        return new(fullPath, snapshot.Fingerprint, current.ClonePreferences(), snapshot.Warning);
    }

    // This overload couples the latest stored values and their fingerprint to the same read.
    public static AppPreferencesEditSession BeginEdit(string? path = null)
    {
        var fullPath = System.IO.Path.GetFullPath(path ?? DefaultPath);
        var snapshot = ReadSnapshot(fullPath);
        if (snapshot.Value is null)
            throw new InvalidDataException(L.T("Die ungültige Einstellungsdatei bleibt unverändert. Automatische Fensterdaten werden nicht gespeichert."));
        return new(fullPath, snapshot.Fingerprint, snapshot.Value, null);
    }

    // Window shutdown updates only state, preserving preferences changed by another app instance.
    public static AppPreferences SaveWindowState(string deviceId, double width, double height, string? path = null) =>
        SaveWindowState(BeginEdit(path), deviceId, width, height);

    internal static AppPreferences SaveWindowState(AppPreferencesEditSession session, string deviceId, double width, double height)
    {
        if (session.Warning is not null) throw new InvalidDataException(L.T("Die ungültige Einstellungsdatei bleibt unverändert."));
        var updated = CaptureWindow(session.Preferences with { LastDeviceId = deviceId }, width, height);
        SaveCore(updated, session.Path, session.Fingerprint, repairInvalidFile: false);
        return updated;
    }

    // Complete explicit preference writes reject malformed files; shutdown uses SaveWindowState.
    public static void Save(AppPreferences preferences, string? path = null) =>
        SaveCore(preferences, System.IO.Path.GetFullPath(path ?? DefaultPath), null, repairInvalidFile: false);

    // Explicit acceptance in Settings may repair an invalid file, after preserving its exact bytes.
    public static void Save(AppPreferencesEditSession session, AppPreferences preferences) =>
        SaveCore(preferences, session.Path, session.Fingerprint, repairInvalidFile: true);

    public static AppPreferences CaptureWindow(AppPreferences preferences, double width, double height)
    {
        if (!preferences.RememberWindowSize || !double.IsFinite(width) || !double.IsFinite(height)) return preferences;
        return preferences with { WindowWidth = Math.Clamp(width, 1150, 7680), WindowHeight = Math.Clamp(height, 760, 4320) };
    }

    public static void Validate(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.SchemaVersion != 1 || (preferences.StartDevice != "last" && !DeviceCatalog.All.Any(device => device.Id == preferences.StartDevice)) ||
            !DeviceCatalog.All.Any(device => device.Id == preferences.LastDeviceId) || !L.IsLanguagePreference(preferences.Language) ||
            !double.IsFinite(preferences.WindowWidth) || preferences.WindowWidth is < 1150 or > 7680 ||
            !double.IsFinite(preferences.WindowHeight) || preferences.WindowHeight is < 760 or > 4320)
            throw new InvalidDataException(L.T("Die App-Einstellungen enthalten ungültige Werte oder eine unbekannte Version."));
    }

    private static Snapshot ReadSnapshot(string path)
    {
        if (!File.Exists(path)) return new(null, "missing", new(), null);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 65536) throw new InvalidDataException(L.T("Die App-Einstellungsdatei ist unerwartet groß. Sie bleibt unverändert."));
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            var value = JsonSerializer.Deserialize<AppPreferences>(bytes, JsonOptions) ?? throw new InvalidDataException(L.T("Leere Einstellungen."));
            Validate(value);
            return new(bytes, hash, value, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return new(bytes, hash, null, L.T("Die Einstellungsdatei ist ungültig. Es gelten die Standardwerte. Beim Speichern wird die ursprüngliche Datei zuerst gesichert."));
        }
    }

    private static void SaveCore(AppPreferences preferences, string path, string? expectedFingerprint, bool repairInvalidFile)
    {
        Validate(preferences);
        var directory = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = ReadSnapshot(path);
        if (expectedFingerprint is not null && previous.Fingerprint != expectedFingerprint)
            throw new IOException(L.T("Die App-Einstellungen wurden inzwischen geändert. Bitte diesen Dialog schließen und erneut öffnen."));
        if (previous.Value is null)
        {
            if (!repairInvalidFile) throw new InvalidDataException(L.T("Die ungültige Einstellungsdatei bleibt unverändert. Öffne die App-Einstellungen und speichere dort, um sie mit Sicherung zu ersetzen."));
            var backup = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N") + ".bak";
            WriteNewDurable(backup, previous.Bytes!);
            if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(previous.Bytes))
                throw new IOException(L.T("Die Sicherung der Einstellungsdatei konnte nicht bestätigt werden. Das Original bleibt unverändert."));
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteNewDurable(temporary, JsonSerializer.SerializeToUtf8Bytes(preferences, JsonOptions));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteNewDurable(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(bytes); file.Flush(flushToDisk: true);
    }
}
