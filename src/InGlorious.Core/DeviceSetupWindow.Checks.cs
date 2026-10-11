using System.ComponentModel;
using System.Windows;

namespace InGlorious.Core;

public sealed partial class DeviceSetupWindow
{
    internal void ShowOfflineStep(string step, string? deviceId = null)
    {
        if (!_offlinePreview || _service is not null) throw new InvalidOperationException("Offline setup fixture required.");
        var id = deviceId ?? _preferredDeviceId ?? "gmmk3-75-iso";
        var board = BoardDescriptorRegistry.Get(id);
        var stock = StockDeviceCatalog.All.First(match => match.CandidateDeviceIds.Contains(id));
        var runtime = _operation != SetupOperation.Install;
        var device = new SetupDevice("offline-fixture", DeviceCatalog.Get(id).Name, runtime ? [id] : stock.CandidateDeviceIds,
            runtime ? BoardDescriptorRegistry.RuntimeVendorId : stock.VendorId, runtime ? board.RuntimeProductId : stock.ProductId, [], runtime);
        _session = null; _progress = null; _writeAttempted = _outcomeUncertain = false; _busy = _observing = _readingVersion = _error = false;
        _returnedUsbInstanceId = null; _lightingRunning = false; _lightingStatus.Text = ""; _typingTest.Clear();
        ApplyDevices([device]);
        _runtimeVersion = runtime ? new(id, new(0, 0, 1), 1) : null; // Synthetic older report; no hardware identity or data.
        var stage = step.ToLowerInvariant() switch
        {
            "disconnect" => SetupStage.Disconnect, "connect" => SetupStage.ConnectBootloader,
            "backup" => SetupStage.ReadyToBackup, "confirm" => SetupStage.ReadyToInstall,
            "reconnect" => SetupStage.Reconnect, "complete" => SetupStage.Complete,
            "blocked" => SetupStage.Blocked, _ => (SetupStage?)null
        };
        if (stage is { } value)
        {
            _session = new(device, _operation, "offline-fixture")
            {
                Stage = value, DeviceId = value is SetupStage.Disconnect or SetupStage.ConnectBootloader ? null : id,
                InstalledFirmwareVersion = runtime ? "0.0.1" : null
            };
            _writeAttempted = value is SetupStage.Reconnect or SetupStage.Complete;
            if (value == SetupStage.Complete && _operation != SetupOperation.Restore)
                _returnedUsbInstanceId = $@"USB\VID_FEED&PID_{board.RuntimeProductId:X4}\OFFLINE";
            ApplyProgress(new(value, L.T("Offline-Vorschau"), _session.DeviceId));
        }
        else Render();
    }

