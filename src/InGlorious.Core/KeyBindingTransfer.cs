using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InGlorious.Core;

// A portable binding file has no lighting, timing, profile path or physical-device identity.
public sealed class KeyBindingDocument
{
    [JsonRequired] public string Format { get; set; } = "inglorious-bindings";
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public string DeviceId { get; set; } = "";
    [JsonRequired] public List<KeyBindingLayer> Layers { get; set; } = [];
    [JsonRequired] public List<MacroDefinition> Macros { get; set; } = [];
    [JsonRequired] public SliderSettings? Slider { get; set; }
}

public sealed class KeyBindingLayer
{
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public Dictionary<string, string> Bindings { get; set; } = [];
}

public static class KeyBindingTransfer
{
    public const int MaximumFileBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static KeyBindingDocument Capture(AppProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        OnboardSerializer.ValidateEditorProject(project);
        var referenced = References(project.Layers.SelectMany(layer => layer.Keys.Values.Select(key => key.Binding)));
        var result = new KeyBindingDocument
        {
            DeviceId = project.DeviceId,
            Layers = project.Layers.Select(layer => new KeyBindingLayer
            {
                Name = layer.Name,
                Bindings = layer.Keys.ToDictionary(key => key.Key, key => key.Value.Binding, StringComparer.Ordinal)
            }).ToList(),
            Macros = project.Macros.Where(macro => referenced.Contains(macro.Id)).Select(CopyMacro).ToList(),
            Slider = DeviceCatalog.Get(project.DeviceId).HasSlider ? CopySlider(project.Slider) : null
        };
        Validate(result);
        return result;
    }

