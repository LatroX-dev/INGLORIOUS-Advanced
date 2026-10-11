using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace InGlorious.Core;

public partial class AppSettingsWindow : Window
{
    private readonly bool _offlinePreview;
    private readonly AppPreferencesEditSession? _session;
    private readonly AppAutostartEditor _autostart;
    public AppPreferences Preferences { get; private set; }
    public Func<Task>? SetupAction { get; set; }
    public Func<Task>? RestoreSetupAction { get; set; }
    internal SoftwareUpdateInstallation? RequestedSoftwareUpdate { get; private set; }
    private bool _openingSetup;

    public AppSettingsWindow(AppPreferences current, bool offlinePreview = false, IReadOnlyList<DeviceDefinition>? connectedDevices = null)
    {
        AppPreferencesStore.Validate(current);
        Preferences = current.ClonePreferences();
        _offlinePreview = offlinePreview;
        _autostart = new AppAutostartEditor(offlinePreview);
        InitializeComponent();
        LanguageChoice.ItemsSource = L.LanguageChoices;
        LanguageChoice.SelectedValue = current.Language;
        StartDeviceChoice.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = L.T("Zuletzt benutztes Gerät"), Tag = "last" });
        foreach (var device in (connectedDevices ?? []).DistinctBy(device => device.Id))
            StartDeviceChoice.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = device.Name, Tag = device.Id,
                ToolTip = device.IsOfflineOnly ? L.T("Offline-Profil") : device.Subtitle });
        StartDeviceChoice.SelectedValue = current.StartDevice;
        RememberSizeCheck.IsChecked = current.RememberWindowSize;
        ShowHelpCheck.IsChecked = current.ShowHelp;
        SoftwareUpdateCheck.IsChecked = current.CheckForSoftwareUpdates;
        SoftwareUpdateButton.IsEnabled = !offlinePreview;
        AutostartDescriptionText.Text = AppAutostartEditor.Description;
        RefreshAutostartControl();
        var assembly = typeof(AppSettingsWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3) ?? L.T("unbekannt");
        VersionText.Text = L.T("INGLORIOUS Advanced · Version ") + version;
        if (_offlinePreview)
        {
            SaveButton.IsEnabled = OpenFolderButton.IsEnabled = false;
            StatusText.Text = L.T("Offline-Vorschau");
            return;
        }
        try
        {
            _session = AppPreferencesStore.BeginEdit(current);
            StatusText.Text = _session.Warning ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SaveButton.IsEnabled = false;
            StatusText.Text = L.T("Die App-Einstellungen können derzeit nicht gespeichert werden. ") + ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_offlinePreview || _session is null) return;
        var changed = Preferences with
        {
            Language = LanguageChoice.SelectedValue as string ?? L.SystemLanguage,
            StartDevice = StartDeviceChoice.SelectedValue as string ?? Preferences.StartDevice,
            RememberWindowSize = RememberSizeCheck.IsChecked == true,
            ShowHelp = ShowHelpCheck.IsChecked == true,
            CheckForSoftwareUpdates = SoftwareUpdateCheck.IsChecked == true
        };
        try
        {
            AppPreferencesStore.Validate(changed);
            if (!_autostart.Save(AutostartCheck.IsChecked == true, () => AppPreferencesStore.Save(_session, changed)))
            {
                RefreshAutostartControl();
                StatusText.Text = "";
                return;
            }
            Preferences = changed;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { StatusText.Text = L.T("Einstellungen wurden nicht gespeichert. ") + ex.Message; }
    }

    private void RefreshAutostartControl()
    {
        AutostartCheck.IsChecked = _autostart.Enabled;
        AutostartCheck.IsEnabled = _autostart.CanEdit;
        AutostartStatusText.Text = _autostart.Message;
        AutostartStatusText.Visibility = string.IsNullOrEmpty(_autostart.Message) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static void RunOfflineChecks(Action<bool, string> check)
    {
        AppAutostartEditor.RunOfflineChecks(check);
        var preview = new AppSettingsWindow(new AppPreferences(), offlinePreview: true);
        check(preview.AutostartCheck.IsChecked == false && !preview.AutostartCheck.IsEnabled && !preview.SaveButton.IsEnabled,
            "Settings: offline startup switch is neutral and cannot modify the system");
        preview.Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_offlinePreview) return;
        try
        {
            Directory.CreateDirectory(Paths.DataRoot);
            Process.Start(new ProcessStartInfo { FileName = Paths.DataRoot, UseShellExecute = true });
            StatusText.Text = "";
        }
        catch (Exception ex) { StatusText.Text = L.T("Der Profilordner konnte nicht geöffnet werden. ") + ex.Message; }
    }

    private async void SetupDevice_Click(object sender, RoutedEventArgs e) => await OpenSetupAsync(SetupOperation.Install);
    private void SoftwareUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_offlinePreview || _openingSetup) return;
        var dialog = new SoftwareUpdateWindow { Owner = this };
        dialog.ShowDialog();
        if (dialog.Installation is null) return;
        // Persist the choices visible in Settings through the existing conflict-safe save.
        RequestedSoftwareUpdate = dialog.Installation;
        Save_Click(sender, e);
        if (IsVisible) RequestedSoftwareUpdate = null;
    }
    private async void RestoreDevice_Click(object sender, RoutedEventArgs e) => await OpenSetupAsync(SetupOperation.Restore);
    private async Task OpenSetupAsync(SetupOperation operation)
    {
        if (_openingSetup) return;
        _openingSetup = true; SetupDeviceButton.IsEnabled = RestoreDeviceButton.IsEnabled = false;
        try
        {
            var action = operation == SetupOperation.Install ? SetupAction : RestoreSetupAction;
            if (action is not null) await action();
            else if (_offlinePreview) new DeviceSetupWindow(operation: operation, offlinePreview: true) { Owner = this }.ShowDialog();
        }
        finally { _openingSetup = false; SetupDeviceButton.IsEnabled = RestoreDeviceButton.IsEnabled = true; }
    }
}
