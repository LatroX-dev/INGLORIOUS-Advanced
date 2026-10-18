using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

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
        _firmwareSequence = new();
        _returnedUsbInstanceId = null; _lightingRunning = false; _lightingStatus.Text = ""; _typingSequence = "";
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
            ApplyProgress(new(value, L.T(value == SetupStage.Reconnect ? "Verbinde das Gerät ohne gedrückte Tasten neu." : "Offline-Vorschau"), _session.DeviceId));
        }
        else Render();
    }

    internal void RunOfflineChecks(Action<bool, string> check)
    {
        if (!_offlinePreview) throw new InvalidOperationException("Setup checks are offline only.");
        bool HasInput(DependencyObject element) => element is TextBox || LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().Any(HasInput);
        check(!HasInput(_completion), "Setup: post-install and update tests contain no entry field");
        if (_operation == SetupOperation.Install)
        {
            var selected = new DeviceSetupWindow(offlinePreview: true, preferredInstanceId: "selected-stock");
            try
            {
                var stock = StockDeviceCatalog.Match(0x320F, 0x5046)!;
                var chosen = new SetupDevice("selected-stock", "GMMK PRO ISO", stock.CandidateDeviceIds, stock.VendorId, stock.ProductId, [], false);
                var other = chosen with { InstanceId = "other-stock" };
                selected.ApplyDevices([other, chosen]);
                check(selected.SelectedDevice == chosen && selected._devices.Visibility == Visibility.Collapsed && selected.ActionReady &&
                    selected._session is null && chosen.CandidateDeviceIds.Count == 2,
                    "Setup: sidebar selection uses the chosen physical keyboard without starting work or guessing its board revision");
                selected.ApplyDevices([other]);
                check(selected.SelectedDevice is null && !selected.ActionReady,
                    "Setup: unplugging a sidebar-selected keyboard never falls back to another device of the same model");
                selected.ApplyDevices([chosen with { IsRuntime = true }]);
                check(selected.SelectedDevice is null && !selected.ActionReady,
                    "Setup: an already installed sidebar device never enters first installation");
            }
            finally { selected.Close(); }
            var unavailable = new DeviceSetupWindow(offlinePreview: true);
            try
            {
                var stock = StockDeviceCatalog.Match(0x342D, 0xE3CE)!;
                var identified = new SetupDevice("unavailable-fixture", stock.IdentifiedName!, [],
                    stock.VendorId, stock.ProductId, [], false);
                unavailable.ApplyDevices([identified]);
                check(unavailable.SelectedDevice == identified && unavailable._deviceName.Text.Contains("GMMK 3 PRO 100% ISO", StringComparison.Ordinal) &&
                    unavailable._description.Text == L.T("Für dieses Modell ist noch keine INGLORIOUS-Firmware verfügbar."),
                    "Setup: a device without any candidate shows its exact name and unavailable firmware message.");
                check(!unavailable.ActionReady && !unavailable._action.IsEnabled && unavailable._service is null && !unavailable._timer.IsEnabled &&
                    unavailable._version.Visibility == Visibility.Collapsed && unavailable._summary.Visibility == Visibility.Collapsed &&
                    unavailable._step.Visibility == Visibility.Collapsed && unavailable._keepOpen.Visibility == Visibility.Collapsed,
                    "Setup: unavailable firmware has no executable action, offered version, backup promise or installation steps.");
                check(BootKeyLabels(identified).Length == 0 && BootInstruction(identified) == "" && unavailable._keyPanel.Visibility == Visibility.Collapsed,
                    "Setup: a device without candidates never inherits a boot gesture.");
                check(L.Catalog["Für dieses Modell ist noch keine INGLORIOUS-Firmware verfügbar."].Count == L.SupportedLanguages.Count - 1,
                    "Setup: unavailable firmware guidance is translated in every supported language.");
            }
            finally { unavailable.Close(); }
        }
        ShowOfflineStep("select");
        var device = SelectedDevice!;
        check(_service is null && !_timer.IsEnabled && !CanAct, "Setup: offline construction never creates a service or starts polling");
        check(_flowTitle.Text == OperationTitle && _stepProgress.Value == 1 && _step.Text == L.Format("Schritt {0} von 5", 1) && _keepOpen.Visibility == Visibility.Visible,
            "Setup: the operation header, first step and keep-open reminder are visible before preparation");
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
            check(ActionReady && _version.Text.Contains("0.0.1", StringComparison.Ordinal) && _version.Text.Contains(DeviceSetupService.TargetFirmwareVersionFor(device.CandidateDeviceIds[0]), StringComparison.Ordinal),
                "Update: the actual older report and target appear before preparing the update");
            _runtimeVersion = null; Render();
            check(!ActionReady, "Update: missing version response never offers an update");
            FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(device.CandidateDeviceIds[0]), out var target);
            _runtimeVersion = new(device.CandidateDeviceIds[0], target, 1); Render();
            check(!ActionReady, "Update: the installed target version is already current");
            _runtimeVersion = new(device.CandidateDeviceIds[0], new(ushort.MaxValue, 0, 0), 1); Render();
            check(!ActionReady, "Update: newer firmware is never offered a downgrade");
            ShowOfflineStep("backup"); _modelConfirmation.IsChecked = true;
            _session!.InstalledFirmwareVersion = DeviceSetupService.TargetFirmwareVersionFor(device.CandidateDeviceIds[0]);
            check(!ActionReady, "Update: firmware captured in boot mode must match the earlier actual runtime report");
            ShowOfflineStep("select"); _readingVersion = true;
            check(!ActionReady, "Update: no button activates while the selected device version is being read");
            _readingVersion = false;
        }
        foreach (var step in new[] { "disconnect", "connect", "backup", "confirm", "reconnect", "complete", "blocked" })
        {
            ShowOfflineStep(step);
            check(NeedsObservation == (step is "disconnect" or "connect" or "reconnect"),
                "Setup: only USB transition stages need periodic observation: " + step);
            check(!CanAct && !_action.IsEnabled && !_timer.IsEnabled, "Setup: offline " + step + " has no executable device action");
            check(!ActionReady, "Setup: " + step + " never authorizes capture or writing without an explicit confirmation");
            if (step is "disconnect" or "connect" or "reconnect")
                check(_action.Visibility == Visibility.Visible && !_action.IsEnabled, "Setup: automatic USB waiting steps show a disabled waiting action");
            check((_keyPanel.Visibility == Visibility.Visible) == (step == "connect"), "Setup: boot keys appear only during the bootloader connection step");
        }
        ShowOfflineStep("backup"); _modelConfirmation.IsChecked = true;
        check(ActionReady && !CanAct, "Setup: explicit model and firmware consent permits the combined action");
        check(_action.Content?.ToString() == CombinedAction &&
            ((TextBlock)_modelConfirmation.Content!).Text!.Contains(L.T("Das erkannte Modell stimmt mit meinem Gerät überein."), StringComparison.Ordinal),
            "Setup: every operation has one clear combined action and an explicit model confirmation");
        check(!NeedsObservation && !_observing && ActionReady,
            "Setup: stable confirmation cannot start a background scan that silently ignores the first click");
        for (var tick = 0; tick < 10; tick++)
        {
            Render();
            check(!NeedsObservation && ActionReady && _action.Content?.ToString() == CombinedAction,
                "Setup: repeated timer/render cycles keep the combined action stable");
        }
        _session!.DeviceId = "foreign-device"; Render();
        check(!ActionReady, "Setup: a model outside the selected physical device candidates cannot proceed");
        ShowOfflineStep("confirm");
        _modelConfirmation.IsChecked = true;
        check(!ActionReady && !CanAct && !NeedsObservation, "Setup: no operation exposes a second write action or starts a scan after backup");
        _busy = true; Render(); _activity.ApplyTemplate(); var closing = new CancelEventArgs(); WindowClosing(this, closing);
        check(_activity.Visibility == Visibility.Visible && _activity.Foreground == FindResource("Accent") &&
            _activity.Template.FindName("Pulse", _activity) is Border { Visibility: Visibility.Visible },
            "Setup: capture and write activity use the flat accent progress template");
        check(closing.Cancel && !ActionReady, "Setup: capture or write blocks closing and reentry without cancellation");
        _busy = false; closing = new(); WindowClosing(this, closing);
        check(!closing.Cancel, "Setup: a waiting wizard can be closed");
        ShowOfflineStep("backup"); _modelConfirmation.IsChecked = true;
        _observing = true; check(!ActionReady, "Setup: observation and button actions cannot overlap"); _observing = false;
        _error = true; check(!ActionReady, "Setup: a failed device check cannot authorize writing"); _error = false;
        _writeAttempted = true; check(!ActionReady, "Setup: an attempted firmware write is never silently retried");
        _writeAttempted = false; _outcomeUncertain = true;
        check(DeviceStateMayHaveChanged && !Completed, "Setup: a missing write result invalidates cached device state without claiming success");
        ShowOfflineStep("backup"); _modelConfirmation.IsChecked = true;
        ShowError(new SetupException("CORE fixture", recoverable: true));
        check(_session!.Stage == SetupStage.ReadyToBackup && !ActionReady, "Setup: a recoverable preflight error holds the current stage and blocks writing");
        ApplyProgress(new(SetupStage.ReadyToBackup, "Checked fixture", _session.DeviceId));
        check(ActionReady && !DeviceStateMayHaveChanged, "Setup: a fresh successful result releases a recoverable preflight error");
        var restart = L.T("Schließe diesen Dialog, verbinde das Gerät ohne gedrückte Tasten neu und starte den Vorgang erneut.");
        try
        {
            _firmwareSequence.RunAsync(_session!, _operation, _session!.DeviceId!, true, () => true,
                () => Task.FromException<SetupProgress>(new SetupException("CORE fixture", recoverable: true)), _ => { },
                () => throw new InvalidOperationException("No offline write delegate may run.")).GetAwaiter().GetResult();
        }
        catch (SetupException exception) { ShowError(exception); }
        check(_firmwareSequence.Started && !ActionReady && _status.Text.Contains(restart, StringComparison.Ordinal),
            "Setup: a stopped prewrite attempt explains how to start a fresh wizard instead of offering a consumed action");
        _writeAttempted = true;
        ShowError(new System.IO.IOException("Write fixture"));
        check(!_status.Text.Contains(restart, StringComparison.Ordinal), "Setup: an attempted write never receives prewrite restart guidance");
        _outcomeUncertain = true;
        ShowError(new System.IO.IOException("Uncertain fixture"));
        check(_status.Text == FirmwareMessages.ForExecutionFailure() && !_status.Text.Contains(restart, StringComparison.Ordinal),
            "Setup: uncertain firmware results never suggest reconnecting or retrying");
        ShowOfflineStep("connect");
        ApplyProgress(new(SetupStage.ConnectBootloader, L.T("Für den Update-Modus fehlt der USB-Treiber."), _session.DeviceId));
        check(_driverHelp.Visibility == Visibility.Visible, "Setup: driver instructions appear only with a detected missing driver");
        ApplyProgress(new(SetupStage.ConnectBootloader, "Waiting fixture", _session.DeviceId));
        check(_driverHelp.Visibility == Visibility.Collapsed, "Setup: driver instructions disappear after the condition changes");
        foreach (var model in DeviceCatalog.All.Where(model => !model.IsOfflineOnly))
        {
            ShowOfflineStep("reconnect", model.Id);
            check(_heading.Text == L.T("Jetzt normal neu verbinden") && _deviceName.Text == "\"" + model.Name + "\"" &&
                _description.Text == L.Format("Trenne \"{0}\" einmal vom USB-Kabel und stecke es ohne gedrückte Tasten wieder ein.", model.Name) &&
                _status.Text == L.T("Nach dem Wiederanschließen geht es automatisch weiter.") && _activity.Visibility == Visibility.Visible &&
                _activity.Margin == new Thickness(0, 0, 0, 18) && _status.Margin == new Thickness(0, 0, 0, 14),
                "Setup: " + model.Id + " reconnect matches the update instructions and visible automatic observation");
            var warning = L.T("Die USB-Verbindung konnte nicht geprüft werden. Die App versucht es erneut.");
            ApplyProgress(new(SetupStage.Reconnect, warning, model.Id));
            check(_status.Text == warning && !Completed, "Setup: reconnect guidance never hides a failed verification or invents completion");
            ShowOfflineStep("connect", model.Id);
            var instruction = BootInstruction(SelectedDevice);
            var stockEsc = _operation == SetupOperation.Install && model.Id.StartsWith("gmmk3-", StringComparison.Ordinal) && model.Id != "gmmk3-pro-100-iso";
            var expectedInstruction = model.Id == "numpad" ? L.T("Halte Plus und Enter beim Einstecken gedrückt. Lass die Tasten nach etwa 3 Sekunden los.")
                : stockEsc ? L.T("Halte Esc beim Einstecken gedrückt. Lass die Taste nach etwa 3 Sekunden los.")
                : L.T("Halte Leertaste und B beim Einstecken gedrückt. Lass die Tasten nach etwa 3 Sekunden los.");
            check(instruction == expectedInstruction, "Setup: " + model.Id + " " + _operation + " uses its reviewed firmware-specific boot gesture");
            var keyLabels = _keyCaps.Children.OfType<Border>().Select(cap => ((TextBlock)cap.Child).Text).ToArray();
            var expectedKeys = model.Id == "numpad" ? new[] { "+", L.T("Plus (+) + Enter").Split(" + ")[1] }
                : stockEsc ? ["Esc"] : L.T("Leertaste + B").Split(" + ");
            check(keyLabels.SequenceEqual(expectedKeys) && _stepProgress.Value == 3, "Setup: " + model.Id + " shows the real boot keys as individual keycaps");
            var firstCap = _keyCaps.Children[0]; Render();
            check(ReferenceEquals(firstCap, _keyCaps.Children[0]), "Setup: USB observation does not replace or flicker unchanged keycaps");
            ShowOfflineStep("complete", model.Id);
            check(CompletionTestsVisible == (_operation != SetupOperation.Restore) && !_lightingButton.IsEnabled,
                "Setup: " + model.Id + " " + _operation + " shows its post-flash test without allowing offline device access");
            check(_deviceTestButton.Visibility == Visibility.Visible && !_deviceTestButton.IsEnabled &&
                (_numLockHint.Visibility == Visibility.Visible) == (model.Id == "numpad"),
                "Setup: all models offer device testing after install/update, with the Num Lock hint only for Numpad");
            RecordCompletionText(model.Id == "numpad" ? "123456789" : "INGLORIOU");
            check(!TypingCompletes, "Setup: incomplete typing never finishes the wizard");
            var partial = _typingSequence;
            _busy = true; RecordCompletionText(ExpectedTypingSequence);
            check(_typingSequence == partial && !TypingCompletes, "Setup: busy operations never capture typed completion");
            _busy = false; OnDeactivated(EventArgs.Empty);
            check(_typingSequence == "", "Setup: leaving the guide clears incomplete test input before another window or modal");
            RecordCompletionText(model.Id == "numpad" ? "1234567890" : "iNgLoRiOuS");
            check(TypingCompletes == (_operation != SetupOperation.Restore), "Setup: exact digits or case-independent name finishes only the INGLORIOUS test");
            _lightingRunning = true; Render(); var lightClosing = new CancelEventArgs(); WindowClosing(this, lightClosing);
            RecordCompletionText(ExpectedTypingSequence);
            check(lightClosing.Cancel && !_close.IsEnabled && !TypingCompletes && _typingSequence == "",
                "Setup: lighting observation and cleanup finish before closing or accepting typed completion");
            _lightingRunning = false;

            var board = BoardDescriptorRegistry.Get(model.Id);
            FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(model.Id), out var target);
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
        foreach (var key in new[] { "Plus (+) + Enter", "Leertaste + B" })
            check(L.Catalog[key].Count == L.SupportedLanguages.Count - 1 && L.Catalog[key].Values.All(value => value.Split(" + ").Length == 2),
                "Setup: translated boot combinations remain two separate keycaps in every supported language");
        RunCompactLayoutChecks(check);
        ShowOfflineStep("select");
    }

    private void RunCompactLayoutChecks(Action<bool, string> check)
    {
        var language = L.CurrentLanguage;
        try
        {
            foreach (var supported in L.SupportedLanguages)
            {
                L.SetLanguage(supported.Code);
                foreach (var id in new[] { "numpad", "gmmk3-pro-100-iso", "gmmk3-75-iso" })
                {
                    var window = new DeviceSetupWindow(id, _operation, offlinePreview: true);
                    try
                    {
                        var content = (FrameworkElement)window.Content;
                        foreach (var step in new[] { "select", "connect", "backup", "reconnect", "complete", "blocked" })
                        {
                            window.ShowOfflineStep(step, id);
                            if (step == "complete")
                            {
                                window._coreImportPreview = new(id, 0, 100, new() { [DeviceCatalog.Get(id).Keys[0].Id] = "#FF0000" }, new(), 0, 0);
                                window._lightingStatus.Text = L.T("Prüfe, ob die Tasten und Seitenleisten aufgeleuchtet haben."); window.Render();
                            }
                            // Client size leaves space for native title bar at the window's minimum size.
                            foreach (var size in new[] { new Size(744, 580), new Size(1000, 520) })
                            {
                            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                            var body = window._flowBody;
                            var available = ((Grid)body.Parent).RowDefinitions[1].ActualHeight;
                            var flowBottom = window._flowContent.TranslatePoint(new Point(0, window._flowContent.ActualHeight), body).Y;
                            check(flowBottom <= available + 1, $"Setup: {_operation}/{step} instructions fit without scrolling: {supported.Code}/{id} ({flowBottom:0}/{available:0})");
                            if (window.CompletionTestsVisible)
                            {
                                var bottom = window._completion.TranslatePoint(new Point(0, window._completion.ActualHeight), body).Y;
                                check(bottom <= available + 1, $"Setup: test cards and completion actions fit without scrolling: {supported.Code}/{id} ({bottom:0}/{available:0})");
                                check(window._statusScroll.Visibility == Visibility.Collapsed && window._typingHint.Visibility == Visibility.Visible && window._deviceTestButton.Visibility == Visibility.Visible,
                                    "Setup: visual and quick typing tests remain available without duplicate completion status");
                                check(window._feedbackHint.Text == DeviceFeedbackContext.Invitation(DeviceCatalog.Get(id)), "Setup: voluntary feedback invitation matches model support status");
                                foreach (var control in new FrameworkElement[] { window._lightingButton, window._lightingStatus, window._typingHint, window._numLockHint, window._deviceTestButton, window._coreImportButton, window._feedbackHint, window._feedbackButton })
                                {
                                    if (control.Visibility != Visibility.Visible) continue;
                                    var controlBottom = control.TranslatePoint(new Point(0, control.ActualHeight), body).Y;
                                    check(controlBottom <= available + 1, $"Setup: completion control {control.Name} is reachable without scrolling: {supported.Code}/{id}/{size} ({controlBottom:0}/{available:0})");
                                }
                            }
                            }
                        }
                    }
                    finally { window.Close(); }
                }
            }
        }
        finally { L.SetLanguage(language); }
    }
}
