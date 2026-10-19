using System.Windows;

namespace InGlorious.Core;

public sealed partial class TransferWindow
{
    // Pure result-state checks, invoked only by the main window's offline UI smoke.
    // No transport, discovery, file backup, dialog, or simulated HID device is involved.
    internal static void RunOfflineStatusChecks(string deviceId, Action<bool, string> check)
    {
        var project = ProjectService.Create(deviceId);
        var preview = new TransferWindow(project, offlinePreview: true);
        try
        {
            var image = preview._image;
            var info = new OnboardInfo(deviceId, 25, image.UsedBytes, 12, image.Crc32, 0, image.SchemaVersion);
            var result = new OnboardTransferResult(13, image.Crc32, image.UsedBytes, true);
            check(preview._offlinePreview && !preview._findButton.IsEnabled && !preview._readButton.IsEnabled && !preview._transferButton.IsEnabled && !preview._targetChoice.IsEnabled,
                deviceId + ": offline transfer preview disables every hardware action");
            preview.FindDevices_Click(preview, new RoutedEventArgs());
            preview.ReadDevice_Click(preview, new RoutedEventArgs());
            preview.Transfer_Click(preview, new RoutedEventArgs());
            check(!preview._busy && preview._currentInfo is null && !preview.DeviceSaveVerified && !preview.DeviceStateUncertain,
                deviceId + ": offline handler guards return before discovery or device access");

            preview.AcceptSavedResult(result, info);
            check(preview.DeviceSaveVerified && !preview.DeviceStateUncertain && preview._readProject is not null && preview.ImportedGeneration == 13,
                deviceId + ": verified save makes exactly its current result importable");
            preview.ClearConfirmation(invalidated: true);
            check(!preview.DeviceSaveVerified && preview.DeviceStateUncertain && preview._readProject is null,
                deviceId + ": beginning a new read or write clears prior success and import data");
            preview.ClearConfirmation(invalidated: true); // The read-error path.
            check(!preview.DeviceSaveVerified && preview.DeviceStateUncertain && preview._readProject is null,
                deviceId + ": failed follow-up read cannot retain earlier success");

            preview.AcceptReadResult(image.Payload, info);
            check(preview.DeviceSaveVerified && !preview.DeviceStateUncertain && !preview._outcomeUncertain,
                deviceId + ": exact complete read confirms current configuration without requiring a write");
            var changed = ProjectService.Clone(project);
            var key = DeviceCatalog.Get(deviceId).Keys.First(k => k.LedIndex.HasValue);
            changed.Layers[0].Keys[key.Id].Color = "#18394A";
            var changedImage = OnboardSerializer.Prepare(changed);
            preview.AcceptReadResult(changedImage.Payload, info with { ActiveCrc = changedImage.Crc32 });
            check(!preview.DeviceSaveVerified && preview.DeviceStateUncertain && !preview._outcomeUncertain && preview._readProject!.Layers[0].Keys[key.Id].Color == "#18394A",
                deviceId + ": differing complete read invalidates cached confirmation but remains importable");

            preview.AcceptSavedResult(result, info);
            preview.ClearConfirmation(invalidated: true);
            preview.RejectTransferResult(applyStarted: true);
            check(!preview.DeviceSaveVerified && preview.DeviceStateUncertain && preview._outcomeUncertain && preview._readProject is null,
                deviceId + ": failed follow-up write removes stale import and blocks another write");
            preview.ClearConfirmation(); // Discovery or target selection cannot resolve an uncertain write.
            check(preview._outcomeUncertain && preview.DeviceStateUncertain,
                deviceId + ": discovery and target changes cannot clear an uncertain write outcome");
            preview.AcceptReadResult(changedImage.Payload, info with { ActiveCrc = changedImage.Crc32 });
            check(!preview._outcomeUncertain && preview.DeviceStateUncertain && !preview.DeviceSaveVerified,
                deviceId + ": a complete differing read resolves write uncertainty without confirming the draft");
            preview.AcceptReadResult(image.Payload, info);
            check(!preview.DeviceStateUncertain && preview.DeviceSaveVerified,
                deviceId + ": exact read resolves uncertainty and replaces previous failure state");

            preview.RejectTransferResult(applyStarted: false);
            check(!preview.DeviceSaveVerified && preview.DeviceStateUncertain && !preview._outcomeUncertain && preview._readProject is null,
                deviceId + ": failure before upload invalidates verification without claiming an uncertain write");
            preview.AcceptSavedResult(result, info);
            preview.UpdateTarget();
            check(!preview.DeviceSaveVerified && preview._readProject is null,
                deviceId + ": changing target clears the previous target's confirmed and importable result");
        }
        finally { preview.Close(); }
        var screenshot = new TransferWindow(project, screenshotPath: "offline-status-check.png");
        check(screenshot._offlinePreview && !screenshot._findButton.IsEnabled,
            deviceId + ": screenshot path also forces offline mode without an explicit flag");
        screenshot.Close(); // Not shown: no Loaded event and no screenshot file is written.
        project.Standby.Enabled = true;
        var standby = new TransferWindow(project, offlinePreview: true);
        try
        {
            standby._currentInfo = new(deviceId, 121, standby._image.UsedBytes, 0, 0, 0, 1);
            standby.UpdateControls();
            check(standby.UnsupportedFeature(standby._currentInfo) is not null && !standby._transferButton.IsEnabled && standby._firmwareGuideButton.Visibility == Visibility.Visible,
                deviceId + ": unsupported standby has a visible firmware guide instead of an unexplained disabled transfer");
            check(!standby._firmwareGuideButton.IsEnabled, deviceId + ": offline preview cannot open a live firmware guide");
            standby._currentInfo = standby._currentInfo with { Flags = 249 };
            standby.UpdateControls();
            check(standby.UnsupportedFeature(standby._currentInfo) is null && standby._firmwareGuideButton.Visibility == Visibility.Collapsed,
                deviceId + ": compatible standby firmware hides the update action");
            standby._currentInfo = null; standby._deviceSearchCompleted = true; standby.UpdateControls();
            check(standby._firmwareGuideButton.Visibility == Visibility.Visible && Equals(standby._firmwareGuideButton.Content, L.T("Gerätestatus prüfen")), deviceId + ": unconfirmed firmware offers status detection without assuming first-time setup");
        }
        finally { standby.Close(); }
        project.Standby.Mode = 2;
        var screensaver = new TransferWindow(project, offlinePreview: true);
        try
        {
            screensaver._currentInfo = new(deviceId, 249, screensaver._image.UsedBytes, 0, 0, 0, 4);
            screensaver._currentExtensions = new(1, 1, 4250);
            screensaver.UpdateControls();
            check(screensaver.UnsupportedFeature(screensaver._currentInfo) == L.T("Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen.")
                && screensaver._firmwareGuideButton.Visibility == Visibility.Visible,
                deviceId + ": older standby firmware gives a visible screensaver update route before transfer");
            screensaver._currentExtensions = new(1, 3, 21000);
            screensaver.UpdateControls();
            check(screensaver.UnsupportedFeature(screensaver._currentInfo) is null && screensaver._firmwareGuideButton.Visibility == Visibility.Collapsed,
                deviceId + ": explicitly advertised screensaver capability removes the update block");
            screensaver.UpdateTarget();
            check(screensaver._currentExtensions is null,
                deviceId + ": selecting another target clears the previous screensaver capability");
        }
        finally { screensaver.Close(); }
        RunCompactTransferChecks(deviceId, check);
        if (deviceId == "pro-iso") RunHardwareProfileTargetChecks(check);
    }

