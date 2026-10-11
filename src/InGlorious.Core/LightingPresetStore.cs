using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InGlorious.Core;

// One layer's lighting only. Key bindings, macro data and layer names have no representation here.
public sealed class LightingPreset
{
    [JsonRequired] public string Format { get; set; } = "inglorious-lighting";
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string DeviceId { get; set; } = "";
    [JsonRequired] public Dictionary<string, string> KeyColors { get; set; } = [];
    [JsonRequired] public string SideLedColor { get; set; } = "#000000";
    [JsonRequired] public Dictionary<int, string> ExtraLedColors { get; set; } = [];
    [JsonRequired] public bool SynchronizeSides { get; set; }
    [JsonRequired] public LightingEffectSettings Effect { get; set; } = new();
    [JsonRequired] public Dictionary<int, PerLedEffectSettings> LedEffects { get; set; } = [];
}

public sealed record LightingPresetEntry(string Path, LightingPreset Preset, bool IsBuiltIn = false)
{
    public string DeviceName => DeviceCatalog.Get(Preset.DeviceId).Name;
}

public static class LightingPresetStore
{
    public const int MaximumFileBytes = 256 * 1024;
    public static string DefaultFolder => Path.Combine(Paths.DataRoot, "LightingPresets");
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static LightingPreset CreateInGlorious(string deviceId)
    {
        var device = DeviceCatalog.Get(deviceId); var project = ProjectService.Create(deviceId); var layer = project.Layers[0];
        // Four rotations of one pure-red sequence create moving brightness bands
        // without turning keys off. Reuse the same four palettes for both strips.
        string[] reds = ["#700000", "#B00000", "#FF0000", "#900000"];
        List<string> Phase(int offset) => Enumerable.Range(0, reds.Length).Select(i => reds[(i + offset) % reds.Length]).ToList();
        layer.LightingEffect = new() { Mode = 2, Speed = 164, Brightness = 240, Target = 3, Colors = Phase(0) };
        layer.SynchronizeSides = true; layer.SideLedColor = "#E00000";
        var lettering = new HashSet<string>(["KC_I", "KC_N", "KC_G", "KC_L", "KC_O", "KC_R", "KC_U", "KC_S"], StringComparer.Ordinal);
        foreach (var key in device.Keys)
        {
            layer.Keys[key.Id].Color = "#E00000";
            if (key.LedIndex is not { } led) continue;
            var horn = key.Y < 1 && (key.X < device.Width * .18 || key.X + key.W > device.Width * .82);
            var accent = horn || lettering.Contains(key.DefaultBinding);
            var phase = Math.Clamp((int)(key.X * reds.Length / device.Width), 0, reds.Length - 1);
            if (accent) layer.Keys[key.Id].Color = "#FF0000";
            if (accent || phase != 0)
            {
                layer.LedEffects[led] = new() { Mode = 2, Speed = accent ? 208 : 164, Brightness = accent ? 255 : 240, Colors = Phase(phase) };
            }
        }
        foreach (var strip in new[] { device.LeftSideLedIndices, device.RightSideLedIndices })
            for (var i = 0; i < strip.Count; i++)
                layer.LedEffects[strip[i]] = new() { Mode = 2, Speed = 176, Brightness = 255, Colors = Phase(i % reds.Length) };
        return Capture(project, 0, "INGLORIOUS");
    }

    public static LightingPreset Capture(AppProject project, int layer, string name)
    {
        if (layer < 0 || layer >= project.Layers.Count) throw new ArgumentOutOfRangeException(nameof(layer));
        var source = project.Layers[layer];
        var result = new LightingPreset
        {
            Name = name, DeviceId = project.DeviceId,
            KeyColors = source.Keys.ToDictionary(k => k.Key, k => k.Value.Color, StringComparer.Ordinal),
            SideLedColor = source.SideLedColor, ExtraLedColors = new(source.ExtraLedColors), SynchronizeSides = source.SynchronizeSides,
            Effect = Copy(source.LightingEffect), LedEffects = source.LedEffects.ToDictionary(k => k.Key, k => Copy(k.Value))
        };
        Validate(result); return result;
    }

    public static AppProject Apply(AppProject source, int layer, LightingPreset preset)
    {
        Validate(preset);
        if (source.DeviceId != preset.DeviceId) throw new InvalidDataException(L.T("Diese Beleuchtungsvorlage gehört zu einem anderen Gerätemodell."));
        if (layer < 0 || layer >= source.Layers.Count) throw new ArgumentOutOfRangeException(nameof(layer));
        var result = ProjectService.Clone(source);
        Assign(result.Layers[layer], preset);
        return result;
    }

