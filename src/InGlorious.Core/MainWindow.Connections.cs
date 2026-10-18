using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace InGlorious.Core;

public partial class MainWindow
{
    private DispatcherTimer? _connectionTimer;
    private HwndSource? _connectionWindow;
    private bool _connectionRefreshPending, _connectionScanFailed;
    private long _connectionRevision;
    private DateTime _nextConnectionScan;
    private SetupDevice[] _setupDevices = [];

    private static bool IsRuntimeDevice(DetectedDevice device) => device.IsSupported &&
        string.Equals(device.VendorId, "FEED", StringComparison.OrdinalIgnoreCase);

    private DeviceDefinition[] ConnectedDevices => DeviceCatalog.All
        .Where(model => _devices.Any(device => IsRuntimeDevice(device) && device.DeviceId == model.Id)).ToArray();
    private bool IsActiveDeviceConnected => !_connectionScanFailed &&
        _devices.Any(device => IsRuntimeDevice(device) && device.DeviceId == Device.Id);

    private void UpdateConnectedDeviceButtons()
    {
        var connected = ConnectedDevices;
        if (!DeviceButtons.Children.OfType<Button>().Select(button => (string)button.Tag).SequenceEqual(connected.Select(device => device.Id)))
        {
            DeviceButtons.Children.Clear();
            foreach (var device in connected)
            {
                var content = new Grid();
                content.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
                content.ColumnDefinitions.Add(new());
                var icon = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(device.Id == "numpad"
                        ? "M5,2 L19,2 L19,22 L5,22 Z M8,6 L16,6 M8,10 L10,10 M14,10 L16,10 M8,14 L10,14 M14,14 L16,14 M8,18 L10,18 M14,18 L16,18"
                        : "M2,5 L22,5 L22,19 L2,19 Z M5,9 L7,9 M10,9 L12,9 M15,9 L17,9 M19,9 L20,9 M5,12 L7,12 M10,12 L12,12 M15,12 L20,12 M6,16 L18,16"),
                    Width = 15, Height = 16, StrokeThickness = 1.5, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
                };
                var labels = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
                labels.Children.Add(new TextBlock { Text = device.Name, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                labels.Children.Add(new TextBlock { FontSize = 11, Margin = new Thickness(0, 5, 0, 0), Foreground = Brush("#A0A7B0"), TextWrapping = TextWrapping.Wrap });
                labels.Children.Add(new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0), Foreground = Brush("#E9B86B"), TextWrapping = TextWrapping.Wrap, Visibility = device.IsExperimentalFirmware ? Visibility.Visible : Visibility.Collapsed });
                content.Children.Add(icon); Grid.SetColumn(labels, 1); content.Children.Add(labels);
                var button = new Button
                {
                    Content = content, Tag = device.Id, Style = (Style)FindResource("QuietButton"), Foreground = Brush("#E4E7EB"),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10),
                    BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(0, 0, 0, 4)
                };
                System.Windows.Automation.AutomationProperties.SetName(button, device.Name);
                button.Click += DeviceButton_Click;
                DeviceButtons.Children.Add(button);
            }
        }
        foreach (var button in DeviceButtons.Children.OfType<Button>())
        {
            var id = (string)button.Tag;
            var device = DeviceCatalog.Get(id);
            var selected = id == Device.Id;
            button.Background = selected ? Brush("#2A2E33") : Brushes.Transparent;
            button.BorderBrush = selected ? Brush("#EF646B") : Brushes.Transparent;
            button.IsEnabled = !_connectionScanFailed;
            button.ToolTip = device.Name + "\n" + device.Subtitle;
            System.Windows.Automation.AutomationProperties.SetName(button, device.Name + (device.IsExperimentalFirmware ? " · " + L.T("Experimentell") : ""));
            var content = (Grid)button.Content;
            ((System.Windows.Shapes.Path)content.Children[0]).Stroke = Brush(selected ? "#EF646B" : "#A0A7B0");
            ((TextBlock)((StackPanel)content.Children[1]).Children[1]).Text = L.T(_connectionScanFailed ? "Gerätesuche fehlgeschlagen" : "USB verbunden");
            ((TextBlock)((StackPanel)content.Children[1]).Children[2]).Text = L.T("Experimentell");
        }
        UpdateSetupDeviceButtons();
    }

    private void UpdateSetupDeviceButtons()
    {
        if (!SetupDeviceButtons.Children.OfType<Button>().Select(button => ((SetupDevice)button.Tag).InstanceId)
            .SequenceEqual(_setupDevices.Select(device => device.InstanceId)))
        {
            SetupDeviceButtons.Children.Clear();
            foreach (var device in _setupDevices)
            {
                var labels = new StackPanel();
                labels.Children.Add(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap });
                labels.Children.Add(new TextBlock { FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Foreground = Brush("#EF646B"), TextWrapping = TextWrapping.Wrap });
                labels.Children.Add(new TextBlock { FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Foreground = Brush("#E9B86B"), TextWrapping = TextWrapping.Wrap });
                var button = new Button
                {
                    Content = labels, Tag = device, Style = (Style)FindResource("QuietButton"),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10),
                    BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(0, 0, 0, 4)
                };
                button.Click += SetupDeviceButton_Click;
                SetupDeviceButtons.Children.Add(button);
            }
        }
        for (var index = 0; index < _setupDevices.Length; index++)
        {
            var device = _setupDevices[index];
            var button = (Button)SetupDeviceButtons.Children[index];
            button.Tag = device;
            button.IsEnabled = !_connectionScanFailed && !_readingDevice && !_checkingFirmwareGuide && !_startupLoading && !_startupFinishing;
            var labels = (StackPanel)button.Content;
            ((TextBlock)labels.Children[0]).Text = device.Name;
            ((TextBlock)labels.Children[1]).Text = L.T("Gerät einrichten");
            ((TextBlock)labels.Children[2]).Text = L.T("Experimentell");
            labels.Children[2].Visibility = device.IsExperimental ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(button, device.Name + " · " + L.T("Gerät einrichten") +
                (device.IsExperimental ? " · " + L.T("Experimentell") : ""));
        }
    }

    private async void SetupDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SetupDevice selected } || _connectionScanFailed ||
            !_setupDevices.Any(device => device.InstanceId == selected.InstanceId)) return;
        await OpenDeviceSetupAsync(preferredInstanceId: selected.InstanceId);
    }

    private static SetupDevice[] StockSetupDevices(IEnumerable<SetupDevice> devices) => devices
        .Where(device => !device.IsRuntime && device.HasFirmwareCandidate &&
            StockDeviceCatalog.Match(device.VendorId, device.ProductId) is { } stock &&
            stock.CandidateDeviceIds.SequenceEqual(device.CandidateDeviceIds) &&
            device.CandidateDeviceIds.All(id => BoardDescriptorRegistry.TryGet(id, out var board) && !board.IsEngineeringOnly))
        .DistinctBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
        .OrderBy(device => device.Name, StringComparer.Ordinal).ThenBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase).ToArray();

    private static SetupDevice[] StockSetupDevices(IEnumerable<DetectedDevice> devices) => StockSetupDevices(devices.Select(device =>
    {
        if (!ushort.TryParse(device.VendorId, System.Globalization.NumberStyles.HexNumber, null, out var vendor) ||
            !ushort.TryParse(device.ProductId, System.Globalization.NumberStyles.HexNumber, null, out var product) ||
            StockDeviceCatalog.Match(vendor, product) is not { } stock) return null;
        return new SetupDevice(device.InstanceId, stock.IdentifiedName ?? DeviceCatalog.ModelGroupName(stock.CandidateDeviceIds),
            stock.CandidateDeviceIds, vendor, product, [], false);
    }).OfType<SetupDevice>());

    private void DeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || _connectionScanFailed || !ConnectedDevices.Any(device => device.Id == id)) return;
        SwitchDevice(id);
        UpdateConnectedDeviceButtons();
    }

    private bool CanRefreshConnections(bool initial = false) => !_startupClosed && !_startupLoading &&
        (initial || !_startupFinishing) && !_readingDevice && !_checkingFirmwareGuide && IsEnabled &&
        !OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

    private void StartConnectionMonitor()
    {
        if (_screenshotMode || _startupClosed || _connectionTimer is not null) return;
        _connectionWindow = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _connectionWindow?.AddHook(ConnectionMessage);
        Activated += Connections_Activated;
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _connectionTimer.Tick += ConnectionTimer_Tick;
        _connectionTimer.Start();
        RequestConnectionRefresh();
    }

    private void StopConnectionMonitor()
    {
        Activated -= Connections_Activated;
        if (_connectionTimer is not null)
        {
            _connectionTimer.Stop();
            _connectionTimer.Tick -= ConnectionTimer_Tick;
            _connectionTimer = null;
        }
        _connectionWindow?.RemoveHook(ConnectionMessage);
        _connectionWindow = null;
        _connectionRevision++;
    }

    private nint ConnectionMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // WM_DEVICECHANGE is metadata notification only. Do not open HID handles here.
        if (message == 0x0219) RequestConnectionRefresh();
        return 0;
    }

    private void Connections_Activated(object? sender, EventArgs e) => RequestConnectionRefresh();

    private void RequestConnectionRefresh()
    {
        _connectionRefreshPending = true;
        _connectionRevision++;
        _nextConnectionScan = DateTime.UtcNow.AddMilliseconds(600);
    }

    private async void ConnectionTimer_Tick(object? sender, EventArgs e)
    {
        if (_connectionRefreshPending && DateTime.UtcNow >= _nextConnectionScan && CanRefreshConnections())
            await ScanDevicesAsync();
        if (_configurationRefreshPending.Count > 0 && CanRefreshConnections()) await RefreshDeviceConfigurationsAsync();
    }

    private void ApplyConnectionSnapshot(IReadOnlyList<DetectedDevice> devices, bool selectInitial = false, IReadOnlyList<SetupDevice>? setupDevices = null)
    {
        var changedConnections = DeviceCatalog.All.Where(model => !model.IsOfflineOnly).Where(model =>
        {
            var previous = _devices.Where(device => IsRuntimeDevice(device) && device.DeviceId == model.Id).Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var present = devices.Where(device => IsRuntimeDevice(device) && device.DeviceId == model.Id).Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return !previous.SetEquals(present);
        }).Select(model => model.Id).ToArray();
        foreach (var id in _confirmedDeviceSaves.Keys.ToArray())
        {
            var previous = _devices.Where(device => device.IsSupported && device.DeviceId == id).Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var present = devices.Where(device => device.IsSupported && device.DeviceId == id).Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (present.Count == 0 || previous.Count > 0 && !previous.SetEquals(present)) _confirmedDeviceSaves.Remove(id);
        }
        _devices = devices.ToArray();
        _setupDevices = setupDevices is null ? StockSetupDevices(devices) : StockSetupDevices(setupDevices);
        _connectionScanFailed = false;
        // Initial selection happens while the editor is still locked. Later connections
        // invalidate the old read; the refresh preserves drafts and pending inputs.
        var connected = ConnectedDevices;
        if (!selectInitial)
            foreach (var id in changedConnections) RequireDeviceConfigurationRead(id);
        if (selectInitial && !connected.Any(device => device.Id == Device.Id) && connected.Length > 0 && !IsDirty && !HasPendingEdits)
        {
            var target = connected.FirstOrDefault(device => device.Id == _preferences.ResolveStartDevice())?.Id ?? connected[0].Id;
            CaptureSession();
            if (_sessions.TryGetValue(target, out var session)) RestoreEditorSession(session);
            else ApplyOpening(OpenDefaultProject(target, allowDefault: _loadDefaultProjects));
            RefreshAll();
        }
        BuildDeviceButtons();
        UpdateSourceHint();
        RequestDeviceConfigurationRefresh(selectInitial
            ? connected.Where(device => _deviceReadRequired.ContainsKey(device.Id)).Select(device => device.Id)
            : changedConnections.Where(id => connected.Any(device => device.Id == id)));
    }

    private async Task ScanDevicesAsync(bool selectInitial = false,
        Func<Task<IReadOnlyList<DetectedDevice>>>? scan = null, Func<Task<IReadOnlyList<SetupDevice>>>? setupScan = null)
    {
        if (_scanning || (_screenshotMode && scan is null) || !CanRefreshConnections(selectInitial)) return;
        var revision = _connectionRevision;
        _scanning = true;
        _connectionRefreshPending = false;
        try
        {
            IReadOnlyList<DetectedDevice> devices;
            IReadOnlyList<SetupDevice>? setupDevices = null;
            if (scan is not null)
            {
                devices = await scan();
                if (setupScan is not null) setupDevices = await setupScan();
            }
            else
            {
                // One metadata snapshot covers stock and INGLORIOUS devices. No HID handles or firmware writes.
                setupDevices = await new DeviceSetupService().ListDevicesAsync();
                devices = setupDevices.Where(device => device.IsRuntime && device.CandidateDeviceIds.Count == 1)
                    .Select(device => new DetectedDevice(device.CandidateDeviceIds[0], device.Name, device.VendorId.ToString("X4"),
                        device.ProductId.ToString("X4"), "unknown", device.InstanceId, "", "")).ToArray();
            }
            if (revision != _connectionRevision || !CanRefreshConnections(selectInitial))
            { _connectionRefreshPending = true; return; }
            ApplyConnectionSnapshot(devices, selectInitial, setupDevices);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Device presence refresh failed: " + ex);
            if (revision != _connectionRevision || !CanRefreshConnections(selectInitial))
            { _connectionRefreshPending = true; return; }
            // A failed query is not evidence of unplugging. Retain the last list,
            // disable device actions, and retry without changing any profile.
            _connectionScanFailed = true;
            _confirmedDeviceSaves.Clear();
            _connectionRefreshPending = true;
            _nextConnectionScan = DateTime.UtcNow.AddSeconds(10);
            BuildDeviceButtons();
            UpdateSourceHint();
        }
        finally
        {
            _scanning = false;
            if (CanRefreshConnections(selectInitial)) BuildDeviceButtons();
        }
    }

    private void SetOfflineConnectionFixture(IEnumerable<string> ids)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Connection fixtures require offline mode.");
        _devices = ids.Distinct().Where(id => BoardDescriptorRegistry.TryGet(id, out _)).Select(id =>
        {
            var board = BoardDescriptorRegistry.Get(id);
            return new DetectedDevice(id, DeviceCatalog.Get(id).Name, "FEED", board.RuntimeProductId.ToString("X4"),
                "0001", "USB\\VID_FEED&PID_" + board.RuntimeProductId.ToString("X4") + "\\fixture", "", "");
        }).ToArray();
    }
}
