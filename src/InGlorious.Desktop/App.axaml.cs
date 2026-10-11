using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        Name = "INGLORIOUS Advanced";
        AvaloniaXamlLoader.Load(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Program.Offline ? new AppPreferencesLoadResult(new(), null) : AppPreferencesStore.Load();
            var language = Array.IndexOf(Program.Arguments, "--language");
            if (Program.Offline && language >= 0 && language + 1 < Program.Arguments.Length && L.IsLanguagePreference(Program.Arguments[language + 1]))
                settings = settings with { Preferences = settings.Preferences with { Language = Program.Arguments[language + 1] } };
            L.SetLanguage(settings.Preferences.Language);
            desktop.MainWindow = new MainWindow(settings.Preferences, settings.Warning);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
