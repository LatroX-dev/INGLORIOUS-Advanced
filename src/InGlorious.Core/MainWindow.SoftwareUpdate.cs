using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private readonly CancellationTokenSource _softwareUpdateCancellation = new();
    private bool _softwareUpdateLoopStarted, _softwareUpdateChecking;
    private SoftwareUpdateInfo? _availableSoftwareUpdate;
    private SoftwareUpdateInstallation? _pendingSoftwareInstallation;
    private Action<SoftwareUpdateInstallation>? _softwareInstallerStartOverride;

    protected override void OnClosing(CancelEventArgs e)
    {
        // base raises every registered Closing handler, including a later firmware veto.
        base.OnClosing(e);
        if (!e.Cancel) StartPendingSoftwareInstaller(e);
    }

    private async Task CheckSoftwareUpdateOnStartupAsync()
    {
        if (_softwareUpdateLoopStarted || _screenshotMode || _startupClosed) return;
        _softwareUpdateLoopStarted = true;
        try
        {
            while (!_softwareUpdateCancellation.IsCancellationRequested)
            {
                await CheckSoftwareUpdateInBackgroundAsync();
                await Task.Delay(TimeSpan.FromHours(12), _softwareUpdateCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_softwareUpdateCancellation.IsCancellationRequested) { }
    }

    private async Task CheckSoftwareUpdateInBackgroundAsync()
    {
        if (_softwareUpdateChecking || _screenshotMode || _startupClosed || !_preferences.CheckForSoftwareUpdates ||
            SoftwareUpdateService.CurrentPlatform is not { } platform) return;
        _softwareUpdateChecking = true;
        try
        {
            using var service = new SoftwareUpdateService(Paths.UserDataRoot);
            var result = await service.CheckAsync(SoftwareUpdateService.CurrentVersion, platform, _softwareUpdateCancellation.Token);
            if (_startupClosed || !_preferences.CheckForSoftwareUpdates) return;
            _availableSoftwareUpdate = result.Update;
            SoftwareUpdateNotice.Visibility = result.Update is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.Http.HttpRequestException)
        { /* Background checks never interrupt editing or device setup. Manual checks explain failures. */ }
        finally { _softwareUpdateChecking = false; }
    }

    private void SoftwareUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshotMode || _startupLoading || _startupFinishing || _readingDevice || _checkingFirmwareGuide) return;
        var dialog = new SoftwareUpdateWindow(_availableSoftwareUpdate) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Installation is { } installation) InstallSoftwareUpdate(installation);
    }

    private void InstallSoftwareUpdate(SoftwareUpdateInstallation installation)
    {
        if (_screenshotMode || _startupClosed || _readingDevice || _checkingFirmwareGuide || _startupLoading || _startupFinishing ||
            OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
        { Status(L.T("Beende zuerst den laufenden Vorgang. Danach kannst du das Software-Update installieren.")); return; }
        _pendingSoftwareInstallation = installation;
        try { RequestApplicationExit(); }
        finally { _pendingSoftwareInstallation = null; }
    }

    private void StartPendingSoftwareInstaller(CancelEventArgs e)
    {
        if (_pendingSoftwareInstallation is not { } installation || (_screenshotMode && _softwareInstallerStartOverride is null)) return;
        if (_startupClosed || _readingDevice || _checkingFirmwareGuide || _startupLoading || _startupFinishing ||
            OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
        {
            e.Cancel = true;
            Status(L.T("Beende zuerst den laufenden Vorgang. Danach kannst du das Software-Update installieren."));
            return;
        }
        try
        {
            if (_screenshotMode) { _softwareInstallerStartOverride!(installation); return; }
            using var service = new SoftwareUpdateService(Paths.UserDataRoot);
            // Retain a read-only sharing handle through launch, so the verified executable
            // cannot be replaced or written between verification and ShellExecute.
            using var packageLock = new FileStream(installation.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            service.VerifyDownloadedFile(installation.Update, installation.Path);
            var start = new ProcessStartInfo(installation.Path) { UseShellExecute = true };
            // The normal installer keeps its own running-app/firmware guards and user choices.
            if (L.CurrentLanguage == "de") start.ArgumentList.Add("/LANG=german");
            else start.ArgumentList.Add("/LANG=english");
            if (Process.Start(start) is not { } installer) throw new IOException("Installer did not start.");
            installer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            e.Cancel = true;
            Status(L.T("Die Installation konnte nicht gestartet werden. Lade das Update erneut herunter."));
        }
    }

    private void RunSoftwareUpdateChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Software update lifecycle checks require offline mode.");
        var pending = _pendingSoftwareInstallation; var launch = _softwareInstallerStartOverride;
        bool reading = _readingDevice, guide = _checkingFirmwareGuide, loading = _startupLoading, finishing = _startupFinishing, closed = _startupClosed;
        int launches = 0;
        var update = new SoftwareUpdateInfo("1.0.0", "Synthetic update", true, "INGLORIOUS-Advanced-1.0.0-Setup.exe", 1, new string('0', 64));
        CancelEventHandler veto = (_, e) => e.Cancel = true;
        Window? dialog = null;
        try
        {
            CheckSoftwareUpdateOnStartupAsync().GetAwaiter().GetResult();
            CheckSoftwareUpdateInBackgroundAsync().GetAwaiter().GetResult();
            check(!_softwareUpdateLoopStarted && !_softwareUpdateChecking, "offline mode never starts an update loop or background request");
            _readingDevice = _checkingFirmwareGuide = _startupLoading = _startupFinishing = _startupClosed = false;
            _softwareInstallerStartOverride = _ => launches++;
            _pendingSoftwareInstallation = null;
            var ordinaryClose = new CancelEventArgs(); OnClosing(ordinaryClose);
            check(launches == 0, "ordinary close never launches a software installer");
            _pendingSoftwareInstallation = new(update, "offline-only-never-executed");
            Closing += veto;
            var canceled = new CancelEventArgs(); OnClosing(canceled);
            check(canceled.Cancel && launches == 0, "a later Closing veto prevents installation after an unsaved-profile or firmware cancellation");
            Closing -= veto;
            var approved = new CancelEventArgs(); OnClosing(approved);
            check(!approved.Cancel && launches == 1, "installer launch runs once only after every closing handler approved");
            _softwareInstallerStartOverride = _ => throw new IOException("Synthetic changed-package/launch failure");
            var failed = new CancelEventArgs(); OnClosing(failed);
            check(failed.Cancel && launches == 1 && !_startupClosed, "verification or installer launch failure keeps the app open");
            _softwareInstallerStartOverride = _ => launches++;
            var guards = new (string Name, Action<bool> Set)[]
            {
                ("device read", value => _readingDevice = value), ("firmware guide", value => _checkingFirmwareGuide = value),
                ("startup read", value => _startupLoading = value), ("startup completion", value => _startupFinishing = value),
                ("closed application", value => _startupClosed = value)
            };
            foreach (var guard in guards)
            {
                guard.Set(true);
                var blocked = new CancelEventArgs(); StartPendingSoftwareInstaller(blocked);
                check(blocked.Cancel && launches == 1, "software installation is blocked during " + guard.Name);
                guard.Set(false);
            }
            dialog = new Window { Owner = this, Width = 120, Height = 70, ShowInTaskbar = false }; dialog.Show();
            var owned = new CancelEventArgs(); StartPendingSoftwareInstaller(owned);
            check(owned.Cancel && launches == 1, "an open owned dialog prevents software installation");
            dialog.Close(); dialog = null;
            _softwareInstallerStartOverride = null;
            var offline = new CancelEventArgs(); StartPendingSoftwareInstaller(offline);
            check(launches == 1 && !offline.Cancel, "offline mode never reaches a real installer or update cache");
        }
        finally
        {
            Closing -= veto; dialog?.Close();
            _pendingSoftwareInstallation = pending; _softwareInstallerStartOverride = launch;
            _readingDevice = reading; _checkingFirmwareGuide = guide; _startupLoading = loading; _startupFinishing = finishing; _startupClosed = closed;
        }
    }
}
