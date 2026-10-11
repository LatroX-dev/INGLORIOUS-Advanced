using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace InGlorious.Core;

public sealed record LanguageOption(string Code, string NativeName);

// UI-only localization. Source strings and interpolation arguments are kept separate.
public static class L
{
    public const string SystemLanguage = "system";
    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("de", "Deutsch"), new("en", "English"), new("es", "Español"),
        new("fr", "Français"), new("it", "Italiano"), new("pt-BR", "Português (Brasil)"),
        new("pl", "Polski"), new("tr", "Türkçe"), new("ru", "Русский"),
        new("ja", "日本語"), new("ko", "한국어"), new("zh-Hans", "简体中文")
    ];
    // Capture the user's display language before SetLanguage changes this process's UI culture.
    // Reading CurrentUICulture again after a manual choice would make "system" stick to that choice.
    private static readonly string SystemUiCultureName = CultureInfo.CurrentUICulture.Name;
    public static IReadOnlyList<LanguageOption> LanguageChoices => [new(SystemLanguage, T("Systemsprache")), .. SupportedLanguages];
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Strings = new(LoadCatalog);
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog => Strings.Value;
    public static string CurrentLanguage { get; private set; } = ResolveSystemLanguage(SystemUiCultureName);
    public static event Action? LanguageChanged;
    public static bool IsSupportedLanguage(string? code) => SupportedLanguages.Any(l => l.Code == code);
    public static bool IsLanguagePreference(string? code) => code == SystemLanguage || IsSupportedLanguage(code);

    public static string ResolveSystemLanguage(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return "en";
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(cultureName); }
        catch (CultureNotFoundException) { return "en"; }
        if (culture.TwoLetterISOLanguageName == "zh")
        {
            var parts = culture.Name.Split('-');
            if (parts.Contains("Hant", StringComparer.OrdinalIgnoreCase)) return "en";
            if (parts.Contains("Hans", StringComparer.OrdinalIgnoreCase)) return "zh-Hans";
            if (parts.Any(p => p.Equals("TW", StringComparison.OrdinalIgnoreCase) || p.Equals("HK", StringComparison.OrdinalIgnoreCase) || p.Equals("MO", StringComparison.OrdinalIgnoreCase))) return "en";
            return "zh-Hans";
        }
        for (var candidate = culture; candidate.Name.Length > 0; candidate = candidate.Parent)
        {
            var exact = SupportedLanguages.FirstOrDefault(option => option.Code.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Code;
            if (candidate.Name.Equals("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        }
        return "en";
    }

    public static void SetLanguage(string code)
    {
        if (!IsLanguagePreference(code)) throw new ArgumentException("Unsupported application language.", nameof(code));
        var resolved = code == SystemLanguage ? ResolveSystemLanguage(SystemUiCultureName) : code;
        var changed = CurrentLanguage != resolved;
        CurrentLanguage = resolved;
        // Do not change CurrentCulture, keyboard layout, input language or serialized data.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(resolved);
        if (changed) LanguageChanged?.Invoke();
    }

    public static string T(string source)
    {
        if (CurrentLanguage == "de" || string.IsNullOrEmpty(source)) return source;
        if (!Catalog.TryGetValue(source, out var translations)) return source;
        return translations.GetValueOrDefault(CurrentLanguage) ?? translations.GetValueOrDefault("en") ?? source;
    }

    public static string F(FormattableString source) => Format(source.Format, source.GetArguments());
    public static string Format(string source, params object?[] args)
    {
        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        try { return string.Format(culture, T(source), args); }
        catch (FormatException) { return string.Format(culture, source, args); }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadCatalog()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var assembly = typeof(L).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".Locales.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream) ?? throw new InvalidDataException("Empty language catalog: " + resource);
            foreach (var (source, translations) in entries)
            {
                if (string.IsNullOrWhiteSpace(source) || translations.Any(p => !IsSupportedLanguage(p.Key) || string.IsNullOrWhiteSpace(p.Value)))
                    throw new InvalidDataException("Invalid language catalog entry: " + resource);
                if (result.TryGetValue(source, out var prior))
                {
                    var merged = new Dictionary<string, string>(prior, StringComparer.Ordinal);
                    foreach (var (language, value) in translations) merged.TryAdd(language, value);
                    result[source] = merged;
                }
                else result.Add(source, translations);
            }
        }
        return result;
    }
}
