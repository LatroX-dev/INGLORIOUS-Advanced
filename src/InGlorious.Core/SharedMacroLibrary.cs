using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InGlorious.Core;

// Portable macro files intentionally contain no device, bindings, paths or profile IDs.
public sealed class SharedMacro
{
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public List<MacroStep> Steps { get; set; } = [];
}

public sealed class SharedMacroDocument
{
    [JsonRequired] public string Format { get; set; } = "inglorious-macros";
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public List<SharedMacro> Macros { get; set; } = [];
}

public sealed class MacroLibrarySnapshot
{
    internal MacroLibrarySnapshot(string path, string fingerprint, IReadOnlyList<SharedMacro> macros)
    { Path = path; Fingerprint = fingerprint; Macros = macros; }
    internal string Path { get; }
    internal string Fingerprint { get; }
    public IReadOnlyList<SharedMacro> Macros { get; }
}

public sealed record MacroProfileImport(AppProject Project, IReadOnlyList<string> AddedIds);

public static class SharedMacroLibrary
{
    public const int MaximumMacros = 256, MaximumFileBytes = 8 * 1024 * 1024;
    public static string DefaultPath => System.IO.Path.Combine(Paths.DataRoot, "macro-library.macros.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HashSet<string> MediaKeys = ["KC_MUTE", "KC_VOLU", "KC_VOLD", "KC_MNXT", "KC_MPRV", "KC_MSTP", "KC_MPLY", "KC_CALC", "KC_MAIL", "KC_MYCM"];

    public static MacroLibrarySnapshot Load(string? path = null)
    {
        path = System.IO.Path.GetFullPath(path ?? DefaultPath);
        if (!File.Exists(path)) return new(path, "missing", []);
        var bytes = ReadBounded(path);
        return new(path, Hash(bytes), Parse(bytes));
    }

    public static IReadOnlyList<SharedMacro> ImportFile(string path) => Parse(ReadBounded(path));

    public static IReadOnlyList<SharedMacro> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumFileBytes) throw Invalid();
        using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        CheckDuplicateProperties(json.RootElement);
        var document = json.Deserialize<SharedMacroDocument>(Options) ?? throw Invalid();
        if (document.Format != "inglorious-macros" || document.Version != 1) throw Invalid();
        Validate(document.Macros);
        foreach (var macro in json.RootElement.GetProperty("macros").EnumerateArray())
            foreach (var step in macro.GetProperty("steps").EnumerateArray())
                if (!step.TryGetProperty("kind", out _) || !step.TryGetProperty("key", out _) || !step.TryGetProperty("delayMs", out _)) throw Invalid();
        return document.Macros;
    }

    // Callers get detached copies. Importing never mutates the existing collection or user bindings.
    public static List<SharedMacro> Merge(IEnumerable<SharedMacro> existing, IEnumerable<SharedMacro> incoming)
    {
        var left = existing.ToList(); var right = incoming.ToList(); Validate(left); Validate(right);
        var result = left.Select(Clone).ToList();
        var identities = result.Select(Identity).ToHashSet(StringComparer.Ordinal);
        foreach (var macro in right) if (identities.Add(Identity(macro))) result.Add(Clone(macro));
        Validate(result); return result;
    }

    public static MacroLibrarySnapshot Save(MacroLibrarySnapshot snapshot, IEnumerable<SharedMacro> macros)
    {
        var detached = macros.Select(Clone).ToList(); Validate(detached);
        var path = snapshot.Path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // A stale dialog must not overwrite another process's changes or a damaged file.
        var current = Load(path);
        if (snapshot.Fingerprint != current.Fingerprint)
            throw new IOException(L.T("Die Makrobibliothek wurde inzwischen geändert. Bitte schließen und erneut öffnen."));
        var bytes = Serialize(detached);
        WriteAtomic(path, bytes, overwrite: current.Fingerprint != "missing");
        return new(path, Hash(bytes), detached);
    }

    public static void ExportFile(string path, IEnumerable<SharedMacro> macros, bool overwrite = false, MacroLibrarySnapshot? library = null)
    {
        path = System.IO.Path.GetFullPath(path);
        // macOS commonly uses case-insensitive APFS. A conservative comparison also
        // prevents a subset export from replacing the library through a linked folder.
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = ResolveExportPath(path);
        if (string.Equals(target, ResolveExportPath(DefaultPath), comparison)
            || library is not null && string.Equals(target, ResolveExportPath(library.Path), comparison))
            throw new InvalidDataException(L.T("Wähle für den Export eine andere Datei als die zentrale Bibliothek."));
        var detached = macros.Select(Clone).ToList(); Validate(detached);
        if (detached.Count == 0) throw new InvalidDataException(L.T("Wähle mindestens ein Makro aus."));
        WriteAtomic(path, Serialize(detached), overwrite);
    }

    private static string ResolveExportPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var resolved = System.IO.Path.GetPathRoot(full)!;
        foreach (var segment in full[resolved.Length..].Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = System.IO.Path.Combine(resolved, segment);
            FileSystemInfo entry = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            if (entry.LinkTarget is not null)
                resolved = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException(L.T("Wähle für den Export eine andere Datei als die zentrale Bibliothek."));
        }
        return resolved;
    }

    public static SharedMacro FromProfile(MacroDefinition macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var result = new SharedMacro { Name = macro.Name, Steps = macro.Steps?.Select(CopyStep).ToList()! };
        Validate([result]); return result;
    }

    public static MacroProfileImport CopyIntoProfile(AppProject source, IEnumerable<SharedMacro> macros)
    {
        var selected = macros.ToList(); Validate(selected);
        if (selected.Count == 0) throw new InvalidDataException(L.T("Wähle mindestens ein Makro aus."));
        if (source.Macros is null || source.Macros.Any(m => m is null || m.Steps is null) || source.Macros.Count + selected.Count > 32 ||
            source.Macros.Sum(m => (long)m.Steps.Count) + selected.Sum(m => (long)m.Steps.Count) > 768)
            throw new InvalidDataException(L.T("Nicht genügend Makroplätze oder Schritte für eine Kopie."));
        var result = ProjectService.Clone(source);
        var ids = result.Macros.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var names = result.Macros.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var macro in selected)
        {
            string id; do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id));
            var name = macro.Name;
            for (var number = 2; names.Contains(name); ++number)
            {
                var suffix = " (" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                var length = Math.Min(macro.Name.Length, 80 - suffix.Length);
                if (length > 0 && char.IsHighSurrogate(macro.Name[length - 1])) --length;
                name = macro.Name[..length] + suffix;
            }
            names.Add(name);
            result.Macros.Add(new() { Id = id, Name = name, Steps = macro.Steps.Select(CopyStep).ToList() }); added.Add(id);
        }
        return new(result, added);
    }

    public static void Validate(IReadOnlyList<SharedMacro>? macros)
    {
        if (macros is null || macros.Count > MaximumMacros) throw Invalid();
        foreach (var macro in macros)
        {
            if (macro is null || string.IsNullOrWhiteSpace(macro.Name) || macro.Name.Length > 80 || macro.Name.Any(char.IsControl) || macro.Steps is null || macro.Steps.Count is < 1 or > 128)
                throw Invalid();
            var held = new HashSet<string>(StringComparer.Ordinal); long delay = 0;
            foreach (var step in macro.Steps)
            {
                if (step is null || step.Key is null || step.Key.Length > 64) throw Invalid();
                if (step.Kind == "delay")
                {
                    if (step.DelayMs is < 1 or > 10000) throw Invalid(); delay += step.DelayMs;
                }
                else if (step.Kind is "down" or "up")
                {
                    if (!BindingCatalog.IsBasic(step.Key) || MediaKeys.Contains(step.Key))
                        throw new InvalidDataException(L.F($"Makro {macro.Name}: ungültige Taste."));
                    if (step.Kind == "down" ? !held.Add(step.Key) : !held.Remove(step.Key))
                        throw new InvalidDataException(L.T("Ergänze zuerst passende Drücken- und Lösen-Schritte."));
                }
                else throw Invalid();
            }
            if (held.Count != 0) throw new InvalidDataException(L.T("Ergänze zuerst passende Drücken- und Lösen-Schritte."));
            if (delay > 30000) throw new InvalidDataException(L.T("Ein Makro kann insgesamt höchstens 30 Sekunden Pausen enthalten."));
        }
    }

    public static SharedMacro Clone(SharedMacro macro)
    {
        if (macro is null) throw Invalid();
        return new() { Name = macro.Name, Steps = macro.Steps?.Select(CopyStep).ToList()! };
    }
    private static MacroStep CopyStep(MacroStep step) => step is null ? null! : new() { Kind = step.Kind, Key = step.Key, DelayMs = step.DelayMs };
    private static string Identity(SharedMacro macro) => JsonSerializer.Serialize(macro, Options);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static InvalidDataException Invalid() => new(L.T("Ungültige Makrodatei. Unterstützt werden Version 1, höchstens 256 Makros, 1–128 Schritte je Makro und vollständige Tastendrücke."));
    private static byte[] Serialize(List<SharedMacro> macros)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SharedMacroDocument { Macros = macros }, Options);
        if (bytes.Length > MaximumFileBytes) throw Invalid();
        return bytes;
    }
    private static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumFileBytes) throw Invalid();
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    private static void CheckDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); CheckDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicateProperties(item);
    }
    private static void WriteAtomic(string path, byte[] bytes, bool overwrite)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw new IOException(L.T("Die Makrodatei konnte nicht vollständig gespeichert werden."));
            if (overwrite && File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
