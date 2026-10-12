using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private readonly SharedBrightnessService _sharedBrightness = new();
    private ComboBox CreateLanguageChoice(string preference, IReadOnlyList<LanguageOption> options) =>
        Choice(options.Select(option => option.NativeName), options.ToList().FindIndex(option => option.Code == preference));
    private Control SettingsSwitchRow(string source, ToggleSwitch toggle)
    {
        toggle.OnContent = toggle.OffContent = null;
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.Margin = new Thickness(12, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(toggle, L.T(source));
        var row = new Grid { ColumnDefinitions = new("*,Auto"), MinHeight = 38 };
        row.Children.Add(new Label { Content = Text(L.T(source)), Target = toggle,
            Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
        return row;
    }
    private Task OpenSettingsAsync() => OpenSettingsAsync("Allgemein");
    private async Task OpenSettingsAsync(string initialSection)
    {
        if (!await GuardNavigation() || Program.Offline) return;
        var loaded = AppPreferencesStore.Load();
        var session = loaded.Warning is null ? AppPreferencesStore.BeginEdit() : AppPreferencesStore.BeginEdit(loaded.Preferences);
        var current = session.Preferences;
        await CreateSettingsWindow(current, session, offlinePreview: false, initialSection: initialSection).ShowDialog(this);
    }

    private Window CreateSettingsWindow(AppPreferences current, AppPreferencesEditSession? session = null, bool offlinePreview = true, string initialSection = "Allgemein")
    {
        var dialog = new Window { Title = L.T("App-Einstellungen"), Width = 960, Height = 740, MinWidth = 800, MinHeight = 640,
            RequestedThemeVariant = ThemeVariant.Dark, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var languageOptions = L.LanguageChoices;
        var language = CreateLanguageChoice(current.Language, languageOptions);
        var startIds = ConnectedStartDeviceIds();
        var start = Choice(startIds.Select(id => id == "last" ? L.T("Zuletzt verwendet") : DeviceCatalog.Get(id).Name), Array.IndexOf(startIds, current.StartDevice));
        var remember = new ToggleSwitch { IsChecked = current.RememberWindowSize };
        var help = new ToggleSwitch { IsChecked = current.ShowHelp };
        var softwareUpdates = new ToggleSwitch { IsChecked = current.CheckForSoftwareUpdates };
        var brightness = new ToggleSwitch { IsChecked = current.SharedBrightness };
        var autostart = new AppAutostartEditor(offlinePreview);
        var autostartCheck = new ToggleSwitch { Name = "AutostartCheck", IsChecked = autostart.Enabled, IsEnabled = autostart.CanEdit };
        var autostartStatus = Text(autostart.Message, true); autostartStatus.IsVisible = autostart.Message.Length > 0;
        var error = Text(offlinePreview ? L.T("Offline-Vorschau") : session?.Warning ?? "", true); error.Name = "SettingsStatus";
        var save = new Button { Name = "SaveSettings", Content = L.T("Speichern"), IsEnabled = !offlinePreview && session is not null }; save.Classes.Add("primary");
        save.Click += async (_, _) =>
        {
            if (offlinePreview || session is null) return;
            try
            {
                var updated = current with { Language = languageOptions[language.SelectedIndex].Code, StartDevice = StartDevicePreference(startIds, start.SelectedIndex, current.StartDevice), RememberWindowSize = remember.IsChecked == true,
                    ShowHelp = help.IsChecked == true, SharedBrightness = brightness.IsChecked == true, SharedBrightnessNoticeDismissed = true,
                    CheckForSoftwareUpdates = softwareUpdates.IsChecked == true };
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
        var general = Column(Text(L.T("Sprache"), true), language,
            SettingsSwitchRow("Beim Systemstart öffnen", autostartCheck), Text(AppAutostartEditor.Description, true), autostartStatus,
            SettingsSwitchRow("Fenstergröße merken", remember), SettingsSwitchRow("Bedienhilfen anzeigen", help));
        var updates = BuildSoftwareUpdateSettings(dialog, softwareUpdates, offlinePreview);
        var dimmingNotice = new Expander { Header = L.T("Hinweis"), IsExpanded = false,
            Content = Text(L.T(SharedBrightnessService.Notice), true) };
        var devices = new Grid { ColumnDefinitions = new("*,24,*") };
        devices.Children.Add(Column(Text(L.T("Startgerät"), true), start, folder,
            SettingsSwitchRow("Gemeinsames Dimmen", brightness), dimmingNotice));
        var firmware = BuildSetupSettingsSection(dialog, error, offlinePreview);
        Grid.SetColumn(firmware, 2); devices.Children.Add(firmware);
        Border Section(string title, string name, Control content)
        {
            var label = Text(L.T(title), size: 16); label.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            label.Margin = new Thickness(0, 0, 0, 5);
            return new Border { Name = name, Background = Brush("#1C1F22"), BorderBrush = Brush("#353A40"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(16),
                Child = Column(label, content) };
        }
        var overview = new Grid { Name = "SettingsOverview", ColumnDefinitions = new("*,16,*"), RowDefinitions = new("Auto,16,Auto") };
        var generalSection = Section("Allgemein", "SettingsGeneral", general);
        var updateSection = Section("Updates", "SettingsUpdates", updates);
        var deviceSection = Section("Geräte & Daten", "SettingsDevices", devices);
        overview.Children.Add(generalSection); Grid.SetColumn(updateSection, 2); overview.Children.Add(updateSection);
        Grid.SetRow(deviceSection, 2); Grid.SetColumnSpan(deviceSection, 3); overview.Children.Add(deviceSection);
        var scroll = new ScrollViewer { Name = "SettingsScroll", Content = overview,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(24) };
        var title = Text(L.T("App-Einstellungen"), size: 22); title.Margin = new Thickness(0, 0, 0, 20);
        layout.Children.Add(title); Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var footer = new Grid { Name = "SettingsFooter", ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 16, 0, 0) };
        error.VerticalAlignment = VerticalAlignment.Center; error.Margin = new Thickness(0, 0, 12, 0); footer.Children.Add(error);
        var cancel = new Button { Name = "CancelSettings", Content = L.T("Abbrechen"), IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        var actions = Row(cancel, save); actions.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        dialog.Content = layout;
        if (initialSection == "Updates") dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() => updateSection.BringIntoView());
        return dialog;
    }
    private async Task UpdateBrightnessService()
    {
        if (Program.Offline) return;
        if (_preferences.SharedBrightness && !_busy && !_firmwareFlow) _sharedBrightness.Start(); else await _sharedBrightness.StopAsync();
    }

    private Control BuildSetupSettingsSection(Window owner, TextBlock error, bool offlinePreview = true)
    {
        var install = AsyncButton("Gerät einrichten", () => OpenDeviceSetupAsync(owner: owner)); install.Name = "SetupFromSettings";
        var restore = AsyncButton("Original-Firmware wiederherstellen", () => OpenDeviceSetupAsync(operation: SetupOperation.Restore, owner: owner)); restore.Name = "RestoreFromSettings";
        var folder = AsyncButton("Sicherungsordner öffnen", () => OpenRecoveryFolderAsync(message => error.Text = message));
        folder.Name = "OpenRecoveryFolder";
        install.IsEnabled = restore.IsEnabled = folder.IsEnabled = !offlinePreview;
        return Column(Text(L.T("Firmware"), true), install, restore,
            Text(L.T("Deine Original-Firmware wird bei der Einrichtung auf diesem Computer gesichert."), true), folder);
    }

    private async Task OpenRecoveryFolderAsync(Action<string> reportError)
    {
        if (Program.Offline || _firmwareFlow) return;
        try
        {
            Directory.CreateDirectory(DeviceSetupService.RecoveryDirectory);
            if (!await Launcher.LaunchUriAsync(new Uri(DeviceSetupService.RecoveryDirectory + Path.DirectorySeparatorChar)))
                reportError(L.T("Der Sicherungsordner konnte nicht geöffnet werden."));
        }
        catch { reportError(L.T("Der Sicherungsordner konnte nicht geöffnet werden.")); }
    }

    private static Control[] SettingsControls(Window window)
    {
        return window.GetLogicalDescendants().OfType<Control>().ToArray();
    }

    private void RunSettingsLayoutChecks(Action<bool, string> check)
    {
        var window = CreateSettingsWindow(_preferences, offlinePreview: true);
        var controls = SettingsControls(window);
        check(!controls.OfType<TabControl>().Any() && new[] { "SettingsGeneral", "SettingsUpdates", "SettingsDevices" }
            .All(name => controls.Single(control => control.Name == name).IsVisible), "Settings: all three sections are present without tab switching");
        check(window.MinWidth == 800 && window.MinHeight == 640,
            "Settings: overview supports the reviewed minimum size");
        check(window.RequestedThemeVariant == ThemeVariant.Dark, "Settings: overview keeps the dark theme");
        var footer = controls.Single(control => control.Name == "SettingsFooter");
        check(ReferenceEquals(footer.Parent, window.Content) && Grid.GetRow(footer) == 2,
            "Settings: save and cancel are outside the scrolling overview");
        var save = controls.OfType<Button>().Single(control => control.Name == "SaveSettings");
        check(save.GetLogicalAncestors().Contains(footer) && !save.IsEnabled,
            "Settings: offline Save stays in the fixed footer and cannot write preferences");
        var start = controls.OfType<ToggleSwitch>().Single(control => control.Name == "AutostartCheck");
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        check(ReferenceEquals(start, SettingsControls(window).OfType<ToggleSwitch>().Single(control => control.Name == "AutostartCheck")),
            "Settings: resizing preserves unsaved form controls");
        foreach (var name in new[] { "SetupFromSettings", "RestoreFromSettings", "OpenRecoveryFolder" })
            check(!controls.OfType<Button>().Single(control => control.Name == name).IsEnabled,
                "Settings: offline preview disables " + name);
    }
}
