using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private readonly SharedBrightnessService _sharedBrightness = new();
    private ComboBox CreateLanguageChoice(string preference, IReadOnlyList<LanguageOption> options) =>
        Choice(options.Select(option => option.NativeName), options.ToList().FindIndex(option => option.Code == preference));
    private async Task OpenSettingsAsync()
    {
        if (!await GuardNavigation() || Program.Offline) return;
        var loaded = AppPreferencesStore.Load();
        var session = loaded.Warning is null ? AppPreferencesStore.BeginEdit() : AppPreferencesStore.BeginEdit(loaded.Preferences);
        var current = session.Preferences;
        var dialog = new Window { Title = L.T("App-Einstellungen"), Width = 630, Height = 710, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var languageOptions = L.LanguageChoices;
        var language = CreateLanguageChoice(current.Language, languageOptions);
        var startIds = ConnectedStartDeviceIds();
        var start = Choice(startIds.Select(id => id == "last" ? L.T("Zuletzt verwendet") : DeviceCatalog.Get(id).Name), Array.IndexOf(startIds, current.StartDevice));
        var remember = new CheckBox { Content = L.T("Fenstergröße merken"), IsChecked = current.RememberWindowSize };
        var help = new CheckBox { Content = L.T("Bedienhilfen anzeigen"), IsChecked = current.ShowHelp };
        var brightness = new CheckBox { Content = L.T("Gemeinsames Dimmen"), IsChecked = current.SharedBrightness };
        var error = Text(session.Warning ?? "", true);
        var save = new Button { Content = L.T("Speichern") }; save.Classes.Add("primary");
        save.Click += async (_, _) =>
        {
            try
            {
                var updated = current with { Language = languageOptions[language.SelectedIndex].Code, StartDevice = StartDevicePreference(startIds, start.SelectedIndex, current.StartDevice), RememberWindowSize = remember.IsChecked == true,
                    ShowHelp = help.IsChecked == true, SharedBrightness = brightness.IsChecked == true, SharedBrightnessNoticeDismissed = true };
                AppPreferencesStore.Save(session, updated); _preferences = updated; L.SetLanguage(updated.Language); await UpdateBrightnessService(); dialog.Close(); Build();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var folder = AsyncButton("Profilordner öffnen", async () =>
        { Directory.CreateDirectory(Paths.DataRoot); await Launcher.LaunchUriAsync(new Uri(Paths.DataRoot + Path.DirectorySeparatorChar)); });
        dialog.Content = new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = Column(Heading("Sprache"), language, Heading("Startgerät"), start,
            remember, help, Heading("Gemeinsames Dimmen"), brightness, Text(L.T(SharedBrightnessService.Notice), true), folder, Text("INGLORIOUS Advanced · " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "") + " · " + OsActionCatalog.CurrentSystem, true), error, Row(Button("Abbrechen", () => dialog.Close()), save)) } };
        await dialog.ShowDialog(this);
    }
    private async Task UpdateBrightnessService()
    {
        if (Program.Offline) return;
        if (_preferences.SharedBrightness && !_busy) _sharedBrightness.Start(); else await _sharedBrightness.StopAsync();
    }
}
