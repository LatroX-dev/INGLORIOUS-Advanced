using Avalonia.Controls;
using Avalonia.LogicalTree;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private async Task RunConnectionsChecksAsync(Action<bool, string> check)
    {
        if (!Program.Offline) throw new InvalidOperationException("Connection checks require offline mode.");
        var savedState = _state; var savedSessions = _sessions.ToArray(); var savedConfirmed = _confirmed.ToArray();
        var savedRequired = _deviceReadRequired.ToArray(); var savedFallbacks = _startupFallbackNotices.ToArray(); var savedNotices = _startupNotices.ToArray();
        var savedIds = _connectedDeviceIds; var savedFailure = _connectionsFailed;
        var savedSetupDevices = _connectedSetupDevices; var savedSetupReader = _readSetupConnections;
        var savedPending = _pendingApply; var savedPrompt = _offlinePromptChoice; var savedPage = _page;
        var savedEffects = _showEffects;
        var savedBusy = _busy; var savedLoading = _startupLoading; var savedClosed = _connectionsClosed;
        var savedAllowClose = _allowClose; var savedNavigating = _navigating; var savedRevision = _connectionRevision;
        var savedReader = _readConnections; var savedEnabled = IsEnabled; var savedStatus = _status;
        var savedInitial = _initialConnectionsPending; var savedPreferences = _preferences;
        try
        {
            check(PlatformDeviceService.ConnectedModelFromUsbMetadata(0x320F, 0x5046) is null &&
                PlatformDeviceService.ConnectedModelFromUsbMetadata(0x320F, 0x5088) is null,
                "stock USB metadata never enables runtime editors or creates duplicate PRO/numpad cards");
            check(PlatformDeviceService.ConnectedModelFromUsbMetadata(0xFEED, 0x1C01) == "pro-iso" &&
                PlatformDeviceService.ConnectedModelFromUsbMetadata(0xFEED, 0x1C02) == "numpad", "runtime USB metadata identifies both supported devices");
            check(PlatformDeviceService.ConnectedModelFromUsbMetadata(0x320F, 0x1C01) is null &&
                PlatformDeviceService.ConnectedModelFromUsbMetadata(0xFEED, 0x5088) is null &&
                PlatformDeviceService.ConnectedModelFromUsbMetadata(0x342D, 0xDFA0) is null &&
                PlatformDeviceService.ConnectedModelFromUsbMetadata(0x320F, 0xFFFF) is null, "crossed identities, bootloaders and unknown products are not connected model evidence");
            _busy = _startupLoading = _connectionsClosed = _allowClose = _navigating = _initialConnectionsPending = false; IsEnabled = true;
            _state = new EditorState("pro-iso"); _state.MarkInitialClean(); _page = "Beleuchtung"; _showEffects = false;
            _sessions["pro-iso"] = _state; Build(); ApplyConnectionSnapshot([]);
            check(_deviceButtonsPanel!.Children.Count == 0 && _deviceButtons.Count == 0 &&
                _connectionHint!.IsVisible && _connectionHint.Text == L.T("Kein Gerät verbunden"), "empty discovery renders no invented device buttons and an explicit empty hint");
            check(!_readDeviceButton!.IsEnabled && !_transferButton!.IsEnabled && _deviceState.Text == L.T("Gerät nicht verbunden"), "disconnected active profile cannot read or transfer");
            var stocks = StockDeviceCatalog.All.Select((stock, index) => new SetupDevice("offline-stock-" + index,
                stock.IdentifiedName ?? DeviceCatalog.ModelGroupName(stock.CandidateDeviceIds), stock.CandidateDeviceIds,
                stock.VendorId, stock.ProductId, [], false)).ToArray();
            var stockPad = stocks.Single(device => device.CandidateDeviceIds.SequenceEqual(new[] { "numpad" }));
            var stockPro = stocks.First(device => device.CandidateDeviceIds.Contains("pro-iso"));
            var stockPro3 = stocks.Single(device => device.CandidateDeviceIds.Contains("gmmk3-pro-100-iso"));
            ApplyConnectionSnapshot([], setupDevices: stocks);
            check(_connectedSetupDevices.Length == stocks.Length && _deviceButtons.Count == 0 && _setupDeviceButtons.Count == stocks.Length &&
                _deviceButtonsPanel.Children.Count == stocks.Length && !_connectionHint!.IsVisible,
                "every supported connected stock model gets one setup card without a misleading disconnected hint");
            check(_setupDeviceButtons.Values.All(button => button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == L.T("Gerät einrichten"))) &&
                stocks.All(device => _setupDeviceButtons[device.InstanceId].GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "ExperimentalSetupDeviceBadge") ==
                    device.CandidateDeviceIds.Any(id => DeviceCatalog.Get(id).IsExperimentalFirmware)),
                "setup cards show their localized action and experimental models remain clearly marked");
            check(!_readDeviceButton.IsEnabled && !_transferButton!.IsEnabled && _state.Device.Id == "pro-iso",
                "stock presence never enables upload or replaces the user's current profile");
            var pro3Card = _setupDeviceButtons[stockPro3.InstanceId];
            ApplyConnectionSnapshot([], setupDevices: stocks);
            check(ReferenceEquals(pro3Card, _setupDeviceButtons[stockPro3.InstanceId]), "unchanged stock scan preserves setup controls");
            ApplyConnectionSnapshot(["numpad"], setupDevices: [stockPro, stockPro3,
                stockPad with { InstanceId = "runtime-fixture", IsRuntime = true }, stockPad with { InstanceId = "unsupported", CandidateDeviceIds = [] },
                stockPad with { InstanceId = "unknown-product", ProductId = 0xFFFF }]);
            check(_deviceButtons.Count == 1 && _setupDeviceButtons.Count == 2 && _deviceButtonsPanel.Children.Count == 3,
                "runtime cards coexist with supported stock cards while unsupported and runtime metadata rows are excluded");
            ApplyConnectionSnapshot([], failed: true);
            check(_setupDeviceButtons.Values.All(button => !button.IsEnabled), "failed USB scan disables stale setup cards");
            ApplyConnectionSnapshot([], setupDevices: [stockPro3, stockPro3]);
            check(_setupDeviceButtons.Count == 0, "ambiguous duplicate physical rows cannot offer a setup action");
            ApplyConnectionSnapshot([]);
            check(_setupDeviceButtons.Count == 0 && _deviceButtonsPanel.Children.Count == 0, "unplugged setup cards are removed");
            ApplyConnectionSnapshot(["numpad", "unknown", "pro-iso", "numpad"]);
            check(_selectorDeviceIds.SequenceEqual(new[] { "pro-iso", "numpad" }) && _deviceButtons.Count == 2 && _deviceButtons["pro-iso"].Classes.Contains("active-device") && !_deviceButtons["numpad"].Classes.Contains("active-device"),
                "positive models are deduplicated, unknowns excluded and no offline catalog is appended");
            check(_readDeviceButton.IsEnabled && !_transferButton!.IsEnabled && DeviceConfigurationNeedsRead, "newly connected profile requires a current read before transfer");
            MarkDeviceConfigurationRead(_state.Project); UpdateConnectionActions();
            check(_readDeviceButton.IsEnabled && _transferButton!.IsEnabled, "verified configuration read enables transfer");
            check(!_deviceButtonsPanel.GetLogicalDescendants().OfType<ComboBox>().Any() &&
                _deviceButtonsPanel.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(icon => icon.Name == "KeyboardDeviceIcon") &&
                _deviceButtonsPanel.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(icon => icon.Name == "NumpadDeviceIcon") &&
                _deviceButtonsPanel.GetLogicalDescendants().OfType<TextBlock>().Count(text => text.Text == L.T("USB verbunden")) == 2,
                "connected models are separate keyboard/numpad buttons with readable USB subtitles, not a dropdown");
            var editor = _editor; var root = _root; var preview = _keyboard; var choice = _deviceButtonsPanel; var proButton = _deviceButtons["pro-iso"]; var padButton = _deviceButtons["numpad"];
            ApplyConnectionSnapshot(["pro-iso", "numpad"]);
            check(ReferenceEquals(proButton, _deviceButtons["pro-iso"]) && ReferenceEquals(padButton, _deviceButtons["numpad"]), "unchanged presence preserves existing device buttons");
            var hex = _editor.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ColorHex");
            hex.Text = "#1A2B3C"; var pending = _pendingApply;
            check(pending is not null, "connection fixture includes a real pending color edit");
            _state.Change(project => project.Name = "Unsaved device profile"); var fingerprint = EditorState.Fingerprint(_state.Project);
            _confirmed[_state.Device.Id] = fingerprint;
            ApplyConnectionSnapshot(["numpad"]);
            check(ReferenceEquals(_root, root) && ReferenceEquals(_editor, editor) && ReferenceEquals(_keyboard, preview) && ReferenceEquals(_deviceButtonsPanel, choice) && ReferenceEquals(padButton, _deviceButtons["numpad"]),
                "unplug refresh never rebuilds editor, preview or selector controls");
            check(ReferenceEquals(_pendingApply, pending) && hex.Text == "#1A2B3C" && _state.Dirty && EditorState.Fingerprint(_state.Project) == fingerprint,
                "unplug preserves pending input, unsaved profile and undo state");
            check(_state.Device.Id == "pro-iso" && !_deviceButtons.Values.Any(button => button.Classes.Contains("active-device")) && choice.Children.Count == 1 && !_confirmed.ContainsKey("pro-iso"),
                "unplug preserves active session without auto-selecting another model or keeping stale confirmation");
            _offlinePromptChoice = 0; await SelectConnectedDeviceAsync("numpad");
            check(_state.Device.Id == "pro-iso" && !_deviceButtons.Values.Any(button => button.Classes.Contains("active-device")) && ReferenceEquals(_pendingApply, pending),
                "cancelled device navigation restores disconnected selection without discarding pending input");
            ApplyConnectionSnapshot(["pro-iso", "numpad"]);
            check(_deviceButtons["pro-iso"].Classes.Contains("active-device") && !_connectionHint!.IsVisible && !_confirmed.ContainsKey("pro-iso") && _deviceState.Text == L.T("Noch nicht übertragen"),
                "reconnect restores current selector selection but does not invent a new readback confirmation");
            ApplyConnectionSnapshot([], failed: true);
            check(choice.Children.Count == 2 && _deviceButtons.Values.All(button => !button.IsEnabled) && _connectionHint!.Text == L.T("Gerätesuche fehlgeschlagen") && _deviceState.Text == L.T("Gerätesuche fehlgeschlagen") &&
                ReferenceEquals(_pendingApply, pending), "discovery failure retains last observed models, clearly disables actions and preserves pending state");
            check(ConnectedStartDeviceIds().SequenceEqual(new[] { "last" }), "failed discovery cannot offer unverified start-device choices");
            ApplyConnectionSnapshot(["numpad"]);
            check(!_connectionsFailed && _connectionHint!.Text == L.T("Gerät nicht verbunden"), "a later successful scan clears only the discovery error");
            check(ConnectedStartDeviceIds().SequenceEqual(new[] { "last", "numpad" }), "settings offers last device and positively connected models only");
            check(StartDevicePreference(ConnectedStartDeviceIds(), -1, "pro-ansi") == "pro-ansi" &&
                StartDevicePreference(ConnectedStartDeviceIds(), 1, "pro-ansi") == "numpad", "unavailable saved start preference survives unrelated settings edits until explicitly replaced");

            var calls = 0;
            _readConnections = () => { calls++; throw new InvalidOperationException("Real discovery must never run offline."); };
            _readSetupConnections = () => { calls++; throw new InvalidOperationException("Real setup discovery must never run offline."); };
            await RefreshConnectionsAsync(); await StartConnectionsAsync();
            check(calls == 0 && !_connectionTimer.IsEnabled, "offline startup and polling never invoke the real discovery provider");
            Task<IReadOnlyList<string>> Fixture() { calls++; return Task.FromResult<IReadOnlyList<string>>(["pro-iso"]); }
            _busy = true; await RefreshConnectionsAsync(Fixture); _busy = false;
            _startupLoading = true; await RefreshConnectionsAsync(Fixture); _startupLoading = false;
            _navigating = true; await RefreshConnectionsAsync(Fixture); _navigating = false;
            IsEnabled = false; await RefreshConnectionsAsync(Fixture); IsEnabled = true;
            check(calls == 0, "busy, startup, navigation and modal-disabled owner defer discovery before calling a provider");
            await RefreshConnectionsAsync(Fixture);
            check(calls == 1 && _connectedDeviceIds.SequenceEqual(new[] { "pro-iso" }), "explicit offline fixture updates presence without device I/O");
            await RefreshConnectionsAsync(() => Task.FromResult<IReadOnlyList<string>>([]), () => Task.FromResult<IReadOnlyList<SetupDevice>>([stockPro3]));
            check(calls == 1 && _setupDeviceButtons.ContainsKey(stockPro3.InstanceId) && !_readDeviceButton!.IsEnabled && !_transferButton!.IsEnabled,
                "explicit stock fixture updates sidebar without HID access or editor transfer");
            await RefreshConnectionsAsync(() => Task.FromException<IReadOnlyList<string>>(new IOException("Synthetic metadata failure")));
            check(_connectionsFailed && !_transferButton!.IsEnabled && ReferenceEquals(_pendingApply, pending), "failed async discovery disables hardware actions without affecting pending input");

            ApplyConnectionSnapshot(["pro-iso"]);
            var delayed = new TaskCompletionSource<IReadOnlyList<string>>();
            var first = RefreshConnectionsAsync(() => { calls++; return delayed.Task; });
            await RefreshConnectionsAsync(Fixture);
            check(_scanningConnections && calls == 2, "only one metadata scan can remain in flight");
            _busy = true; delayed.SetResult(["numpad"]); await first; _busy = false;
            check(_connectedDeviceIds.SequenceEqual(new[] { "pro-iso" }) && !_scanningConnections, "a scan completed during an operation is ignored without editor rebuild");
            delayed = new(); first = RefreshConnectionsAsync(() => delayed.Task);
            IsEnabled = false; delayed.SetResult(["numpad"]); await first; IsEnabled = true;
            check(_connectedDeviceIds.SequenceEqual(new[] { "pro-iso" }), "late scan cannot update a modal-disabled owner");
            delayed = new(); first = RefreshConnectionsAsync(() => delayed.Task);
            StopConnections(); delayed.SetResult(["numpad"]); await first;
            check(_connectionsClosed && !_connectionTimer.IsEnabled && _connectedDeviceIds.SequenceEqual(new[] { "pro-iso" }), "close stops the timer and invalidates late scan results");
            check(EditorState.Fingerprint(_state.Project) == fingerprint && ReferenceEquals(_pendingApply, pending) && hex.Text == "#1A2B3C", "all discovery outcomes preserve the actual pending editor and unsaved profile");

            _connectionsClosed = false; _pendingApply = null; _initialConnectionsPending = true;
            _state = new EditorState("pro-ansi"); _state.MarkInitialClean(); _preferences = _preferences with { StartDevice = "pro-ansi" };
            ApplyConnectionSnapshot([], setupDevices: [stockPad]);
            check(!SelectInitialConnectedSession() && _state.Device.Id == "pro-ansi", "initial stock-only discovery offers setup without treating it as an editable runtime");
            ApplyConnectionSnapshot(["numpad"]);
            check(SelectInitialConnectedSession() && _state.Device.Id == "numpad", "initial runtime discovery selects its connected session ahead of an unavailable offline preference");
            _initialConnectionsPending = false; _state = new EditorState("pro-iso"); _state.MarkInitialClean();
            check(!SelectInitialConnectedSession() && _state.Device.Id == "pro-iso", "later hotplug never automatically switches a clean active profile either");
            _initialConnectionsPending = true; _state.Change(project => project.Name = "Unsaved");
            check(!SelectInitialConnectedSession(), "initial fallback never overwrites an edited session");
            _state.MarkInitialClean(); _pendingApply = () => true;
            check(!SelectInitialConnectedSession(), "initial fallback never replaces pending form input");
        }
        finally
        {
            _state = savedState; _sessions.Clear(); foreach (var item in savedSessions) _sessions.Add(item.Key, item.Value);
            _confirmed.Clear(); foreach (var item in savedConfirmed) _confirmed.Add(item.Key, item.Value);
            _deviceReadRequired.Clear(); foreach (var item in savedRequired) _deviceReadRequired.Add(item.Key, item.Value);
            _startupFallbackNotices.Clear(); foreach (var item in savedFallbacks) _startupFallbackNotices.Add(item.Key, item.Value);
            _startupNotices.Clear(); foreach (var item in savedNotices) _startupNotices.Add(item.Key, item.Value);
            _connectedDeviceIds = savedIds; _connectionsFailed = savedFailure; _pendingApply = savedPending; _offlinePromptChoice = savedPrompt;
            _connectedSetupDevices = savedSetupDevices; _readSetupConnections = savedSetupReader;
            _page = savedPage; _showEffects = savedEffects;
            _busy = savedBusy; _startupLoading = savedLoading; _connectionsClosed = savedClosed;
            _allowClose = savedAllowClose; _navigating = savedNavigating; _connectionRevision = savedRevision;
            _readConnections = savedReader; IsEnabled = savedEnabled; _status = savedStatus;
            _initialConnectionsPending = savedInitial; _preferences = savedPreferences; Build();
        }
    }
}
