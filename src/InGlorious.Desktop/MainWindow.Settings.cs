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
        await CreateSettingsWindow(current, session, offlinePreview: false).ShowDialog(this);
    }

    private Window CreateSettingsWindow(AppPreferences current, AppPreferencesEditSession? session = null, bool offlinePreview = true)
    {
        var dialog = new Window { Title = L.T("App-Einstellungen"), Width = 630, Height = 710, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var languageOptions = L.LanguageChoices;
        var language = CreateLanguageChoice(current.Language, languageOptions);
        var startIds = ConnectedStartDeviceIds();
        var start = Choice(startIds.Select(id => id == "last" ? L.T("Zuletzt verwendet") : DeviceCatalog.Get(id).Name), Array.IndexOf(startIds, current.StartDevice));
        var remember = new CheckBox { Content = L.T("Fenstergröße merken"), IsChecked = current.RememberWindowSize };
        var help = new CheckBox { Content = L.T("Bedienhilfen anzeigen"), IsChecked = current.ShowHelp };
        var brightness = new CheckBox { Content = L.T("Gemeinsames Dimmen"), IsChecked = current.SharedBrightness };
        var autostart = new AppAutostartEditor(offlinePreview);
        var autostartCheck = new CheckBox { Name = "AutostartCheck", Content = Text(L.T("Beim Systemstart öffnen")), IsChecked = autostart.Enabled, IsEnabled = autostart.CanEdit };
        var autostartStatus = Text(autostart.Message, true); autostartStatus.IsVisible = autostart.Message.Length > 0;
        var error = Text(offlinePreview ? L.T("Offline-Vorschau") : session?.Warning ?? "", true);
        var save = new Button { Name = "SaveSettings", Content = L.T("Speichern"), IsEnabled = !offlinePreview && session is not null }; save.Classes.Add("primary");
        save.Click += async (_, _) =>
        {
            if (offlinePreview || session is null) return;
            try
            {
                var updated = current with { Language = languageOptions[language.SelectedIndex].Code, StartDevice = StartDevicePreference(startIds, start.SelectedIndex, current.StartDevice), RememberWindowSize = remember.IsChecked == true,
                    ShowHelp = help.IsChecked == true, SharedBrightness = brightness.IsChecked == true, SharedBrightnessNoticeDismissed = true };
                AppPreferencesStore.Validate(updated);
                if (!autostart.Save(autostartCheck.IsChecked == true, () => AppPreferencesStore.Save(session, updated)))
                {
                    autostartCheck.IsChecked = autostart.Enabled; autostartCheck.IsEnabled = autostart.CanEdit;
                    autostartStatus.Text = autostart.Message; autostartStatus.IsVisible = autostart.Message.Length > 0;
                    error.Text = ""; return;
                }
                _preferences = updated; L.SetLanguage(updated.Language); await UpdateBrightnessService(); dialog.Close(); Build();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var folder = AsyncButton("Profilordner öffnen", async () =>
        { if (offlinePreview) return; Directory.CreateDirectory(Paths.DataRoot); await Launcher.LaunchUriAsync(new Uri(Paths.DataRoot + Path.DirectorySeparatorChar)); });
        folder.IsEnabled = !offlinePreview;
        dialog.Content = new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = Column(Heading("Sprache"), language, Heading("Startgerät"), start,
            remember, help, autostartCheck, Text(AppAutostartEditor.Description, true), autostartStatus,
            Heading("Gemeinsames Dimmen"), brightness, Text(L.T(SharedBrightnessService.Notice), true), folder,
            BuildSetupSettingsSection(dialog, error),
            Text("INGLORIOUS Advanced · " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "") + " · " + OsActionCatalog.CurrentSystem, true), error, Row(Button("Abbrechen", () => dialog.Close()), save)) } };
        return dialog;
    }
    private async Task UpdateBrightnessService()
    {
        if (Program.Offline) return;
        if (_preferences.SharedBrightness && !_busy && !_firmwareFlow) _sharedBrightness.Start(); else await _sharedBrightness.StopAsync();
    }

    private Control BuildSetupSettingsSection(Window owner, TextBlock error)
    {
        var install = AsyncButton("Gerät einrichten", () => OpenDeviceSetupAsync(owner: owner)); install.Name = "SetupFromSettings";
        var restore = AsyncButton("Original-Firmware wiederherstellen", () => OpenDeviceSetupAsync(operation: SetupOperation.Restore, owner: owner)); restore.Name = "RestoreFromSettings";
        var folder = AsyncButton("Sicherungsordner öffnen", async () =>
        {
            if (Program.Offline || _firmwareFlow) return;
            try
            {
                Directory.CreateDirectory(DeviceSetupService.RecoveryDirectory);
                if (!await Launcher.LaunchUriAsync(new Uri(DeviceSetupService.RecoveryDirectory + Path.DirectorySeparatorChar)))
                    error.Text = L.T("Der Sicherungsordner konnte nicht geöffnet werden.");
            }
            catch { error.Text = L.T("Der Sicherungsordner konnte nicht geöffnet werden."); }
        });
        folder.Name = "OpenRecoveryFolder";
        return Column(Heading("Firmware"), install, restore,
            Text(L.T("Deine Original-Firmware wird bei der Einrichtung auf diesem Computer gesichert."), true), folder);
    }
}
