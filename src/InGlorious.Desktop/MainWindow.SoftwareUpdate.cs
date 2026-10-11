using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _softwareUpdateLifetime = new();
    private CancellationTokenSource? _softwareUpdateOperation;
    private DispatcherTimer? _softwareUpdateTimer;
    private SoftwareUpdateInfo? _availableSoftwareUpdate;
    private Button? _softwareUpdateHint;
    private Action? _softwareUpdateChanged;
    private bool _softwareUpdateChecked, _softwareUpdateBusy, _softwareUpdateDownloading;
    private double _softwareUpdateProgress;
    private string? _softwareUpdateMessage, _softwareUpdateDownloadPath;

    private Button BuildSoftwareUpdateHint()
    {
        var button = AsyncButton("Update verfügbar", () => OpenSettingsAsync("Updates"));
        button.Name = "SoftwareUpdateHint";
        button.FontSize = 12;
        button.Classes.Add("primary");
        _softwareUpdateHint = button;
        RefreshSoftwareUpdateViews();
        return button;
    }

    private void RefreshSoftwareUpdateViews()
    {
        if (_startupWindowClosed) return;
        if (_softwareUpdateHint is not null)
            _softwareUpdateHint.IsVisible = _availableSoftwareUpdate is not null && !Program.Offline;
        _softwareUpdateChanged?.Invoke();
    }

    private async Task CheckSoftwareUpdateOnStartupAsync()
    {
        if (Program.Offline || _softwareUpdateChecked || _startupWindowClosed) return;
        _softwareUpdateChecked = true;
        _softwareUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(12) };
        _softwareUpdateTimer.Tick += async (_, _) =>
        {
            if (!_startupWindowClosed && _preferences.CheckForSoftwareUpdates)
                await CheckSoftwareUpdateAsync(automatic: true);
        };
        _softwareUpdateTimer.Start();
        if (_preferences.CheckForSoftwareUpdates) await CheckSoftwareUpdateAsync(automatic: true);
    }

    private async Task CheckSoftwareUpdateAsync(bool automatic = false)
    {
        if (Program.Offline || _startupWindowClosed || _softwareUpdateBusy || (!automatic && (_busy || _firmwareFlow))) return;
        if (automatic && !_preferences.CheckForSoftwareUpdates) return;
        if (SoftwareUpdateService.CurrentPlatform is not { } platform)
        {
            if (!automatic) { _softwareUpdateMessage = "Für dieses System ist noch kein Update-Paket verfügbar."; RefreshSoftwareUpdateViews(); }
            return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_softwareUpdateLifetime.Token);
        _softwareUpdateOperation = operation;
        _softwareUpdateBusy = true;
        if (!automatic) _softwareUpdateMessage = "Suche nach Updates …";
        RefreshSoftwareUpdateViews();
        try
        {
            using var service = new SoftwareUpdateService(Paths.UserDataRoot);
            var result = await service.CheckAsync(SoftwareUpdateService.CurrentVersion, platform, operation.Token);
            if (_startupWindowClosed || (automatic && !_preferences.CheckForSoftwareUpdates)) return;
            if (result.Update?.Version != _availableSoftwareUpdate?.Version) _softwareUpdateDownloadPath = null;
            _availableSoftwareUpdate = result.Update;
            _softwareUpdateMessage = result.Status switch
            {
                SoftwareUpdateStatus.Current => "Deine App ist aktuell.",
                SoftwareUpdateStatus.PackageUnavailable => "Für dieses System ist noch kein Update-Paket verfügbar.",
                _ => null
            };
        }
        catch (OperationCanceledException) { if (!automatic) _softwareUpdateMessage = null; }
        catch (Exception)
        {
            // Automatic checks are deliberately silent. No technical exception text reaches the UI.
            if (!automatic) _softwareUpdateMessage = "Updates konnten nicht abgerufen werden. Prüfe deine Internetverbindung und versuche es erneut.";
        }
        finally
        {
            _softwareUpdateOperation = null;
            _softwareUpdateBusy = false;
            RefreshSoftwareUpdateViews();
        }
    }

    private Control BuildSoftwareUpdateSettings(Window owner, ToggleSwitch automatic, bool offlinePreview)
    {
        var status = Text("", true); status.Name = "SoftwareUpdateStatus";
        var notes = Text(""); notes.Name = "SoftwareUpdateNotes";
        var notesBox = new Expander { Header = L.T("Changelog"), Content = notes, IsExpanded = false };
        var progress = new ProgressBar { Name = "SoftwareUpdateProgress", Minimum = 0, Maximum = 1, Height = 5 };
        var check = AsyncButton("Nach Updates suchen", () => CheckSoftwareUpdateAsync()); check.Name = "CheckSoftwareUpdate";
        var download = AsyncButton("Update herunterladen", DownloadSoftwareUpdateAsync, true); download.Name = "DownloadSoftwareUpdate";
        var cancel = new Button { Content = L.T("Abbrechen"), Name = "CancelSoftwareUpdate" };
        cancel.Click += (_, _) => _softwareUpdateOperation?.Cancel();
        var folder = AsyncButton("Download-Ordner öffnen", OpenSoftwareUpdateFolderAsync); folder.Name = "OpenSoftwareUpdateFolder";
        var instruction = Text(SoftwareUpdateInstallInstruction(SoftwareUpdateService.CurrentPlatform), true);
        instruction.Name = "SoftwareUpdateInstallInstruction";
        automatic.Name = "AutomaticSoftwareUpdates";
        automatic.IsEnabled = !offlinePreview;
        void Refresh()
        {
            var available = _availableSoftwareUpdate;
            status.Text = _softwareUpdateDownloading ? L.F($"Herunterladen … {(int)(_softwareUpdateProgress * 100)} %")
                : _softwareUpdateMessage is { } message ? L.T(message)
                : available is not null ? L.T("Neue Version verfügbar: ") + available.Version : "";
            status.IsVisible = status.Text?.Length > 0;
            notes.Text = available?.Notes ?? "";
            notesBox.IsVisible = !string.IsNullOrWhiteSpace(notes.Text);
            check.IsEnabled = !offlinePreview && !_softwareUpdateBusy && !_firmwareFlow && !_busy;
            download.IsVisible = available is not null && _softwareUpdateDownloadPath is null;
            download.IsEnabled = !offlinePreview && !_softwareUpdateBusy && !_firmwareFlow && !_busy;
            progress.IsVisible = _softwareUpdateDownloading;
            progress.Value = _softwareUpdateProgress;
            cancel.IsVisible = _softwareUpdateBusy;
            cancel.IsEnabled = !offlinePreview;
            folder.IsVisible = _softwareUpdateDownloadPath is not null;
            folder.IsEnabled = !offlinePreview && !_softwareUpdateBusy && !_firmwareFlow && !_busy;
            instruction.IsVisible = _softwareUpdateDownloadPath is not null;
        }
        Refresh();
        if (!offlinePreview)
        {
            _softwareUpdateChanged += Refresh;
            owner.Closed += (_, _) =>
            {
                _softwareUpdateChanged -= Refresh;
                if (_softwareUpdateDownloading) _softwareUpdateOperation?.Cancel();
            };
        }
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { check, download, cancel, folder })
        { button.Margin = new Avalonia.Thickness(0, 0, 7, 5); actions.Children.Add(button); }
        return Column(Text(L.F($"Installierte Version: {SoftwareUpdateService.CurrentVersion}"), true),
            Text(L.T("Updates für die App. Die Firmware deiner Tastatur wird separat unter „Gerät“ aktualisiert."), true),
            SettingsSwitchRow("Automatisch nach Updates suchen", automatic), status, notesBox, progress, actions, instruction);
    }

    private static string SoftwareUpdateInstallInstruction(SoftwareUpdatePlatform? platform) => L.T(platform switch
    {
        SoftwareUpdatePlatform.MacArm64 or SoftwareUpdatePlatform.MacX64 => "macOS: Entpacke das Paket, beende INGLORIOUS Advanced und ersetze die App im Ordner Programme.",
        SoftwareUpdatePlatform.LinuxX64 => "Linux: Entpacke das Paket, beende INGLORIOUS Advanced und starte die neue Version aus dem entpackten Ordner.",
        _ => "Windows: Beende INGLORIOUS Advanced und öffne die heruntergeladene Installationsdatei."
    });

    private async Task DownloadSoftwareUpdateAsync()
    {
        if (Program.Offline || _startupWindowClosed || _softwareUpdateBusy || _firmwareFlow || _busy || _availableSoftwareUpdate is not { } update) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_softwareUpdateLifetime.Token);
        _softwareUpdateOperation = operation;
        _softwareUpdateBusy = _softwareUpdateDownloading = true;
        _softwareUpdateProgress = 0;
        _softwareUpdateMessage = null;
        RefreshSoftwareUpdateViews();
        try
        {
            using var service = new SoftwareUpdateService(Paths.UserDataRoot);
            var progress = new Progress<double>(value =>
            {
                if (_startupWindowClosed || !ReferenceEquals(_softwareUpdateOperation, operation)) return;
                _softwareUpdateProgress = Math.Clamp(value, 0, 1);
                RefreshSoftwareUpdateViews();
            });
            var path = await service.DownloadAsync(update, progress, operation.Token);
            if (_startupWindowClosed) return;
            _softwareUpdateDownloadPath = path;
            _softwareUpdateMessage = "Download geprüft. Öffne den Ordner, um die neue Version zu installieren.";
        }
        catch (OperationCanceledException) { _softwareUpdateMessage = "Download abgebrochen."; }
        catch (Exception) { _softwareUpdateMessage = "Das Update konnte nicht heruntergeladen werden. Bitte versuche es erneut."; }
        finally
        {
            _softwareUpdateOperation = null;
            _softwareUpdateBusy = _softwareUpdateDownloading = false;
            RefreshSoftwareUpdateViews();
        }
    }

    private async Task OpenSoftwareUpdateFolderAsync()
    {
        if (Program.Offline || _startupWindowClosed || _softwareUpdateBusy || _firmwareFlow || _busy ||
            _softwareUpdateDownloadPath is not { } path || _availableSoftwareUpdate is not { } update) return;
        try
        {
            using var service = new SoftwareUpdateService(Paths.UserDataRoot);
            service.VerifyDownloadedFile(update, path);
            var folder = Path.GetDirectoryName(path) ?? throw new InvalidDataException();
            if (!await Launcher.LaunchUriAsync(new Uri(folder + Path.DirectorySeparatorChar))) throw new IOException();
        }
        catch (Exception)
        {
            _softwareUpdateDownloadPath = null;
            _softwareUpdateMessage = "Der Download-Ordner konnte nicht geöffnet werden.";
            RefreshSoftwareUpdateViews();
        }
    }

    private void StopSoftwareUpdates()
    {
        _softwareUpdateTimer?.Stop();
        _softwareUpdateTimer = null;
        _softwareUpdateLifetime.Cancel();
        _softwareUpdateLifetime.Dispose();
        _softwareUpdateChanged = null;
    }

    private async Task RunSoftwareUpdateChecks(Action<bool, string> check)
    {
        if (!Program.Offline) throw new InvalidOperationException("Software-update UI checks require offline mode.");
        await CheckSoftwareUpdateOnStartupAsync();
        await CheckSoftwareUpdateAsync();
        check(!_softwareUpdateChecked && !_softwareUpdateBusy && _softwareUpdateOperation is null && _softwareUpdateTimer is null,
            "software update: offline startup and manual checks create no service or timer");
        check(_softwareUpdateHint is { IsVisible: false }, "software update: unknown version never shows an update notice");
        var fixture = new SoftwareUpdateInfo("9.8.7", "Improved lighting previews.", true,
            "INGLORIOUS-Advanced-9.8.7-linux-x64.tar.gz", 123, new string('A', 64));
        _availableSoftwareUpdate = fixture;
        try
        {
            RefreshSoftwareUpdateViews();
            check(_softwareUpdateHint is { IsVisible: false }, "software update: offline screenshot never advertises a fabricated update");
            var settings = CreateSettingsWindow(_preferences, offlinePreview: true, initialSection: "Updates");
            var controls = SettingsControls(settings);
            var automatic = controls.OfType<ToggleSwitch>().Single(control => control.Name == "AutomaticSoftwareUpdates");
            check(automatic.IsChecked == _preferences.CheckForSoftwareUpdates && !automatic.IsEnabled,
                "software update: preview shows the saved preference without allowing edits");
            foreach (var name in new[] { "CheckSoftwareUpdate", "DownloadSoftwareUpdate", "CancelSoftwareUpdate", "OpenSoftwareUpdateFolder" })
                check(!controls.OfType<Button>().Single(control => control.Name == name).IsEnabled,
                    "software update: offline preview disables " + name);
            check(controls.OfType<TextBlock>().Single(control => control.Name == "SoftwareUpdateNotes").Text == fixture.Notes,
                "software update: release notes remain plain English text");
            check(!controls.Single(control => control.Name == "SoftwareUpdateInstallInstruction").IsVisible,
                "software update: installation instructions appear only after a verified download");
            check(!controls.Single(control => control.Name == "SoftwareUpdateProgress").IsVisible,
                "software update: idle progress is hidden");
            await DownloadSoftwareUpdateAsync();
            await OpenSoftwareUpdateFolderAsync();
            check(_softwareUpdateDownloadPath is null && !_softwareUpdateBusy && _softwareUpdateOperation is null,
                "software update: offline download and folder actions cannot access user data or start processes");
        }
        finally { _availableSoftwareUpdate = null; RefreshSoftwareUpdateViews(); }
    }
}
