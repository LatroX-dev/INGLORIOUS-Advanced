using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InGlorious.Core;

public partial class MainWindow
{
    // Offline smoke only: metadata is injected; no PnP, HID, firmware or profile-file operation.
    private async Task RunConnectionChecksAsync(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Connection checks require offline UI-smoke mode.");
        var devicesBefore = _devices;
        var setupDevicesBefore = _setupDevices;
        var preferencesBefore = _preferences;
        var confirmationsBefore = _confirmedDeviceSaves.ToArray();
        var requiredBefore = _deviceReadRequired.ToArray();
        var scanFailedBefore = _connectionScanFailed; var pendingBefore = _connectionRefreshPending;
        var revisionBefore = _connectionRevision; var nextScanBefore = _nextConnectionScan;
        var scanningBefore = _scanning; var readingBefore = _readingDevice; var guideBefore = _checkingFirmwareGuide;
        var loadingBefore = _startupLoading; var finishingBefore = _startupFinishing; var closedBefore = _startupClosed;
        var enabledBefore = IsEnabled; var decisionBefore = _pendingEditDecisionOverride;
        static DetectedDevice DevicePresence(string id, bool stock = false, string instance = "fixture")
        {
            var board = BoardDescriptorRegistry.Get(id);
            var vid = stock ? "320F" : "FEED";
            var pid = (stock ? board.StockProductId ?? throw new InvalidOperationException("No reviewed stock identity.") : board.RuntimeProductId).ToString("X4");
            return new(id, DeviceCatalog.Get(id).Name, vid, pid, "0001", $"USB\\VID_{vid}&PID_{pid}\\{instance}", "", "");
        }
        static Task<IReadOnlyList<DetectedDevice>> Snapshot(params DetectedDevice[] devices) => Task.FromResult<IReadOnlyList<DetectedDevice>>(devices);
        Button[] DeviceOptions() => DeviceButtons.Children.OfType<Button>().ToArray();
        string[] VisibleModels() => DeviceOptions().Select(button => (string)button.Tag).ToArray();
        SetupDevice[] SetupOptions() => SetupDeviceButtons.Children.OfType<Button>().Select(button => (SetupDevice)button.Tag).ToArray();
        string[] HighlightedModels() => DeviceOptions()
            .Where(button => button.BorderBrush is SolidColorBrush { Color.A: > 0 })
            .Select(button => (string)button.Tag).ToArray();
        void ClickDevice(string id) => DeviceOptions().Single(button => (string)button.Tag == id)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            _scanning = _readingDevice = _checkingFirmwareGuide = _startupLoading = _startupFinishing = _startupClosed = false;
            IsEnabled = true; _connectionScanFailed = false;
            _pendingEditDecisionOverride = () => PendingEditChoice.Discard;
            ResetWorkflowFixture("pro-iso");
            _devices = [];
            var pro = DevicePresence("pro-iso"); var pad = DevicePresence("numpad");
            await ScanDevicesAsync(scan: () => Snapshot(pro, pad));
            check(VisibleModels().SequenceEqual(new[] { "pro-iso", "numpad" }) && IsActiveDeviceConnected,
                "Connections: only the two present normal-mode models appear, not seven offline editor layouts");
            check(ReadDeviceButton.IsEnabled && !TransferButton.IsEnabled && DeviceConfigurationNeedsRead,
                "Connections: a newly connected model must be read before its stale local session can be transferred");
            MarkDeviceConfigurationRead(_project); BuildDeviceButtons();
            check(ReadDeviceButton.IsEnabled && TransferButton.IsEnabled, "Connections: verified configuration read enables transfer");
            ClickDevice("numpad");
            check(Device.Id == "numpad" && HighlightedModels().SequenceEqual(new[] { "numpad" }),
                "Connections: clicking the Numpad button opens its session and highlights only that device");
            ClickDevice("pro-iso");
            check(Device.Id == "pro-iso" && HighlightedModels().SequenceEqual(new[] { "pro-iso" }),
                "Connections: clicking the PRO button returns to its session and updates the active highlight");
            var stableButtons = DeviceOptions();
            await ScanDevicesAsync(scan: () => Snapshot(pro, pad));
            check(stableButtons.SequenceEqual(DeviceOptions()) && HighlightedModels().SequenceEqual(new[] { "pro-iso" }),
                "Connections: unchanged presence keeps the existing buttons and active selection stable");
            await ScanDevicesAsync(scan: () => Snapshot(DevicePresence("pro-iso", stock: true), DevicePresence("numpad", stock: true)));
            check(VisibleModels().Length == 0 && SetupOptions().Length == 2 &&
                SetupOptions().Any(device => device.Name == "GMMK Numpad") && !ReadDeviceButton.IsEnabled && !TransferButton.IsEnabled,
                "Connections: original firmware appears as setup cards, never duplicate editor cards or enabled transfers");
            var stockCards = SetupDeviceButtons.Children.OfType<Button>().ToArray();
            await ScanDevicesAsync(scan: () => Snapshot(DevicePresence("pro-iso", stock: true), DevicePresence("numpad", stock: true)));
            check(stockCards.SequenceEqual(SetupDeviceButtons.Children.OfType<Button>()), "Connections: unchanged stock presence keeps setup cards stable");
            foreach (var stock in StockDeviceCatalog.All.Where(item => item.HasFirmwareCandidate))
            {
                var device = new SetupDevice("stock-fixture", stock.IdentifiedName ?? DeviceCatalog.ModelGroupName(stock.CandidateDeviceIds),
                    stock.CandidateDeviceIds, stock.VendorId, stock.ProductId, [], false);
                await ScanDevicesAsync(scan: () => Snapshot(pro), setupScan: () => Task.FromResult<IReadOnlyList<SetupDevice>>([device]));
                var card = SetupDeviceButtons.Children.OfType<Button>().Single();
                var labels = (StackPanel)card.Content;
                check(SetupOptions().Single() == device && card.IsEnabled && ((TextBlock)labels.Children[1]).Text == L.T("Gerät einrichten") &&
                    labels.Children[2].Visibility == (device.IsExperimental ? Visibility.Visible : Visibility.Collapsed),
                    "Connections: stock model " + device.Name + " offers setup regardless of experimental status");
            }
            await ScanDevicesAsync(scan: () => Snapshot(pro), setupScan: () => Task.FromResult<IReadOnlyList<SetupDevice>>([
                new("unsupported-fixture", "Unsupported keyboard", [], 0x342D, 0xFFFF, [], false)]));
            check(SetupOptions().Length == 0, "Connections: identified devices without a firmware candidate never offer sidebar setup");

            var candidate = new DetectedDevice("unverified-usb-320f-5093", "Glorious USB 320F:5093", "320F", "5093", "0001", "USB\\VID_320F&PID_5093\\fixture", "", "");
            var falsePro = candidate with { DeviceId = "pro-iso" };
            await ScanDevicesAsync(scan: () => Snapshot(pro, pro, pro with { InstanceId = pro.InstanceId + "-two" }, candidate, falsePro));
            check(VisibleModels().SequenceEqual(new[] { "pro-iso" }),
                "Connections: duplicate model entries collapse, and PID5093 cannot be presented as the reviewed PRO");
            await ScanDevicesAsync(scan: () => Snapshot(candidate, falsePro));
            check(VisibleModels().Length == 0 && !ReadDeviceButton.IsEnabled && !TransferButton.IsEnabled &&
                SetupOptions().All(device => device.CandidateDeviceIds.SequenceEqual(new[] { "pro-rev2-iso" })),
                "Connections: stock revision candidates offer only setup, never an alias for the reviewed runtime");

            ResetWorkflowFixture("pro-ansi");
            _preferences = _preferences with { StartDevice = "pro-ansi" };
            _startupFinishing = true;
            await ScanDevicesAsync(selectInitial: true, scan: () => Snapshot(DevicePresence("numpad", stock: true)));
            check(Device.Id == "pro-ansi" && !IsDirty && !IsActiveDeviceConnected && SetupOptions().Length == 1,
                "Connections: original firmware offers setup without selecting an unreadable editor session");
            _startupFinishing = false;
            ResetWorkflowFixture("pro-ansi");
            _preferences = _preferences with { StartDevice = "numpad" };
            await ScanDevicesAsync(selectInitial: true, scan: () => Snapshot(pro, pad));
            check(Device.Id == "numpad", "Connections: a connected start preference is honored");
            ResetWorkflowFixture("pro-ansi");
            _project.Layers[0].Keys.First().Value.Color = "#123456";
            var dirtyInitial = Fingerprint(_project);
            await ScanDevicesAsync(selectInitial: true, scan: () => Snapshot(pad));
            check(Device.Id == "pro-ansi" && IsDirty && Fingerprint(_project) == dirtyInitial,
                "Connections: even initial metadata cannot replace an already modified local draft");

            ResetWorkflowFixture("pro-iso");
            await ScanDevicesAsync(scan: () => Snapshot(pro, pad));
            _project.Layers[0].Keys.First().Value.Color = "#A01337";
            var draft = Fingerprint(_project); var undoCount = _undo.Count; var path = _projectPath;
            SetLightingEditor(true); GlobalEffectEnabled.IsChecked = true; EffectChoice.SelectedValue = 3; EffectSpeed.Value = 177;
            ColorHex.Text = "#A0"; // Incomplete text must not be normalized by a background refresh.
            var decisions = 0;
            _pendingEditDecisionOverride = () => { decisions++; return PendingEditChoice.Cancel; };
            ClickDevice("numpad");
            check(decisions == 1 && Device.Id == "pro-iso" && HighlightedModels().SequenceEqual(new[] { "pro-iso" }) &&
                  Fingerprint(_project) == draft && HasPendingEffect && EffectSpeed.Value == 177 && ColorHex.Text == "#A0",
                "Connections: cancelling a device-button switch retains the active device, dirty draft and pending inputs");
            decisions = 0;
            _confirmedDeviceSaves["pro-iso"] = (draft, DateTime.Now);
            await ScanDevicesAsync(scan: () => Snapshot(pad));
            check(Device.Id == "pro-iso" && IsDirty && Fingerprint(_project) == draft && _projectPath == path && _undo.Count == undoCount,
                "Connections: unplug preserves the exact dirty draft, file association and undo history");
            check(HasPendingEffect && (int)EffectChoice.SelectedValue == 3 && EffectSpeed.Value == 177 && ColorHex.Text == "#A0" && decisions == 0,
                "Connections: unplug keeps unaccepted effect and color input without asking or applying anything");
            check(VisibleModels().SequenceEqual(new[] { "numpad" }) && HighlightedModels().Length == 0 && !IsActiveDeviceConnected,
                "Connections: unplug removes only that device's button and leaves the still-open draft disconnected");
            check(!_confirmedDeviceSaves.ContainsKey("pro-iso"), "Connections: unplug invalidates a previous device confirmation");
            await ScanDevicesAsync(scan: () => Snapshot(pro, pad));
            check(Device.Id == "pro-iso" && Fingerprint(_project) == draft && HasPendingEffect && decisions == 0 && !_confirmedDeviceSaves.ContainsKey("pro-iso"),
                "Connections: reconnect restores presence without reading over the draft or reviving a stale save confirmation");
            _pendingEditDecisionOverride = () => PendingEditChoice.Discard;
            DiscardPendingEdits(); RefreshAll();

            _confirmedDeviceSaves["pro-iso"] = (Fingerprint(_project), DateTime.Now);
            await ScanDevicesAsync(scan: () => Snapshot(pro with { InstanceId = pro.InstanceId + "-replacement" }, pad));
            check(!_confirmedDeviceSaves.ContainsKey("pro-iso"), "Connections: a replacement physical instance does not inherit the previous keyboard's save confirmation");
            var listBeforeFailure = VisibleModels(); var projectBeforeFailure = Fingerprint(_project);
            var stockPad = new SetupDevice("stock-pad", "GMMK Numpad", ["numpad"], 0x320F, 0x5088, [], false);
            await ScanDevicesAsync(scan: () => Snapshot(pro, pad), setupScan: () => Task.FromResult<IReadOnlyList<SetupDevice>>([stockPad]));
            _confirmedDeviceSaves["pro-iso"] = (projectBeforeFailure, DateTime.Now);
            await ScanDevicesAsync(scan: () => Task.FromException<IReadOnlyList<DetectedDevice>>(new IOException("Synthetic metadata error")));
            check(_connectionScanFailed && _connectionRefreshPending && VisibleModels().SequenceEqual(listBeforeFailure) && Fingerprint(_project) == projectBeforeFailure,
                "Connections: a scan failure is distinct from unplugging and preserves the last list and draft");
            check(!IsActiveDeviceConnected && !ReadDeviceButton.IsEnabled && !TransferButton.IsEnabled &&
                  DeviceOptions().All(button => !button.IsEnabled) && SetupDeviceButtons.Children.OfType<Button>().All(button => !button.IsEnabled) &&
                  SetupOptions().Single() == stockPad && _confirmedDeviceSaves.Count == 0,
                "Connections: unknown presence disables device actions and clears stale confirmations");
            await ScanDevicesAsync(scan: () => Snapshot());
            check(!_connectionScanFailed && VisibleModels().Length == 0 && SetupOptions().Length == 0 && DeviceModeText.Text == L.T("Kein Gerät verbunden") && Fingerprint(_project) == projectBeforeFailure,
                "Connections: a successful empty snapshot reports no connected devices without changing the local draft");

            await ScanDevicesAsync(scan: () => Snapshot(pro));
            var stale = new TaskCompletionSource<IReadOnlyList<DetectedDevice>>();
            var staleScan = ScanDevicesAsync(scan: () => stale.Task);
            RequestConnectionRefresh();
            stale.SetResult([pad]); await staleScan;
            check(VisibleModels().SequenceEqual(new[] { "pro-iso" }) && _connectionRefreshPending && !_scanning,
                "Connections: a newer hotplug event invalidates an older scan result");
            var blockedReads = 0;
            foreach (var state in new[] { "read", "firmware", "startup", "finalizing", "modal", "closed" })
            {
                _readingDevice = state == "read"; _checkingFirmwareGuide = state == "firmware";
                _startupLoading = state == "startup"; _startupFinishing = state == "finalizing";
                _startupClosed = state == "closed"; IsEnabled = state != "modal";
                await ScanDevicesAsync(scan: () => { blockedReads++; return Snapshot(pad); });
                check(blockedReads == 0 && VisibleModels().SequenceEqual(new[] { "pro-iso" }),
                    "Connections: metadata refresh is deferred during " + state);
            }
            _readingDevice = _checkingFirmwareGuide = _startupLoading = _startupFinishing = _startupClosed = false; IsEnabled = true;
            foreach (var state in new[] { "read", "firmware", "modal", "closed" })
            {
                var pending = new TaskCompletionSource<IReadOnlyList<DetectedDevice>>();
                var pendingScan = ScanDevicesAsync(scan: () => pending.Task);
                _readingDevice = state == "read"; _checkingFirmwareGuide = state == "firmware";
                _startupClosed = state == "closed"; IsEnabled = state != "modal";
                pending.SetResult([pad]); await pendingScan;
                check(VisibleModels().SequenceEqual(new[] { "pro-iso" }) && !_scanning,
                    "Connections: an in-flight result cannot change the UI after " + state + " begins");
                _readingDevice = _checkingFirmwareGuide = _startupClosed = false; IsEnabled = true;
            }

            await ScanDevicesAsync(scan: () => Snapshot(pad));
            var settings = new AppSettingsWindow(_preferences with { StartDevice = "pro-ansi" }, offlinePreview: true, connectedDevices: ConnectedDevices);
            try
            {
                var choices = ((ComboBox)settings.FindName("StartDeviceChoice")).Items.Cast<ComboBoxItem>().Select(item => (string)item.Tag).ToArray();
                check(choices.SequenceEqual(new[] { "last", "numpad" }), "Connections: settings offers last-used plus only currently connected devices");
                check(settings.Preferences.StartDevice == "pro-ansi", "Connections: opening settings never silently rewrites a stored disconnected preference");
            }
            finally { settings.Close(); }

            ResetWorkflowFixture("pro-iso");
            _project.Layers[0].Keys.First().Value.Color = "#B01337"; SwitchDevice("numpad");
            _project.Layers[0].Keys.First().Value.Color = "#C01337";
            var savedModels = new List<string>();
            var closeAccepted = ConfirmCloseProjects(_ => UnsavedChangesChoice.Save, () => { savedModels.Add(Device.Id); _savedFingerprint = Fingerprint(_project); return true; });
            check(closeAccepted && savedModels.Order().SequenceEqual(new[] { "numpad", "pro-iso" }),
                "Connections: closing can save every dirty session, including an unplugged keyboard, through the internal session path");
            var beforeNoProvider = VisibleModels();
            await ScanDevicesAsync();
            check(VisibleModels().SequenceEqual(beforeNoProvider) && !_scanning,
                "Connections: an offline smoke scan without an injected provider cannot reach live PnP");
        }
        finally
        {
            _readingDevice = _checkingFirmwareGuide = _startupLoading = _startupFinishing = _startupClosed = _scanning = false;
            IsEnabled = true; _pendingEditDecisionOverride = () => PendingEditChoice.Discard;
            DiscardPendingEdits(); ResetWorkflowFixture("pro-iso");
            _preferences = preferencesBefore; _devices = devicesBefore; _setupDevices = setupDevicesBefore;
            _connectionScanFailed = scanFailedBefore; _connectionRefreshPending = pendingBefore;
            _connectionRevision = revisionBefore; _nextConnectionScan = nextScanBefore;
            _confirmedDeviceSaves.Clear(); foreach (var entry in confirmationsBefore) _confirmedDeviceSaves.Add(entry.Key, entry.Value);
            _scanning = scanningBefore; _readingDevice = readingBefore; _checkingFirmwareGuide = guideBefore;
            _startupLoading = loadingBefore; _startupFinishing = finishingBefore; _startupClosed = closedBefore;
            _deviceReadRequired.Clear(); foreach (var pair in requiredBefore) _deviceReadRequired.Add(pair.Key, pair.Value);
            IsEnabled = enabledBefore; _pendingEditDecisionOverride = decisionBefore;
            BuildDeviceButtons(); UpdateSourceHint();
        }
    }
}