    internal void RunOfflineChecks(Action<bool, string> check)
    {
        if (!_offlinePreview) throw new InvalidOperationException("Setup checks are offline only.");
        ShowOfflineStep("select");
        var device = SelectedDevice!;
        check(_service is null && !_timer.IsEnabled && !CanAct, "Setup: offline construction never creates a service or starts polling");
        check(!_description.Text.Contains("Glorious CORE", StringComparison.Ordinal) && _driverHelp.Visibility == Visibility.Collapsed, "Setup: ordinary discovery does not invent a CORE or driver problem");
        check(_devices.Visibility == Visibility.Collapsed && ReferenceEquals(_devices.SelectedItem, device), "Setup: exactly one eligible device is selected without a dropdown");
        ApplyDevices([device, device with { InstanceId = "second-fixture" }]);
        check(_devices.Visibility == Visibility.Visible && _devices.SelectedIndex == -1 && !ActionReady, "Setup: multiple physical devices require explicit selection even with a preferred model");
        ApplyDevices([device with { IsRuntime = !device.IsRuntime }]);
        check(_devices.Items.Count == 0 && !ActionReady, "Setup: install never mixes stock with update/restore runtime devices");
        ApplyDevices([device with { CandidateDeviceIds = ["numpad"] }]);
        check(_devices.Items.Count == 0 && !ActionReady, "Setup: an explicit preferred model never falls back to another keyboard");
        if (_operation == SetupOperation.Update)
        {
            ShowOfflineStep("select");
            check(ActionReady && _version.Text.Contains("0.0.1", StringComparison.Ordinal) && _version.Text.Contains(DeviceSetupService.TargetFirmwareVersion, StringComparison.Ordinal),
                "Update: the actual older report and target appear before preparing the update");
            _runtimeVersion = null; Render();
            check(!ActionReady, "Update: missing version response never offers an update");
            FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersion, out var target);
            _runtimeVersion = new(device.CandidateDeviceIds[0], target, 1); Render();
            check(!ActionReady, "Update: the installed target version is already current");
            _runtimeVersion = new(device.CandidateDeviceIds[0], new(ushort.MaxValue, 0, 0), 1); Render();
            check(!ActionReady, "Update: newer firmware is never offered a downgrade");
            ShowOfflineStep("confirm"); _writeConfirmation.IsChecked = true;
            _session!.InstalledFirmwareVersion = DeviceSetupService.TargetFirmwareVersion;
            check(!ActionReady, "Update: firmware captured in boot mode must match the earlier actual runtime report");
            ShowOfflineStep("select"); _readingVersion = true;
            check(!ActionReady, "Update: no button activates while the selected device version is being read");
            _readingVersion = false;
        }
        foreach (var step in new[] { "disconnect", "connect", "backup", "confirm", "reconnect", "complete", "blocked" })
        {
            ShowOfflineStep(step);
            check(!CanAct && !_action.IsEnabled && !_timer.IsEnabled, "Setup: offline " + step + " has no executable device action");
            check(!ActionReady, "Setup: " + step + " never authorizes capture or writing without an explicit confirmation");
        }
        ShowOfflineStep("backup"); _modelConfirmation.IsChecked = true;
        check(ActionReady && !CanAct, "Setup: model confirmation permits only the separately requested capture action");
        _session!.DeviceId = "foreign-device"; Render();
        check(!ActionReady, "Setup: a model outside the selected physical device candidates cannot proceed");
        ShowOfflineStep("confirm");
        _writeConfirmation.IsChecked = true;
        check(ActionReady && !CanAct, "Setup: a verified backup still requires the final write confirmation");
        _busy = true; var closing = new CancelEventArgs(); WindowClosing(this, closing);
        check(closing.Cancel && !ActionReady, "Setup: capture or write blocks closing and reentry without cancellation");
        _busy = false; closing = new(); WindowClosing(this, closing);
        check(!closing.Cancel, "Setup: a waiting wizard can be closed");
        _observing = true; check(!ActionReady, "Setup: observation and button actions cannot overlap"); _observing = false;
        _error = true; check(!ActionReady, "Setup: a failed device check cannot authorize writing"); _error = false;
        _writeAttempted = true; check(!ActionReady, "Setup: an attempted firmware write is never silently retried");
        _writeAttempted = false; _outcomeUncertain = true;
        check(DeviceStateMayHaveChanged && !Completed, "Setup: a missing write result invalidates cached device state without claiming success");
        ShowOfflineStep("backup"); _modelConfirmation.IsChecked = true;
        ShowError(new SetupException("CORE fixture", recoverable: true));
        check(_session!.Stage == SetupStage.ReadyToBackup && !ActionReady, "Setup: a recoverable preflight error holds the current stage and blocks writing");
        ApplyProgress(new(SetupStage.ReadyToBackup, "Checked fixture", _session.DeviceId));
        check(ActionReady && !DeviceStateMayHaveChanged, "Setup: a fresh successful observation releases a recoverable preflight error");
        ApplyProgress(new(SetupStage.ConnectBootloader, L.T("Für den Update-Modus fehlt der USB-Treiber."), _session.DeviceId));
        check(_driverHelp.Visibility == Visibility.Visible, "Setup: driver instructions appear only with a detected missing driver");
        ApplyProgress(new(SetupStage.ConnectBootloader, "Waiting fixture", _session.DeviceId));
        check(_driverHelp.Visibility == Visibility.Collapsed, "Setup: driver instructions disappear after the condition changes");
        foreach (var model in DeviceCatalog.All)
        {
            ShowOfflineStep("connect", model.Id);
            var instruction = BootInstruction(SelectedDevice);
            check(model.Id == "numpad" ? instruction.Contains("Plus", StringComparison.Ordinal) : _operation == SetupOperation.Install && model.Id.StartsWith("gmmk3-", StringComparison.Ordinal) ? instruction.Contains("Esc", StringComparison.Ordinal) : instruction.Contains("Leertaste", StringComparison.Ordinal), "Setup: " + model.Id + " " + _operation + " uses its reviewed firmware-specific boot gesture");
            ShowOfflineStep("complete", model.Id);
            check(CompletionTestsVisible == (_operation != SetupOperation.Restore) && !_lightingButton.IsEnabled,
                "Setup: " + model.Id + " " + _operation + " shows its post-flash test without allowing offline device access");
            _typingTest.Text = model.Id == "numpad" ? "123456789" : "INGLORIOU";
            check(!TypingCompletes, "Setup: incomplete typing never finishes the wizard");
            _typingTest.Text = model.Id == "numpad" ? "1234567890" : "iNgLoRiOuS";
            check(TypingCompletes == (_operation != SetupOperation.Restore), "Setup: exact digits or case-independent name finishes only the INGLORIOUS test");
            _lightingRunning = true; Render(); var lightClosing = new CancelEventArgs(); WindowClosing(this, lightClosing);
            check(lightClosing.Cancel && !_close.IsEnabled && !TypingCompletes && !_typingTest.IsEnabled,
                "Setup: lighting observation and cleanup finish before closing or accepting typed completion");
            _lightingRunning = false;

            var board = BoardDescriptorRegistry.Get(model.Id);
            FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersion, out var target);
            var report = new OnboardFirmwareVersion(model.Id, target, 1);
            var info = new OnboardInfo(model.Id, (byte)(249 | (board.HasSlider ? 2 : 0)), 0, 1, 0, 0);
            var extensions = new OnboardExtensions(1, 3, 21000);
            check(IsExpectedReturnedRuntime(model.Id, info, extensions, report), "Setup: bundled target version and complete capabilities confirm " + model.Id);
            check(!IsExpectedReturnedRuntime(model.Id, info, extensions, null) && !IsExpectedReturnedRuntime(model.Id, info, extensions, report with { Version = new(0, 0, 1) }) &&
                !IsExpectedReturnedRuntime(model.Id, info, extensions, report with { BuildId = 0 }), "Setup: absent, wrong or invalid version cannot claim firmware completion");
            check(!IsExpectedReturnedRuntime(model.Id, info with { StagingBytes = 1 }, extensions, report) &&
                !IsExpectedReturnedRuntime(model.Id, info with { Flags = 0 }, extensions, report) &&
                !IsExpectedReturnedRuntime(model.Id, info, extensions with { Flags = 0 }, report), "Setup: busy, nonpersistent or missing lighting capability cannot claim completion");
            var original = _session!.Device with { LocationPaths = ["PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)"] };
            var returned = original with { IsRuntime = true, CandidateDeviceIds = [model.Id], VendorId = 0xFEED, ProductId = board.RuntimeProductId, InstanceId = "returned-fixture" };
            check(SelectReturnedDevice([returned], original, model.Id) == returned, "Setup: returned runtime is resolved at the original physical port");
            check(SelectReturnedDevice([returned with { LocationPaths = ["PCIROOT(0)#PCI(1500)#USBROOT(0)#USB(2)"] }], original, model.Id) is null &&
                SelectReturnedDevice([returned, returned with { InstanceId = "duplicate-fixture" }], original, model.Id) is null &&
                SelectReturnedDevice([returned with { IsRuntime = false }], original, model.Id) is null,
                "Setup: another controller, duplicate runtime or stock descriptor cannot receive the post-flash test");
        }
        ShowOfflineStep("select");
    }
}