    public static void Validate(LightingPreset preset)
    {
        if (preset is null || preset.Format != "inglorious-lighting" || preset.Version != 1 || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80 || preset.Name.Any(char.IsControl)) throw Invalid();
        var device = DeviceCatalog.All.FirstOrDefault(d => d.Id == preset.DeviceId);
        if (device is null || preset.KeyColors is null || preset.KeyColors.Count != device.Keys.Count || device.Keys.Any(k => !preset.KeyColors.ContainsKey(k.Id)) ||
            preset.KeyColors.Values.Any(c => !ValidColor(c)) || !ValidColor(preset.SideLedColor) || preset.ExtraLedColors is null ||
            preset.ExtraLedColors.Any(p => !device.NonKeyLedIndices.Contains(p.Key) || !ValidColor(p.Value)) || preset.Effect is null || preset.LedEffects is null) throw Invalid();
        var test = ProjectService.Create(device.Id); Assign(test.Layers[0], preset);
        var errors = ProjectService.Validate(test);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    public static LightingPreset Load(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumFileBytes) throw Invalid();
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        return Parse(bytes);
    }
    public static LightingPreset Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumFileBytes) throw Invalid();
        using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        CheckProperties(json.RootElement);
        var preset = json.Deserialize<LightingPreset>(Options) ?? throw Invalid(); Validate(preset); return preset;
    }

    public static LightingPresetEntry SaveToLibrary(LightingPreset preset, string? folder = null)
    {
        Validate(preset); folder = Path.GetFullPath(folder ?? DefaultFolder); Directory.CreateDirectory(folder);
        preset = Copy(preset);
        if (preset.Name.Trim().Equals("INGLORIOUS", StringComparison.OrdinalIgnoreCase)) preset.Name = L.F($"{preset.Name} – Kopie");
        // Names are display data, never filenames. Every explicit save is an independent preset.
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".lighting.json");
        Export(path, preset); return new(path, Copy(preset));
    }
    public static void Export(string path, LightingPreset preset, bool overwrite = false)
    {
        Validate(preset); var bytes = JsonSerializer.SerializeToUtf8Bytes(preset, Options);
        if (bytes.Length > MaximumFileBytes) throw Invalid();
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw new IOException(L.T("Die Beleuchtungsvorlage konnte nicht vollständig gespeichert werden."));
            if (overwrite && File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<LightingPresetEntry> Discover(out string? warning, string? folder = null)
    {
        warning = null; folder ??= DefaultFolder;
        var entries = DeviceCatalog.All.Select(d => new LightingPresetEntry("builtin:inglorious:" + d.Id, CreateInGlorious(d.Id), true)).ToList();
        if (!Directory.Exists(folder)) return entries;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.lighting.json", SearchOption.TopDirectoryOnly))
            {
                try { entries.Add(new(path, Load(path))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
                { warning = L.T("Mindestens eine Beleuchtungsvorlage konnte nicht gelesen werden. Die Dateien bleiben unverändert."); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = ex.Message; }
        return entries.OrderByDescending(e => e.IsBuiltIn).ThenBy(e => e.Preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static void Delete(LightingPresetEntry entry, string? folder = null)
    {
        if (entry.IsBuiltIn || entry.Path.StartsWith("builtin:", StringComparison.Ordinal))
            throw new InvalidDataException(L.T("Die INGLORIOUS-Standardvorlage kann nicht gelöscht werden."));
        var root = Path.GetFullPath(folder ?? DefaultFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(entry.Path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), root, comparison) || !full.EndsWith(".lighting.json", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        // Verify the chosen file still represents the same preset before deleting it.
        if (!JsonSerializer.Serialize(Load(full), Options).Equals(JsonSerializer.Serialize(entry.Preset, Options), StringComparison.Ordinal))
            throw new IOException(L.T("Die Beleuchtungsvorlage wurde inzwischen geändert. Bitte die Liste neu öffnen."));
        File.Delete(full);
    }

    private static void Assign(LayerProfile layer, LightingPreset preset)
    {
        foreach (var (key, color) in preset.KeyColors) layer.Keys[key].Color = color;
        layer.SideLedColor = preset.SideLedColor; layer.ExtraLedColors = new(preset.ExtraLedColors); layer.SynchronizeSides = preset.SynchronizeSides;
        layer.LightingEffect = Copy(preset.Effect); layer.LedEffects = preset.LedEffects.ToDictionary(p => p.Key, p => Copy(p.Value));
    }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
    private static bool ValidColor(string? color) => color is not null && Regex.IsMatch(color, "\\A#[0-9A-Fa-f]{6}\\z");
    private static InvalidDataException Invalid() => new(L.T("Ungültige Beleuchtungsvorlage. Version, Name und vollständiges Gerätelayout müssen übereinstimmen."));
    private static void CheckProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); CheckProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckProperties(item);
    }
}