    public static KeyBindingDocument Load(string path)
    {
        // The limit and the read refer to the same handle, including a concurrent file growth check.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumFileBytes) throw Invalid();
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw Invalid();
        try
        {
            var content = bytes.AsMemory();
            if (content.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
            using var json = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
            CheckShape(json.RootElement);
            var document = json.Deserialize<KeyBindingDocument>(Options) ?? throw Invalid();
            Validate(document);
            return document;
        }
        catch (JsonException) { throw Invalid(); }
    }

    public static AppProject Apply(AppProject project, KeyBindingDocument document)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(document);
        if (project.DeviceId != document.DeviceId)
            throw new InvalidDataException(L.T("Diese Tastenbelegungen gehören zu einem anderen Gerätemodell oder Layout."));
        OnboardSerializer.ValidateEditorProject(project);
        var result = ProjectService.Clone(project);
        var ids = result.Macros.Select(macro => macro.Id).ToHashSet(StringComparer.Ordinal);
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var macro in result.Macros) identities.TryAdd(ContentIdentity(macro), macro.Id);
        var remapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var incoming in document.Macros)
        {
            var identity = ContentIdentity(incoming);
            if (!identities.TryGetValue(identity, out var id))
            {
                id = incoming.Id;
                if (!ids.Add(id)) { do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id)); }
                var copy = CopyMacro(incoming); copy.Id = id;
                result.Macros.Add(copy);
                identities.Add(identity, id);
            }
            remapped.Add(incoming.Id, id);
        }
        Assign(result, document, remapped);
        // Lighting palettes/standby share device storage with macros. Validate the merged profile,
        // not only the imported subset; never remove old macros or truncate steps to make it fit.
        OnboardSerializer.ValidateEditorProject(result);
        return result;
    }

    public static void Export(string path, KeyBindingDocument document, bool overwrite = false)
    {
        Validate(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (bytes.Length > MaximumFileBytes) throw Invalid();
        path = Path.GetFullPath(path);
        NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        NoLinks(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw SaveFailed();
            NoLinks(path); NoLinks(temporary);
            if (overwrite && File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string SuggestFileName(AppProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var name = project.Name?.Trim() ?? "";
        foreach (var suffix in new[] { ".bindings.json", ".inglorious.json", ".json" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        name = string.Concat(name.Select(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '-' : character)).Trim(' ', '.');
        if (name.Length > 80) name = name[..(char.IsHighSurrogate(name[79]) ? 79 : 80)].TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) name = DeviceCatalog.Get(project.DeviceId).Name;
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
            name = "INGLORIOUS-" + name;
        return name + ".bindings.json";
    }

    private static void Validate(KeyBindingDocument document)
    {
        if (document is null || document.Format != "inglorious-bindings" || document.Version != 1) throw Invalid();
        var device = DeviceCatalog.All.FirstOrDefault(candidate => candidate.Id == document.DeviceId);
        if (device is null || document.Layers is null || document.Layers.Count != 4 || document.Macros is null || document.Macros.Count > 32 ||
            device.HasSlider != (document.Slider is not null)) throw Invalid();
        foreach (var layer in document.Layers)
        {
            if (layer is null || !ValidName(layer.Name) || layer.Bindings is null || layer.Bindings.Count != device.Keys.Count ||
                !layer.Bindings.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(device.Keys.Select(key => key.Id)) ||
                layer.Bindings.Any(pair => pair.Value is null || pair.Value.Length > 70 || pair.Value.Any(char.IsControl))) throw Invalid();
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long steps = 0;
        foreach (var macro in document.Macros)
        {
            if (macro is null || macro.Id is null || macro.Id.Length is < 1 or > 64 ||
                macro.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-') || !ids.Add(macro.Id) ||
                !ValidName(macro.Name) || macro.Steps is null || macro.Steps.Count is < 1 or > 128) throw Invalid();
            steps += macro.Steps.Count;
            foreach (var step in macro.Steps)
                if (step is null || step.Kind is not ("down" or "up" or "delay") || step.Key is null || step.Key.Length > 64 ||
                    step.Key.Any(char.IsControl) || step.DelayMs is < 0 or > 10000) throw Invalid();
        }
        var referenced = References(document.Layers.SelectMany(layer => layer.Bindings.Values));
        if (steps > 768 || !ids.SetEquals(referenced)) throw Invalid();
        var neutral = ProjectService.Create(device.Id);
        neutral.Macros = document.Macros.Select(CopyMacro).ToList();
        Assign(neutral, document, ids.ToDictionary(id => id, id => id, StringComparer.Ordinal));
        OnboardSerializer.ValidateEditorProject(neutral);
    }

    private static void Assign(AppProject target, KeyBindingDocument document, IReadOnlyDictionary<string, string> remapped)
    {
        for (var index = 0; index < 4; index++)
        {
            target.Layers[index].Name = document.Layers[index].Name;
            foreach (var (key, binding) in document.Layers[index].Bindings)
                target.Layers[index].Keys[key].Binding = binding.StartsWith("MACRO:", StringComparison.Ordinal) ? "MACRO:" + remapped[binding[6..]] : binding;
        }
        if (document.Slider is not null) target.Slider = CopySlider(document.Slider);
    }

    private static HashSet<string> References(IEnumerable<string> bindings) => bindings.Where(binding => binding.StartsWith("MACRO:", StringComparison.Ordinal))
        .Select(binding => binding[6..]).ToHashSet(StringComparer.Ordinal);
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !name.Any(char.IsControl);
    private static MacroDefinition CopyMacro(MacroDefinition macro) => new()
    {
        Id = macro.Id, Name = macro.Name,
        Steps = macro.Steps.Select(step => new MacroStep { Kind = step.Kind, Key = step.Key, DelayMs = step.DelayMs }).ToList()
    };
    private static SliderSettings CopySlider(SliderSettings slider) => new()
    { Mode = slider.Mode, Minimum = slider.Minimum, Maximum = slider.Maximum, MidiChannel = slider.MidiChannel, MidiController = slider.MidiController };
    private static string ContentIdentity(MacroDefinition macro) => JsonSerializer.Serialize(macro.Steps.Select(step => new
    { step.Kind, Key = step.Kind == "delay" ? "" : step.Key, DelayMs = step.Kind == "delay" ? step.DelayMs : 0 }));

    private static void CheckShape(JsonElement root)
    {
        RequireProperties(root, "format", "version", "deviceId", "layers", "macros", "slider");
        if (root.GetProperty("layers").ValueKind != JsonValueKind.Array || root.GetProperty("macros").ValueKind != JsonValueKind.Array) throw Invalid();
        foreach (var layer in root.GetProperty("layers").EnumerateArray())
        {
            RequireProperties(layer, "name", "bindings");
            var bindings = layer.GetProperty("bindings");
            if (bindings.ValueKind != JsonValueKind.Object) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings.EnumerateObject())
                if (!seen.Add(binding.Name) || binding.Value.ValueKind != JsonValueKind.String) throw Invalid();
        }
        foreach (var macro in root.GetProperty("macros").EnumerateArray())
        {
            RequireProperties(macro, "id", "name", "steps");
            if (macro.GetProperty("steps").ValueKind != JsonValueKind.Array) throw Invalid();
            foreach (var step in macro.GetProperty("steps").EnumerateArray()) RequireProperties(step, "kind", "key", "delayMs");
        }
        var slider = root.GetProperty("slider");
        if (slider.ValueKind != JsonValueKind.Null) RequireProperties(slider, "mode", "minimum", "maximum", "midiChannel", "midiController");
    }
    private static void RequireProperties(JsonElement value, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var remaining = required.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) throw Invalid();
        if (remaining.Count != 0) throw Invalid();
    }
    private static void NoLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null || entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw SaveFailed();
        }
    }
    private static InvalidDataException Invalid() => new(L.T("Die Datei enthält keine gültigen Tastenbelegungen. Bitte wähle eine andere Datei."));
    private static IOException SaveFailed() => new(L.T("Die Tastenbelegungen konnten nicht vollständig gespeichert werden."));
}
