using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InGlorious.Core;

public sealed record ColorPaletteLoadResult(IReadOnlyList<string> Colors, string? Error);

/// <summary>App-wide color presets. Reading never creates or repairs a file.</summary>
public static class ColorPaletteStore
{
    public const int MaximumColors = 32;
    public static string DefaultPath => Path.Combine(Paths.DataRoot, "custom-colors.json");
    public static IReadOnlyList<string> DefaultColors { get; } = Array.AsReadOnly(new[]
    { "#FFFFFF", "#FF4D58", "#FF8A3D", "#FFD447", "#A6E34B", "#46D881", "#35D6C8", "#46C7F4", "#4389FF", "#7659F7", "#BA65EF", "#F369B2", "#FFC4A3", "#BCD9FF", "#B1F1DD", "#000000" });
    private const int MaximumFileBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed class PaletteDocument
    {
        [JsonRequired] public int SchemaVersion { get; set; }
        [JsonRequired] public List<string>? Colors { get; set; }
    }

    public static ColorPaletteLoadResult Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException(L.T("Die Farbpalette ist zu groß (maximal 16 KB)."));
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != json.RootElement.EnumerateObject().Count())
                throw new InvalidDataException(L.T("Die Farbpalette ist ungültig."));
            var document = json.Deserialize<PaletteDocument>(JsonOptions)
                ?? throw new InvalidDataException(L.T("Die Farbpalette ist leer."));
            if (document.SchemaVersion is not 1 and not 2) throw new InvalidDataException(L.T("Diese Version der Farbpalette wird nicht unterstützt."));
            if (document.Colors is null) throw new InvalidDataException(L.T("Die Farbliste fehlt."));
            var colors = NormalizeColors(document.Colors);
            // V1 had permanent swatches plus removable custom colors. V2 makes the whole palette editable.
            return new(document.SchemaVersion == 1 ? colors.Concat(DefaultColors).Distinct(StringComparer.Ordinal).Take(MaximumColors).ToArray() : colors, null);
        }
        catch (FileNotFoundException) { return new(DefaultColors, null); }
        catch (DirectoryNotFoundException) { return new(DefaultColors, null); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return new(Array.Empty<string>(), L.T("Farbpalette konnte nicht geladen werden: ") + ex.Message);
        }
    }

    /// <summary>Saves explicitly; refuses to overwrite a malformed or unsupported existing file.</summary>
    public static IReadOnlyList<string> Save(string path, IReadOnlyList<string> colors)
    {
        var normalized = NormalizeColors(colors);
        return Update(path, _ => normalized);
    }

    // These operations always use the latest shared list rather than a stale window's copy.
    public static IReadOnlyList<string> Add(string path, string color)
    {
        var normalized = NormalizeColor(color);
        return Update(path, colors => colors.Append(normalized).ToArray());
    }
    public static IReadOnlyList<string> Remove(string path, string color)
    {
        var normalized = NormalizeColor(color);
        return Update(path, colors => colors.Where(c => c != normalized).ToArray());
    }
    private static IReadOnlyList<string> Update(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var gate = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Load(full);
        if (existing.Error is not null)
            throw new InvalidDataException(existing.Error + L.T(" Die vorhandene Datei wurde nicht überschrieben."));
        var normalized = NormalizeColors(transform(existing.Colors));
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new PaletteDocument { SchemaVersion = 2, Colors = normalized.ToList() }, JsonOptions);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes)) throw new IOException(L.T("Die Farbpalette konnte nicht vollständig gespeichert werden."));
            if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            return normalized;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string NormalizeColor(string color)
    {
        if (color is null) throw new InvalidDataException(L.T("Eine Farbe fehlt."));
        var value = color.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length != 6 || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException(L.T("Farben benötigen sechs Hex-Zeichen im Format #RRGGBB."));
        return "#" + value.ToUpperInvariant();
    }

    private static IReadOnlyList<string> NormalizeColors(IReadOnlyList<string> colors)
    {
        if (colors is null) throw new InvalidDataException(L.T("Die Farbliste fehlt."));
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var color in colors)
        {
            var normalized = NormalizeColor(color);
            if (seen.Add(normalized)) result.Add(normalized);
            if (result.Count > MaximumColors) throw new InvalidDataException(L.T("Die Farbpalette kann maximal 32 Farben enthalten."));
        }
        return result.AsReadOnly();
    }
}
