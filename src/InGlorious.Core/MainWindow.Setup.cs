using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private async void SetupDevice_Click(object sender, RoutedEventArgs e) => await OpenDeviceSetupAsync();
    private async void RestoreSetupDevice_Click(object sender, RoutedEventArgs e) => await OpenDeviceSetupAsync(operation: SetupOperation.Restore);

    internal async Task OpenDeviceSetupAsync(string? preferredDeviceId = null, SetupOperation operation = SetupOperation.Install, Window? owner = null, string? preferredInstanceId = null)
    {
        if (_readingDevice || _checkingFirmwareGuide || _startupLoading || _startupFinishing) return;
        _checkingFirmwareGuide = true;
        CoreImportSelection? importedCore = null;
        try
        {
            await _sharedBrightness.StopAsync();
            var dialog = new DeviceSetupWindow(preferredDeviceId, operation, _screenshotMode, preferredInstanceId) { Owner = owner ?? this };
            dialog.ShowDialog();
            if (dialog.DeviceStateMayHaveChanged && dialog.DeviceId is { } id)
            {
                _confirmedDeviceSaves.Remove(id); _firmwareAssessments.Remove(id);
                UpdateSourceHint(); RefreshFirmwareVersionLabel();
            }
            if (dialog.Completed) Status(L.T("Prüfe jetzt die Tasten und die Beleuchtung."));
            if (dialog.Completed) importedCore = dialog.ImportedCoreSelection;
        }
        catch (Exception ex) { Status(DeviceSetupService.ForUser(ex)); }
        finally
        {
            _checkingFirmwareGuide = false;
            UpdateSetupDeviceButtons();
            if (importedCore is not null) AdoptSetupCoreImport(importedCore);
            if (!_startupClosed) { RequestConnectionRefresh(); StartSharedBrightnessIfReady(); }
        }
    }

    private void RunDeviceSetupChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Setup checks require offline mode.");
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(board.DeviceId), out var current);
            var info = new OnboardInfo(board.DeviceId, 1, 0, 1, 0, 0);
            var version = new OnboardFirmwareVersion(board.DeviceId, current, 1);
            check(ClassifySetupFirmware(info, board.DeviceId, version) == FirmwareGuideState.Ready, board.DeviceId + ": catalog target is current, including experimental models");
            check(ClassifySetupFirmware(info, board.DeviceId, version with { Version = new(0, 1, 0) }) == FirmwareGuideState.UpdateRequired, board.DeviceId + ": older actual version permits update discovery");
            check(ClassifySetupFirmware(info, board.DeviceId, version with { Version = new(99, 0, 0) }) == FirmwareGuideState.NewerVersion, board.DeviceId + ": newer version never offers a downgrade");
            check(ClassifySetupFirmware(info, board.DeviceId, null) == FirmwareGuideState.Unverified && ClassifySetupFirmware(info, board.DeviceId, version with { BuildId = 0 }) == FirmwareGuideState.Unverified, board.DeviceId + ": absent or invalid version never invents an update");
            check(ClassifySetupFirmware(info with { Flags = 5 }, board.DeviceId, version) == FirmwareGuideState.Unverified && ClassifySetupFirmware(info with { Flags = 0 }, board.DeviceId, version) == FirmwareGuideState.Unverified, board.DeviceId + ": broken or nonpersistent runtime stays unverified");
            check(ClassifySetupFirmware(info with { StagingBytes = 1 }, board.DeviceId, version) == FirmwareGuideState.Unverified, board.DeviceId + ": staged configuration blocks firmware update discovery");
        }
        foreach (var operation in Enum.GetValues<SetupOperation>())
        {
            var dialog = new DeviceSetupWindow("gmmk3-75-iso", operation, offlinePreview: true);
            try { dialog.RunOfflineChecks(check); } finally { dialog.Close(); }
        }
    }
}
