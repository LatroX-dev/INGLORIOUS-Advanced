using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InGlorious.Core;
using System.Diagnostics;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _connectionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Func<Task<IReadOnlyList<string>>> _readConnections = PlatformDeviceService.FindConnectedModelsAsync;
    private string[] _connectedDeviceIds = [], _selectorDeviceIds = [];
    private bool _connectionsFailed, _scanningConnections, _connectionsStarted, _connectionsClosed;
    private bool _initialConnectionsPending = !Program.Offline;
    private int _connectionRevision;
    private StackPanel? _deviceButtonsPanel;
    private readonly Dictionary<string, Button> _deviceButtons = [];
    private TextBlock? _connectionHint;
    private Button? _readDeviceButton, _transferButton;

    private bool CurrentModelConnected => !_connectionsFailed && _connectedDeviceIds.Contains(_state.Device.Id, StringComparer.Ordinal);
    private bool CanRefreshConnections => !_connectionsClosed && !_startupWindowClosed && !_allowClose &&
        !_busy && !_startupLoading && !_navigating && !_closing && !_building && IsEnabled &&
        !OwnedWindows.Any(window => window.IsVisible);

    private void InitializeOfflineConnections()
    {
        if (!Program.Offline) return;
        var index = Array.IndexOf(Program.Arguments, "--connected-devices");
        if (index >= 0 && index + 1 < Program.Arguments.Length)
            _connectedDeviceIds = NormalizeConnectedModels(Program.Arguments[index + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task StartConnectionsAsync()
    {
        if (Program.Offline || _connectionsStarted || _connectionsClosed || _startupWindowClosed || _allowClose) return;
        _connectionsStarted = true;
        _connectionTimer.Tick += async (_, _) => await RefreshConnectionsAsync();
        _connectionTimer.Start();
        await Task.CompletedTask;
    }

    private async Task LoadInitialConnectionsAsync()
    {
        if (Program.Offline || !_initialConnectionsPending || _startupWindowClosed || _allowClose) return;
        _busy = true; _root.IsEnabled = false;
        try
        {
            var ids = await _readConnections();
            if (_startupWindowClosed || _allowClose) return;
            ApplyConnectionSnapshot(ids);
            if (SelectInitialConnectedSession()) Build();
        }
        catch (Exception error)
        {
            Trace.TraceError("Initial USB metadata discovery failed: {0}", error);
            if (!_startupWindowClosed && !_allowClose) ApplyConnectionSnapshot([], failed: true);
        }
        finally
        {
            _initialConnectionsPending = _busy = false;
            if (!_startupWindowClosed && !_allowClose)
            { _root.IsEnabled = true; UpdateConnectionSelector(); UpdateFooter(); }
        }
    }

    private bool SelectInitialConnectedSession()
    {
        if (!_initialConnectionsPending || CurrentModelConnected || _connectionsFailed || _connectedDeviceIds.Length == 0 || _pendingApply is not null || _state.Dirty) return false;
        var preferred = _preferences.ResolveStartDevice();
        var selected = _connectedDeviceIds.Contains(preferred, StringComparer.Ordinal) ? preferred : _connectedDeviceIds[0];
        _state = OpenSession(selected); _macroId = null; return true;
    }

    private void StopConnections()
    {
        _connectionsClosed = true;
        _connectionRevision++;
        _connectionTimer.Stop();
    }

    private async Task RefreshConnectionsAsync(Func<Task<IReadOnlyList<string>>>? offlineSnapshot = null)
    {
        // Offline windows can enter this path only with an explicitly injected fixture.
        if (_scanningConnections || !CanRefreshConnections || (Program.Offline && offlineSnapshot is null)) return;
        var read = Program.Offline ? offlineSnapshot! : _readConnections;
        var revision = _connectionRevision;
        _scanningConnections = true;
        try
        {
            var ids = await read();
            if (revision == _connectionRevision && CanRefreshConnections) ApplyConnectionSnapshot(ids);
        }
        catch (Exception error)
        {
            Trace.TraceError("USB metadata discovery failed: {0}", error);
            if (revision == _connectionRevision && CanRefreshConnections) ApplyConnectionSnapshot([], failed: true);
        }
        finally { _scanningConnections = false; }
    }

    private static string[] NormalizeConnectedModels(IEnumerable<string> ids)
    {
        var known = ids.ToHashSet(StringComparer.Ordinal);
        return DeviceCatalog.All.Where(device => known.Contains(device.Id)).Select(device => device.Id).ToArray();
    }

    private void ApplyConnectionSnapshot(IEnumerable<string> ids, bool failed = false)
    {
        if (!failed) _connectedDeviceIds = NormalizeConnectedModels(ids);
        _connectionsFailed = failed;
        // A previous readback cannot confirm a later connection, or an unknown connection state.
        foreach (var id in _confirmed.Keys.Where(id => failed || !_connectedDeviceIds.Contains(id, StringComparer.Ordinal)).ToArray()) _confirmed.Remove(id);
        UpdateConnectionSelector();
        UpdateFooter();
    }

    private void UpdateConnectionSelector()
    {
        if (_deviceButtonsPanel is null || _connectionHint is null) return;
        if (!_selectorDeviceIds.SequenceEqual(_connectedDeviceIds) || _deviceButtons.Count != _connectedDeviceIds.Length)
        {
            _selectorDeviceIds = _connectedDeviceIds.ToArray();
            foreach (var id in _deviceButtons.Keys.Where(id => !_connectedDeviceIds.Contains(id, StringComparer.Ordinal)).ToArray()) _deviceButtons.Remove(id);
            foreach (var id in _connectedDeviceIds)
                if (!_deviceButtons.ContainsKey(id)) _deviceButtons[id] = CreateConnectedDeviceButton(DeviceCatalog.Get(id));
            _deviceButtonsPanel.Children.Clear();
            foreach (var id in _selectorDeviceIds) _deviceButtonsPanel.Children.Add(_deviceButtons[id]);
        }
        foreach (var (id, button) in _deviceButtons)
        {
            var active = CurrentModelConnected && id == _state.Device.Id;
            button.Background = active ? Brush("#3A252B") : Brushes.Transparent;
            button.BorderBrush = active ? Brush("#EF646B") : Brushes.Transparent;
            if (active) { if (!button.Classes.Contains("active-device")) button.Classes.Add("active-device"); }
            else button.Classes.Remove("active-device");
            var body = (Grid)button.Content!;
            var labels = (StackPanel)body.Children[1];
            ((TextBlock)labels.Children[1]).Text = _connectionsFailed ? L.T("Gerätesuche fehlgeschlagen") : L.T("USB verbunden");
        }
        _connectionHint.Text = _connectionsFailed ? L.T("Gerätesuche fehlgeschlagen") : _selectorDeviceIds.Length == 0 ? L.T("Kein Gerät verbunden") : L.T("Gerät nicht verbunden");
        _connectionHint.IsVisible = _connectionsFailed || _selectorDeviceIds.Length == 0 || !CurrentModelConnected;
        UpdateConnectionActions();
    }

    private Button CreateConnectedDeviceButton(DeviceDefinition device)
    {
        var body = new Grid { ColumnDefinitions = new("20,*"), ColumnSpacing = 7 };
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Name = device.HasSlider ? "NumpadDeviceIcon" : "KeyboardDeviceIcon",
            Data = Geometry.Parse(device.HasSlider
                ? "M5 2 L19 2 L19 22 L5 22 Z M8 6 L10 6 M14 6 L16 6 M8 10 L10 10 M14 10 L16 10 M8 14 L10 14 M14 14 L16 14 M8 18 L10 18 M14 18 L16 18"
                : "M2 5 L22 5 L22 19 L2 19 Z M5 9 L7 9 M10 9 L12 9 M15 9 L17 9 M19 9 L20 9 M5 13 L7 13 M10 13 L12 13 M15 13 L20 13 M6 16 L17 16"),
            Width = 20, Height = 20, Stretch = Stretch.Uniform, Stroke = Brush("#E4E7EB"), StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        body.Children.Add(icon);
        var labels = new StackPanel { Spacing = 3 };
        labels.Children.Add(Text(device.Name, size: 12)); labels.Children.Add(Text(L.T("USB verbunden"), true, 11));
        Grid.SetColumn(labels, 1); body.Children.Add(labels);
        var button = new Button
        {
            Name = "DeviceButton-" + device.Id, Content = body, Tag = device.Id,
            Padding = new Thickness(8, 8), MinHeight = 55, CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(2, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        button.Classes.Add("nav");
        Avalonia.Automation.AutomationProperties.SetName(button, device.Name);
        ToolTip.SetTip(button, device.Name + " · " + device.HardwareRevision);
        button.Click += async (_, _) => await SelectConnectedDeviceAsync(device.Id);
        return button;
    }

    private void UpdateConnectionActions()
    {
        var available = CurrentModelConnected && !_state.Device.IsOfflineOnly && !_busy && !_startupLoading && !_initialConnectionsPending;
        if (_readDeviceButton is not null) _readDeviceButton.IsEnabled = available;
        if (_transferButton is not null) _transferButton.IsEnabled = available;
        foreach (var button in _deviceButtons.Values) button.IsEnabled = !_connectionsFailed && !_busy && !_startupLoading && !_initialConnectionsPending;
    }

    private async Task SelectConnectedDeviceAsync(string id)
    {
        if (_building || _connectionsFailed || _connectionsClosed || !_connectedDeviceIds.Contains(id, StringComparer.Ordinal)) return;
        if (id == _state.Device.Id) return;
        if (!await GuardNavigation() || !_connectedDeviceIds.Contains(id, StringComparer.Ordinal) || _connectionsFailed || _connectionsClosed)
        { UpdateConnectionSelector(); return; }
        _state = OpenSession(id); _macroId = null; Build(); ShowStartupNotice();
    }

    private string[] ConnectedStartDeviceIds() => new[] { "last" }.Concat(_connectionsFailed ? [] : _connectedDeviceIds).ToArray();
    private static string StartDevicePreference(string[] choices, int selectedIndex, string savedPreference) =>
        selectedIndex >= 0 && selectedIndex < choices.Length ? choices[selectedIndex] : savedPreference;
}
