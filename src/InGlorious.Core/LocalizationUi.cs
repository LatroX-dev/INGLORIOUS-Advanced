using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Markup;

namespace InGlorious.Core;

public static class LocalizationUi
{
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);
    private static bool _initialized;
    public static void Initialize()
    {
        if (!_initialized) { _initialized = true; L.LanguageChanged += Refresh; }
        Refresh();
    }
    public static string ResourceKey(string source) => "InGlorious.Text." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    public static string Register(string source)
    {
        Registered.Add(source);
        var key = ResourceKey(source);
        if (Application.Current is { } app) app.Resources[key] = L.T(source);
        return key;
    }
    private static void Refresh()
    {
        if (Application.Current is not { } app) return;
        foreach (var source in L.Catalog.Keys.Concat(Registered).Distinct(StringComparer.Ordinal))
            app.Resources[ResourceKey(source)] = L.T(source);
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new DynamicResourceExtension(LocalizationUi.Register(Key)).ProvideValue(serviceProvider);
}