    private static void RunHardwareProfileTargetChecks(Action<bool, string> check)
    {
        const string id = "gmmk3-pro-100-iso";
        var project = ProjectService.Create(id); var image = OnboardSerializer.Prepare(project);
        var binding = new OnboardProfileBinding(id, Guid.Parse("ab667fea-036b-4258-85b5-cd10c2d42f68"), 2, 5, image.Crc32);
        var info = new OnboardInfo(id, 249, image.UsedBytes, 5, image.Crc32, 0, image.SchemaVersion);
        var profile = new OnboardHardwareProfileStatus(id, 2, 2, 1, 2, 5, image.Crc32, image.UsedBytes, image.SchemaVersion);
        var target = new VerifiedTarget(new(id, "Fixture", "offline-only"), info, "Fixture", new(1, 511, 21000), binding, profile);
        var window = new TransferWindow(project, profileBinding: binding); // Never shown: no hardware/discovery.
        try
        {
            window.SetDiscoveredTargets([target]);
            check(window._transferButton.IsEnabled, "Hardware-profile transfer: matching installation, slot and generation are required");
            foreach (var invalid in new[]
            {
                target with { ProfileBinding = binding with { Generation = 6 } },
                target with { ProfileBinding = binding with { InstallationId = Guid.Empty } },
                target with { ProfileStatus = profile with { ActiveSlot = 3 } },
                target with { ProfileStatus = profile with { Flags = 5 } },
                target with { ProfileStatus = profile with { Flags = 9 } },
                target with { ProfileStatus = profile with { Flags = 0 } }
            })
            {
                window.SetDiscoveredTargets([invalid]);
                check(!window._transferButton.IsEnabled, "Hardware-profile transfer: changed, busy or unhealthy discovered target stays blocked");
            }
            window.SetDiscoveredTargets([target], failed: true);
            check(window.SelectedTarget is null && !window._transferButton.IsEnabled && !window._readButton.IsEnabled,
                "Hardware-profile transfer: incomplete discovery cannot expose the remaining keyboard as a target");
        }
        finally { window.Close(); }
    }

