using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed class DeviceTestWindow : Window
{
    private readonly DeviceDefinition _device;
    private readonly OnboardDevice? _connection;
    private readonly TextBlock _status = Label(""), _count = Label("");
    private readonly StackPanel _physicalTest = new() { Name = "DevicePhysicalTest", Spacing = 6, IsVisible = false };
    private readonly StackPanel _typingPanel = new() { Name = "DeviceTypingTest", Spacing = 6, IsVisible = false };
    private readonly TextBlock _lastKey = Label(L.T("Noch keine Taste gedrückt."));
    private readonly StackPanel _hardwareControls = new() { Name = "DeviceHardwareControls", MaxWidth = 340, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _osSwitch = Label("");
    private readonly Button _start = new() { Name = "StartDeviceTest", Content = L.T("Test starten"), Focusable = false };
    private readonly List<Control> _lightControls = [];
    private readonly Dictionary<string, Action<IBrush>> _keyColors = [];
    private readonly HashSet<string> _typingKeys = [];
    private readonly Viewbox _keyboard = new() { Name = "DeviceTestKeyboardView", Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly CancellationTokenSource _lifetime = new();
    private DeviceDiagnosticSession? _session;
    private HardwareControlTestSession? _hardwareSession;
    private Task _starting = Task.CompletedTask, _pollTask = Task.CompletedTask;
    private bool _polling, _closing, _closed;
    private static IBrush ColorBrush(string color) => new SolidColorBrush(Color.Parse(color));

    public DeviceTestWindow(string deviceId, OnboardDevice? connection, bool preview = false)
    {
        _device = DeviceCatalog.Get(deviceId);
        if (connection is not null && connection.DeviceId != deviceId) throw new ArgumentException("Device test model mismatch.", nameof(connection));
        _connection = preview ? null : connection;
        _typingPanel.IsVisible = preview;
        Title = L.T("Gerät testen") + " · " + _device.Name;
        Width = 1060; Height = 720; MinWidth = 760; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Focusable = true;
        Background = ColorBrush("#171B1F"); Foreground = ColorBrush("#E4E7EB");

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 0), Spacing = 8 };
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16 };
        heading.Children.Add(new TextBlock { Text = _device.Name, FontSize = 24, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_hardwareControls, 1); heading.Children.Add(_hardwareControls); body.Children.Add(heading);
        body.Children.Add(Label(L.T("Die Tasten behalten ihre normale Funktion. Deine Einstellungen werden nicht verändert."), muted: true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        _start.IsEnabled = _connection is not null;
        _start.Classes.Add("primary");
        _start.Click += async (_, _) => { _starting = StartAsync(); await _starting; };
        _count.Name = "DeviceTestCount"; _count.VerticalAlignment = VerticalAlignment.Center; _count.IsVisible = false;
        actions.Children.Add(_start); actions.Children.Add(_count); body.Children.Add(actions);
        _hardwareControls.IsVisible = HardwareControlCatalog.HasOsSwitch(deviceId);
        _osSwitch.Name = "DeviceOsSwitch"; _osSwitch.IsVisible = HardwareControlCatalog.HasOsSwitch(deviceId);
        _osSwitch.FontSize = 12; _osSwitch.TextAlignment = TextAlignment.Right;
        _osSwitch.Text = HardwareControlCatalog.OsSwitchText(deviceId, null, false); _hardwareControls.Children.Add(_osSwitch);
        var unsupported = HardwareControlCatalog.UnsupportedText(deviceId);
        if (unsupported.Length > 0) { ToolTip.SetTip(_start, unsupported); ToolTip.SetTip(_hardwareControls, unsupported); ToolTip.SetTip(body, unsupported); }
        _typingPanel.Children.Add(Label(L.T("Drücke die Tasten, die du prüfen möchtest.")));
        if (_device.Id == "numpad") _typingPanel.Children.Add(Label(L.T("Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0."), muted: true));
        _lastKey.Name = "DeviceTypingLastKey";
        _typingPanel.Children.Add(_lastKey);
        _typingPanel.Children.Add(Label(L.T("Gelb zeigt die empfangene Eingabe. Bei eigenen Belegungen kann die Position abweichen."), muted: true));
        _typingPanel.Children.Add(Label(L.T("Die Eingabe kann auch von anderen angeschlossenen Tastaturen kommen."), muted: true));
        _physicalTest.Children.Add(Label(L.T("Drücke jede Taste einzeln und drehe den Knopf in beide Richtungen. Erkannte Tasten werden grün.")));
        _keyboard.Child = CreateKeyboard();
        _physicalTest.Children.Add(new TextBlock { Text = L.T("Beleuchtung prüfen"), FontSize = 17, FontWeight = FontWeight.SemiBold });
        _physicalTest.Children.Add(Label(L.T("Klicke eine Taste an, um nur ihre LED rot einzuschalten. Vergleiche die Position mit der Tastatur.")));
        var lights = new WrapPanel { Name = "DeviceTestLights" };
        foreach (var entry in new (string Name, byte R, byte G, byte B)[]
            { ("Alle weiß",255,255,255), ("Alle rot",255,0,0), ("Alle grün",0,255,0), ("Alle blau",0,0,255), ("Alle aus",0,0,0) })
        {
            var button = LightButton(entry.Name, () => LedAsync(null, entry.R, entry.G, entry.B));
            lights.Children.Add(button);
        }
        _physicalTest.Children.Add(lights);
        var extras = new WrapPanel { Name = "DeviceTestExtraLights" };
        foreach (var side in new[] { (Name: "Links", Keys: _device.LeftSideLedIndices), (Name: "Rechts", Keys: _device.RightSideLedIndices) })
        {
            if (side.Keys.Count == 0) continue;
            var group = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 16, 6) };
            group.Children.Add(Label(L.T(side.Name), muted: true));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            for (int index = 0; index < side.Keys.Count; index++)
            {
                int led = side.Keys[index];
                var button = LightButton((index + 1).ToString(), () => LedAsync(led, 255, 0, 0));
                button.Width = 28; button.Height = 30; button.Padding = new Thickness(0); button.Margin = new Thickness(0); button.FontSize = 11;
                button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
                var name = L.Format(side.Name == "Links" ? "Links {0}" : "Rechts {0}", index + 1);
                AutomationProperties.SetName(button, name); ToolTip.SetTip(button, name); row.Children.Add(button);
            }
            group.Children.Add(row); extras.Children.Add(group);
        }
        foreach (var led in _device.NonKeyLedIndices.Except(_device.LeftSideLedIndices).Except(_device.RightSideLedIndices))
            extras.Children.Add(LightButton("Logo", () => LedAsync(led, 255, 0, 0)));
        if (extras.Children.Count > 0) _physicalTest.Children.Add(extras);
        _physicalTest.Children.Add(LightButton("Eigene Beleuchtung anzeigen", RestoreLightingAsync));
        var lower = new StackPanel { Margin = new Thickness(24, 0, 24, 0) };
        lower.Children.Add(_physicalTest); lower.Children.Add(_typingPanel);
        SetLightingEnabled(false);
        _status.Name = "DeviceTestStatus";
        var close = new Button { Name = "CloseDeviceTest", Content = L.T("Fertig"), HorizontalAlignment = HorizontalAlignment.Right, Focusable = false };
        close.Click += (_, _) => Close();
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16, Margin = new Thickness(24, 8, 24, 20) };
        footer.Children.Add(_status); Grid.SetColumn(close, 1); footer.Children.Add(close);
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Focusable = true, Background = Background };
        layout.Children.Add(body); Grid.SetRow(_keyboard, 1); layout.Children.Add(_keyboard);
        Grid.SetRow(lower, 2); layout.Children.Add(lower); Grid.SetRow(footer, 3); layout.Children.Add(footer); Content = layout;
        _timer.Tick += async (_, _) => { if (_polling) return; _pollTask = PollAsync(); await _pollTask; };
        AddHandler(KeyDownEvent, OnTestKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnTestKeyUp, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, (_, e) =>
        {
            if (_closing) return;
            RecordTypingText(e.Text); e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var availableWidth = screen.WorkingArea.Width / screen.Scaling;
                var availableHeight = screen.WorkingArea.Height / screen.Scaling;
                MinWidth = Math.Min(MinWidth, availableWidth); MinHeight = Math.Min(MinHeight, availableHeight);
                Width = Math.Min(Width, availableWidth); Height = Math.Min(Height, availableHeight);
            }
            layout.Focus();
        };
        if (preview) ShowTypingTest(capabilityKnown: true);
        Closing += OnClosing;
    }

    private Canvas CreateKeyboard()
    {
        const double unit = 42;
        var canvas = new Canvas { Name = "DeviceTestKeyboard", Width = _device.PreviewWidth * unit, Height = _device.PreviewHeight * unit };
        foreach (var key in _device.Keys)
        {
            var label = new TextBlock { Text = key.DisplayLabel, FontSize = key.DisplayLabel.Length > 5 ? 8 : 10, Foreground = ColorBrush("#F2F5F7"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            var cap = new Grid(); cap.Children.Add(label);
            var button = new Button
            {
                Content = cap, Width = Math.Max(18, (key.W + (key.IsIsoEnter ? .25 : 0)) * unit - 4), Height = Math.Max(18, key.H * unit - 4),
                Padding = new Thickness(2), Background = ColorBrush("#252A2F"), BorderBrush = ColorBrush("#56616C"),
                Foreground = ColorBrush("#F2F5F7"), BorderThickness = new Thickness(1), Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
            };
            _keyColors[key.Id] = brush => button.Background = brush;
            if (key.IsIsoEnter)
            {
                var width = button.Width; var height = button.Height;
                var outline = Geometry.Parse(FormattableString.Invariant($"M 1,1 L {width-1},1 {width-1},{height-1} {.25*unit+1},{height-1} {.25*unit+1},{unit-5} 1,{unit-5} Z"));
                var shape = new Avalonia.Controls.Shapes.Path { Data = outline, Fill = button.Background, Stroke = button.BorderBrush, StrokeThickness = 1 };
                button.Clip = outline; button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Padding = new Thickness(0);
                cap.Children.Insert(0, shape); label.Margin = new Thickness(.25 * unit, 0, 0, 0);
                _keyColors[key.Id] = brush => shape.Fill = brush;
            }
            if (key.Label == "Knob") button.CornerRadius = new CornerRadius(99);
            if (key.LedIndex is { } led)
            { button.Click += async (_, _) => await LedAsync(led, 255, 0, 0); }
            else button.IsHitTestVisible = false;
            ToolTip.SetTip(button, key.AccessibleLabel); AutomationProperties.SetName(button, key.AccessibleLabel);
            Canvas.SetLeft(button, (key.X - (key.IsIsoEnter ? .25 : 0)) * unit);
            Canvas.SetTop(button, (key.Y + _device.PreviewTopInset) * unit);
            canvas.Children.Add(button);
        }
        return canvas;
    }

    private Button LightButton(string label, Func<Task> action)
    {
        var button = new Button { Content = L.T(label), Margin = new Thickness(0, 0, 8, 6), HorizontalAlignment = HorizontalAlignment.Left, Focusable = false };
        button.Click += async (_, _) => await action(); _lightControls.Add(button); return button;
    }
    private static TextBlock Label(string text, bool muted = false) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush(muted ? "#A7B0B8" : "#E4E7EB") };
    private void SetLightingEnabled(bool enabled)
    { foreach (var control in _lightControls) control.IsEnabled = enabled; }
    private static bool ActivatesControl(Key key) => DeviceTypingTest.IsTestNavigationKey(key.ToString());
    private void OnTestKeyDown(object? sender, KeyEventArgs e)
    {
        if (_closing) return;
        RecordTypingKey(sender, e);
        // Leave printable key-down events unhandled so the platform can deliver translated TextInput.
        if (_session is not null || ActivatesControl(e.Key)) e.Handled = true;
    }
    private void OnTestKeyUp(object? sender, KeyEventArgs e)
    { if (!_closing && (_session is not null || ActivatesControl(e.Key))) e.Handled = true; }
    private void RecordTypingKey(object? sender, KeyEventArgs e)
    {
        if (_closing || _session is not null || !_typingPanel.IsVisible) return;
        _lastKey.Text = L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel(e.Key.ToString()));
        foreach (var id in DeviceTypingTest.MatchingDisplayKeys(_device, e.Key.ToString()))
        { _typingKeys.Add(id); _keyColors[id](ColorBrush("#89661E")); }
    }
    private void ShowTypingTest(bool capabilityKnown)
    {
        _physicalTest.IsVisible = false; _count.IsVisible = false; SetLightingEnabled(false);
        _typingPanel.IsVisible = true; _start.IsVisible = !capabilityKnown;
        _status.Text = "";
    }
    private void RecordTypingText(string? text)
    {
        if (_closing || _session is not null || !_typingPanel.IsVisible || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) return;
        _lastKey.Text = L.Format("Zuletzt gedrückt: {0}", text);
        foreach (var id in DeviceTypingTest.MatchingTextDisplayKeys(_device, text))
        { _typingKeys.Add(id); _keyColors[id](ColorBrush("#89661E")); }
    }

    private async Task StartAsync()
    {
        if (_connection is null || _session is not null || _hardwareSession is not null || _closing || !_start.IsEnabled) return;
        _start.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var session = await DeviceDiagnosticSession.TryStartAsync(_connection, timeout.Token);
            if (_closing) { if (session is not null) await session.DisposeAsync(); return; }
            _session = session;
            _hardwareSession = await HardwareControlTestSession.TryStartAsync(_connection, timeout.Token);
            if (_closing) { await StopAsync(); return; }
            _osSwitch.Text = HardwareControlCatalog.OsSwitchText(_device.Id, _hardwareSession?.Status, true);
            if (_session is null)
            {
                ShowTypingTest(capabilityKnown: true); ((Control)Content!).Focus();
                if (_hardwareSession is not null) _timer.Start();
                return;
            }
            _physicalTest.IsVisible = _count.IsVisible = true; _typingPanel.IsVisible = false;
            _typingKeys.Clear();
            foreach (var color in _keyColors.Values) color(ColorBrush("#252A2F"));
            _count.Text = L.Format("{0} von {1} Eingaben erkannt", 0, _device.Keys.Count);
            _status.Text = L.T("Der Test läuft. Drücke die Tasten einzeln."); SetLightingEnabled(true); _timer.Start();
        }
        catch (Exception error) { await StopAsync(); if (!_closing) { _status.Text = Failure(error); _start.IsEnabled = true; _start.IsVisible = true; } }
    }
    private async Task PollAsync()
    {
        if (_polling || (_session is null && _hardwareSession is null) || _closing) return;
        _polling = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            if (_session is { } session)
            {
                var state = await session.PollAsync(timeout.Token);
                var seen = 0;
                foreach (var key in _device.Keys)
                {
                    var done = key.Id == "encoder:cw" ? state.Clockwise != 0 : key.Id == "encoder:ccw" ? state.Counterclockwise != 0 : state.WasPressed(key.Id);
                    if (done) seen++;
                    _keyColors[key.Id](ColorBrush(done ? "#23704A" : "#252A2F"));
                }
                _count.Text = L.Format("{0} von {1} Eingaben erkannt", seen, _device.Keys.Count);
            }
            if (!_closing && _hardwareSession is { } hardware)
                _osSwitch.Text = HardwareControlCatalog.OsSwitchText(_device.Id, await hardware.PollAsync(timeout.Token), true);
        }
        catch (Exception error)
        {
            _timer.Stop(); await StopAsync();
            if (!_closing) { _status.Text = Failure(error); _start.IsEnabled = true; _start.IsVisible = true; }
        }
        finally { _polling = false; }
    }
    private Task LedAsync(int? led, byte r, byte g, byte b) => ChangeLightingAsync((session, token) => session.SetLedAsync(led, r, g, b, token));
    private Task RestoreLightingAsync() => ChangeLightingAsync((session, token) => session.RestoreLightingAsync(token));
    private async Task ChangeLightingAsync(Func<DeviceDiagnosticSession, CancellationToken, Task> change)
    {
        var session = _session; if (session is null || _closing) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await change(session, timeout.Token);
        }
        catch (Exception error) { if (!_closing) _status.Text = Failure(error); }
    }
    private async Task StopAsync()
    {
        var session = _session; _session = null; SetLightingEnabled(false);
        var hardware = _hardwareSession; _hardwareSession = null;
        if (!_closing) _osSwitch.Text = HardwareControlCatalog.OsSwitchText(_device.Id, null, false);
        try
        {
            if (session is not null) await session.DisposeAsync();
        }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Device test cleanup failed: {0}", error); if (!_closing) _status.Text = Failure(error); }
        finally
        {
            if (hardware is not null)
            {
                try { await hardware.DisposeAsync(); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("Hardware-control test cleanup failed: {0}", error); if (!_closing) _status.Text = Failure(error); }
            }
        }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true; if (_closing) return;
        _closing = true; _timer.Stop();
        try { await DrainAndStopAsync(); }
        finally { _closed = true; _lifetime.Dispose(); Dispatcher.UIThread.Post(Close); }
    }
    private async Task DrainAndStopAsync(Func<Task>? offlineStop = null)
    {
        // Normal close drains the real operations before diagnostic STOP can release the shared channel.
        try { await _starting; }
        finally
        {
            try { await _pollTask; }
            finally
            {
                try { await (offlineStop is null ? StopAsync() : offlineStop()); }
                finally { _lifetime.Cancel(); }
            }
        }
    }
    private static string Failure(Exception error) => error is NotSupportedException ? error.Message : error is OnboardCommandException { Status: 3 }
        ? L.T("Lasse alle Tasten los und starte den Test erneut.") : L.T("Der Gerätetest wurde unterbrochen. Prüfe die USB-Verbindung und starte ihn erneut.");

    internal async Task RunOfflineChecksAsync(Action<bool, string> check)
    {
        if (_connection is not null) throw new InvalidOperationException("Device test checks require an offline preview.");
        // A native template is required for child-to-window event routes; this fixture never activates or reads hardware.
        ShowActivated = false; ShowInTaskbar = false; Opacity = 0; Show();
        await Dispatcher.Yield(DispatcherPriority.Background);
        try { RunOfflineInputAndLayoutChecks(check); await RunOfflineCloseChecksAsync(check); }
        finally { Close(); await Dispatcher.Yield(DispatcherPriority.Background); }
    }

    private void RunOfflineInputAndLayoutChecks(Action<bool, string> check)
    {
        HardwareControlCatalog.RunOfflineChecks(check);
        check(_hardwareControls.IsVisible == HardwareControlCatalog.HasOsSwitch(_device.Id) &&
            _osSwitch.IsVisible == HardwareControlCatalog.HasOsSwitch(_device.Id) && _hardwareSession is null,
            "Device test: hardware controls appear only for explicitly inventoried devices: " + _device.Id);
        check((ToolTip.GetTip(_start) as string ?? "") == HardwareControlCatalog.UnsupportedText(_device.Id),
            "Device test: unsupported hardware information is available without another layout row: " + _device.Id);
        check(_osSwitch.Text == HardwareControlCatalog.OsSwitchText(_device.Id, null, false) && !_timer.IsEnabled,
            "Device test: opening a test never invents a switch state or starts USB polling: " + _device.Id);
        check(_typingPanel.IsVisible && !_physicalTest.IsVisible && !_count.IsVisible && !this.GetLogicalDescendants().OfType<TextBox>().Any(),
            "Device test: typing is available before optional physical diagnostics without claiming a device key count");
        check(_keyboard.IsVisible && _keyboard.Child is Canvas { } keyboard && keyboard.Children.Count == _device.Keys.Count && _keyboard.IsHitTestVisible && _session is null,
            "Device test: every model retains its full diagram and tooltips without a hardware session: " + _device.Id);
        check(!_start.IsEnabled && _lightControls.All(control => !control.IsEnabled),
            "Device test: offline preview cannot start hardware diagnostics or change LEDs");
        var navigation = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Left };
        RaiseEvent(navigation);
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel("Left")) && navigation.Handled,
            "Device test: navigation keys are received directly by the window without an input field");
        var number = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.NumPad7 };
        RaiseEvent(number);
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", "7") && !number.Handled,
            "Device test: numpad digits are shown without a platform enum name");
        check(DeviceTypingTest.MatchingDisplayKeys(_device, "NumPad7").All(_typingKeys.Contains) && !_count.IsVisible && _session is null,
            "Device test: OS hints mark standard legends without claiming physical results: " + _device.Id);
        var symbol = new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = "#" }; RaiseEvent(symbol);
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", "#") && symbol.Handled,
            "Device test: actual symbol input supplies the readable label directly at window level: " + _device.Id);
        var close = this.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseDeviceTest");
        foreach (var key in new[] { Key.Enter, Key.Space, Key.Tab, Key.Escape, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Back, Key.Delete })
        {
            var down = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key }; var up = new KeyEventArgs { RoutedEvent = KeyUpEvent, Key = key };
            close.RaiseEvent(down); close.RaiseEvent(up);
            check(down.Handled && up.Handled && !_closing && _lastKey.Text == L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel(key.ToString())),
                "Device test: key down and release from Done route into testing without activating the button: " + key + " down=" + down.Handled + " up=" + up.Handled + " closing=" + _closing + " last=" + _lastKey.Text);
        }
        var composed = new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = "é" }; close.RaiseEvent(composed);
        check(composed.Handled && _lastKey.Text == L.Format("Zuletzt gedrückt: {0}", "é"),
            "Device test: composed locale input follows the window route without a text field");
        _closing = true;
        RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.A });
        RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = "a" });
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", "é"), "Device test: input stops immediately when closing starts");
        _closing = false;
        check(this.GetLogicalDescendants().OfType<Button>().All(button => !button.Focusable),
            "Device test: mouse actions remain available without keyboard focus reaching buttons");
        ShowTypingTest(capabilityKnown: true);
        check(_typingPanel.IsVisible && !_start.IsVisible && !_physicalTest.IsVisible && !_count.IsVisible && _lightControls.All(control => !control.IsEnabled),
            "Device test: unsupported diagnostics retain typing only and never expose inactive physical results or LEDs");
        var root = (Grid)Content!;
        var switchStates = HardwareControlCatalog.HasOsSwitch(_device.Id)
            ? new[] { HardwareControlCatalog.OsSwitchText(_device.Id, null, false), HardwareControlCatalog.OsSwitchText(_device.Id, null, true),
                HardwareControlCatalog.OsSwitchText(_device.Id, new(_device.Id, false, false, false), true),
                HardwareControlCatalog.OsSwitchText(_device.Id, new(_device.Id, true, false, true), true) }
            : new[] { "" };
        foreach (var physical in new[] { false, true })
        foreach (var size in new[] { new Size(1012, 640), new Size(744, 560), new Size(1000, 500) })
        foreach (var switchState in switchStates)
        {
            _osSwitch.Text = switchState;
            _physicalTest.IsVisible = physical; _typingPanel.IsVisible = !physical;
            root.Measure(size); root.Arrange(new Rect(size));
            var footer = root.Children.OfType<Grid>().Single(element => Grid.GetRow(element) == 3);
            check(_keyboard.Bounds.Height > 80 && footer.Bounds.Bottom <= size.Height + 1 && !root.Children.OfType<ScrollViewer>().Any(),
                "Device test: keyboard and controls fit a fixed footer layout: " + _device.Id + " " + size + " physical=" + physical + " diagram=" + _keyboard.Bounds.Height + " footer=" + footer.Bounds.Bottom);
        }
        _osSwitch.Text = HardwareControlCatalog.OsSwitchText(_device.Id, null, false);
        ShowTypingTest(capabilityKnown: true);
    }
    private async Task RunOfflineCloseChecksAsync(Action<bool, string> check)
    {
        var fixture = new DeviceTestWindow(_device.Id, null, preview: true);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture._starting = start.Task; fixture._pollTask = poll.Task; fixture._closing = true;
        bool stopEntered = false, stopFinished = false;
        var closing = fixture.DrainAndStopAsync(async () =>
        {
            stopEntered = true;
            stopStarted.SetResult();
            check(!fixture._lifetime.IsCancellationRequested, "Device test: diagnostic STOP starts before lifetime cancellation");
            await stop.Task; stopFinished = true;
        });
        try
        {
            check(!closing.IsCompleted && !stopEntered && !fixture._lifetime.IsCancellationRequested,
                "Device test: closing waits for actual startup without cancelling the transport");
            start.SetResult(); await Dispatcher.Yield(DispatcherPriority.Background);
            check(!closing.IsCompleted && !stopEntered && !fixture._lifetime.IsCancellationRequested,
                "Device test: closing also waits for an outstanding physical or switch poll");
            poll.SetResult(); await stopStarted.Task;
            check(stopEntered && !stopFinished && !closing.IsCompleted && !fixture._lifetime.IsCancellationRequested,
                "Device test: window cannot close while diagnostic STOP or transport disposal is pending");
            stop.SetResult(); await closing;
            check(stopFinished && fixture._lifetime.IsCancellationRequested,
                "Device test: normal close cancels the lifetime only after STOP and disposal complete");
        }
        finally
        {
            start.TrySetResult(); poll.TrySetResult(); stop.TrySetResult();
            try { await closing; } finally { fixture._lifetime.Dispose(); fixture._closed = true; fixture.Close(); }
        }
        var failed = new DeviceTestWindow(_device.Id, null, preview: true) { _closing = true };
        failed._starting = Task.FromException(new IOException("offline startup failure"));
        bool cleaned = false, rejected = false;
        try { await failed.DrainAndStopAsync(() => { cleaned = !failed._lifetime.IsCancellationRequested; return Task.CompletedTask; }); }
        catch (IOException) { rejected = true; }
        check(rejected && cleaned && failed._lifetime.IsCancellationRequested,
            "Device test: failed startup still runs STOP/disposal before lifetime cancellation");
        failed._lifetime.Dispose(); failed._closed = true; failed.Close();
        _osSwitch.Text = "stale offline switch state";
        await StopAsync();
        check(_osSwitch.Text == HardwareControlCatalog.OsSwitchText(_device.Id, null, false),
            "Device test: stopping clears the last live switch state");
    }
}
