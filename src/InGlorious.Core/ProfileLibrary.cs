using System.IO;
using System.Text;
using System.Text.Json;

namespace InGlorious.Core;

public sealed record ProfileEntry(string Path, string Name, string DeviceId, DateTime Modified, int Layers, int Macros);

/// <summary>Local discovery and recent paths; reading the library never changes profile files.</summary>
public sealed class ProfileLibrary(string root)
{
    private readonly string _root = System.IO.Path.GetFullPath(root);
    // Unix volumes may be case-sensitive, including APFS. Never merge distinct names there.
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return PathComparer.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
    private sealed class Index
    {
        public int Version { get; set; } = 1;
        public List<string> Recent { get; set; } = [];
        public Dictionary<string, string> Active { get; set; } = [];
    }
    private string IndexPath => System.IO.Path.Combine(_root, "profiles-index.json");
    public string Folder => System.IO.Path.Combine(_root, "Profiles");
    private Index ReadIndex()
    {
        if (!File.Exists(IndexPath)) return new();
        if (new FileInfo(IndexPath).Length > 262144) throw new InvalidDataException(L.T("Die Profilliste ist ungültig."));
        var index = JsonSerializer.Deserialize<Index>(File.ReadAllBytes(IndexPath));
        if (index is null || index.Version != 1 || index.Recent is null || index.Active is null || index.Recent.Count > 1000)
            throw new InvalidDataException(L.T("Die Profilliste ist ungültig."));
        return index;
    }
    public IReadOnlyList<ProfileEntry> Discover(out string? warning)
    {
        warning = null;
        var paths = new HashSet<string>(PathComparer);
        void AddPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { paths.Add(System.IO.Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        try { foreach (var path in ReadIndex().Recent) AddPath(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { warning = ex.Message; }
        foreach (var folder in new[] { _root, Folder })
        {
            try { if (Directory.Exists(folder)) foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)) AddPath(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning ??= ex.Message; }
        }
        var entries = new List<ProfileEntry>();
        foreach (var path in paths)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 1_048_576) continue;
                // Avoid normalizing unrelated JSON documents as profiles.
                using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("deviceId", out _) || !json.RootElement.TryGetProperty("layers", out _)) continue;
                var project = ProjectService.Load(path);
                if (ProjectService.Validate(project).Count != 0) continue;
                entries.Add(new(file.FullName, project.Name, project.DeviceId, file.LastWriteTime, project.Layers.Count, project.Macros.Count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { }
        }
        return entries.OrderByDescending(p => p.Modified).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public string? ActivePath(string deviceId)
    {
        try { return ReadIndex().Active.GetValueOrDefault(deviceId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }
    public void Remember(string path, string deviceId, bool activate = true)
    {
        path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(_root);
        using var gate = new FileStream(IndexPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var index = ReadIndex(); // A malformed index is never silently overwritten.
        index.Recent.RemoveAll(p => PathsEqual(p, path));
        index.Recent.Insert(0, path);
        index.Recent = index.Recent.Take(100).ToList();
        if (activate) index.Active[deviceId] = path;
        var temp = IndexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, index); file.Flush(true); }
            if (File.Exists(IndexPath)) File.Replace(temp, IndexPath, null); else File.Move(temp, IndexPath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public string SuggestPath(string name)
    {
        const string extension = ".inglorious.json";
        var filename = SuggestFileName(name);
        var stem = filename[..^extension.Length];
        var candidate = System.IO.Path.Combine(Folder, filename);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); ++n)
            candidate = System.IO.Path.Combine(Folder, stem + " (" + n + ")" + extension);
        return candidate;
    }

    /// <summary>A portable filename suggestion only. The profile name and existing files are never changed.</summary>
    public static string SuggestFileName(string? name)
    {
        const string extension = ".inglorious.json";
        var stem = (name ?? "").Trim().TrimEnd('.', ' ');
        while (stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^(stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? extension.Length : 5)];
            stem = stem.TrimEnd('.', ' ');
        }
        // Apply the same Windows-safe character set on every host, including Unix.
        stem = new string(stem.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        var bounded = new StringBuilder();
        var bytes = 0;
        foreach (var rune in stem.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 220) break;
            bounded.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        stem = bounded.ToString().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(stem)) stem = "Profile";
        var deviceName = stem.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        var reserved = deviceName is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$"
            || deviceName.Length == 4 && (deviceName.StartsWith("COM", StringComparison.Ordinal) || deviceName.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(deviceName[3]);
        if (reserved)
        {
            var dot = stem.IndexOf('.');
            stem = stem.Insert(dot >= 0 ? dot : stem.Length, "_");
        }
        return stem + extension;
    }
}