    private static void RunCompactTransferChecks(string deviceId, Action<bool, string> check)
    {
        // Normal-mode instances are never shown: Loaded/discovery cannot run. Only pure state methods are exercised.
        var project = ProjectService.Create(deviceId);
        var window = new TransferWindow(project);
        try
        {
            var image = window._image;
            var info = new OnboardInfo(deviceId, 249, image.UsedBytes, 12, image.Crc32, 0, image.SchemaVersion);
            var result = new OnboardTransferResult(13, image.Crc32, image.UsedBytes, true);
            var first = new VerifiedTarget(new(deviceId, "Fixture A", "offline-a"), info, "Fixture A");
            var second = new VerifiedTarget(new(deviceId, "Fixture B", "offline-b"), info, "Fixture B");
            var otherId = deviceId == "pro-iso" ? "numpad" : "pro-iso";
            var foreign = new VerifiedTarget(new(otherId, "Foreign", "offline-foreign"), info with { DeviceId = otherId }, "Foreign");

            window.SetDiscoveredTargets([]);
            check(window.SelectedTarget is null && window._targetChoice.Visibility == Visibility.Collapsed && !window._transferButton.IsEnabled && window._findButton.Visibility == Visibility.Visible,
                deviceId + ": no target offers search without an empty selection control");
            window.SetDiscoveredTargets([foreign]);
            check(window._targetChoice.Items.Count == 0 && !window._transferButton.IsEnabled,
                deviceId + ": foreign board cannot become a transfer target");
            window.SetDiscoveredTargets([first]);
            check(ReferenceEquals(window.SelectedTarget, first) && window._targetChoice.Visibility == Visibility.Collapsed && window._findButton.Visibility == Visibility.Collapsed && window._transferButton.IsEnabled,
                deviceId + ": single matching target is selected without extra choices or search");
            check(window._readButton.Visibility == Visibility.Collapsed && window._importButton.Visibility == Visibility.Collapsed && window.Height == 400 && window.MinHeight == 360,
                deviceId + ": normal compact transfer does not expose recovery actions");
            window.SetDiscoveredTargets([first, second]);
            check(window.SelectedTarget is null && window._targetChoice.Visibility == Visibility.Visible && !window._transferButton.IsEnabled && window._findButton.Visibility == Visibility.Visible,
                deviceId + ": several matching targets require an explicit choice");
            window._targetChoice.SelectedIndex = 1;
            check(ReferenceEquals(window.SelectedTarget, second) && window._transferButton.IsEnabled,
                deviceId + ": explicit target selection enables the checked target");
            window.SetDiscoveredTargets([first], failed: true);
            check(window._findButton.Visibility == Visibility.Visible && !window._transferButton.IsEnabled && window.SelectedTarget is null,
                deviceId + ": partial discovery failure cannot silently select the surviving target");
            window.SetDiscoveredTargets([first with { Info = info with { Flags = 248 } }]);
            check(!window._transferButton.IsEnabled && window._findButton.Visibility == Visibility.Visible,
                deviceId + ": unavailable persistent storage blocks transfer and allows another search");
            window.SetDiscoveredTargets([first]);

            window.AcceptReadResult(image.Payload, info);
            check(window.DeviceSaveVerified && !window.CanAutoClose && !window._closeAfterOperation,
                deviceId + ": ordinary exact read alone never requests automatic closing");
            window.AcceptReadResult(image.Payload, info with { Flags = 248 });
            check(!window.DeviceSaveVerified && !window.CanAutoClose,
                deviceId + ": exact RAM-only data is never considered a confirmed save");
            window.AcceptReadResult(image.Payload, info with { Flags = 253 });
            check(!window.DeviceSaveVerified && !window.CanAutoClose,
                deviceId + ": an active staging session is not a completed recovery");

            window.RejectTransferResult(applyStarted: true);
            window.UpdateControls();
            check(window._readButton.Visibility == Visibility.Visible && window._readButton.IsEnabled && !window._transferButton.IsEnabled && !window._targetChoice.IsEnabled && !window._findButton.IsEnabled,
                deviceId + ": uncertain save permits only recovery reading of the same target");
            window._reconnectRequired = true; // A previous 8 ms setting being reset to the default 1 ms.
            check(!image.RequiresTiming, deviceId + ": default polling-rate payload does not itself request timing support");
            window.AcceptReadResult(image.Payload, info);
            check(window._reconnectRequired && window._recoveryMode && !window.CanAutoClose,
                deviceId + ": recovery retains the previous polling change and waits for reconnect completion");
            window.SetReconnectAcknowledged(false);
            window.CompleteVerifiedOperation();
            check(window.DeviceSaveVerified && window.CanAutoClose && window.CompletionNotice == L.T("Bitte verbinde das USB-Kabel erneut, damit die neue Abfragerate aktiv wird."),
                deviceId + ": exact recovery can finish with a cable notice after a missing reconnect acknowledgement");
            window.SetReconnectAcknowledged(true);
            check(window.CompletionNotice is null && window.DeviceSaveVerified && window.CanAutoClose,
                deviceId + ": acknowledged reconnect leaves no completion warning");
            window.RejectTransferResult(applyStarted: true);
            check(!window.CanAutoClose && window.CompletionNotice is null,
                deviceId + ": a failure after a previous confirmation cancels closing and stale notices");

            var changed = ProjectService.Clone(project);
            var key = DeviceCatalog.Get(deviceId).Keys.First(k => k.LedIndex.HasValue);
            changed.Layers[0].Keys[key.Id].Color = "#193A5B";
            var changedImage = OnboardSerializer.Prepare(changed);
            window.AcceptReadResult(changedImage.Payload, info with { ActiveCrc = changedImage.Crc32 });
            window.UpdateControls();
            check(!window.CanAutoClose && !window._outcomeUncertain && window._importButton.Visibility == Visibility.Visible && window._importButton.IsEnabled && window._transferButton.IsEnabled,
                deviceId + ": a differing recovered profile remains open and can be imported or replaced");

            window.ClearConfirmation();
            var rejected = false;
            try { window.AcceptSavedResult(result with { ReadbackVerified = false }, info); }
            catch (System.IO.IOException) { rejected = true; }
            check(rejected && !window.DeviceSaveVerified && !window.CanAutoClose,
                deviceId + ": a result without verified readback cannot complete");
            rejected = false;
            try { window.AcceptSavedResult(result with { Crc32 = image.Crc32 ^ 1 }, info); }
            catch (System.IO.IOException) { rejected = true; }
            check(rejected && !window.DeviceSaveVerified, deviceId + ": a different returned checksum cannot complete");

            window.SetBusy(true);
            window._progressActive = true; window._progressRevision = 7;
            window.ReportProgress(70, 7);
            check(window._transferStatus.Text == L.T("Dauerhaft speichern … dieser Schritt kann bis zu zwei Minuten dauern. Bitte angeschlossen lassen.") && window._progress.Value == 70,
                deviceId + ": commit progress explains the durable storage wait");
            var text = window._transferStatus.Text;
            window.ReportProgress(10, 6);
            check(window._transferStatus.Text == text && window._progress.Value == 70,
                deviceId + ": a previous transfer callback cannot replace current progress");
            window._progressActive = false;
            window.AcceptSavedResult(result, info);
            window.CompleteVerifiedOperation();
            text = window._transferStatus.Text;
            window.ReportProgress(80, 7);
            check(window._transferStatus.Text == text && window._progress.Value == 100,
                deviceId + ": queued progress cannot overwrite the completed state");
            check(!window.CanAutoClose && !window._closeButton.IsEnabled,
                deviceId + ": confirmed transfer still cannot close while device access is active");
            window.FinishOperation();
            check(window._closed && !window._busy && window.DeviceSaveVerified && !window.DeviceStateUncertain,
                deviceId + ": verified transfer closes only after the operation releases its busy state");
            window.ReportProgress(1, 7);
            check(window._transferStatus.Text == text && window._progress.Value == 100,
                deviceId + ": late callbacks after closing leave the final status unchanged");
        }
        finally { if (!window._closed) { window.SetBusy(false); window.Close(); } }

        var offline = new TransferWindow(project, offlinePreview: true);
        try
        {
            offline.ShowOfflineTargetPreview();
            check(offline.SelectedTarget is not null && offline._targetChoice.Visibility == Visibility.Collapsed && !offline._transferButton.IsEnabled && !offline._readButton.IsEnabled && !offline._findButton.IsEnabled,
                deviceId + ": realistic one-target offline preview keeps all device actions disabled");
            offline.AcceptSavedResult(new(2, offline._image.Crc32, offline._image.UsedBytes, true), offline._currentInfo!);
            offline.CompleteVerifiedOperation();
            offline.FinishOperation();
            check(!offline.CanAutoClose && !offline._closed,
                deviceId + ": offline fixtures never auto-close from a synthetic success");
        }
        finally { offline.Close(); }
    }
}
