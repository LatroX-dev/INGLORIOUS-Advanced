using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed class DeviceTestWindow : Window
{
    private readonly DeviceDefinition _device;
    private readonly OnboardDevice? _connection;
    private readonly TextBlock _status = Label(""), _count = Label("");
    private readonly StackPanel _physicalTest = new() { Name = "DevicePhysicalTest", Spacing = 12, IsVisible = false };
    private readonly StackPanel _typingPanel = new() { Name = "DeviceTypingTest", Spacing = 10, IsVisible = false };
    private readonly TextBox _typing = new() { Name = "DeviceTypingInput", MinHeight = 100, MaxLength = 2048, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _lastKey = Label(L.T("Noch keine Taste gedrückt."));
    private readonly Button _start = new() { Name = "StartDeviceTest", Content = L.T("Test starten") };
    private readonly List<Control> _lightControls = [];
    private readonly Dictionary<string, Action<IBrush>> _keyColors = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly CancellationTokenSource _lifetime = new();
    private DeviceDiagnosticSession? _session;
    private Task _starting = Task.CompletedTask;
    private bool _polling, _closing, _closed;
    private static IBrush ColorBrush(string color) => new SolidColorBrush(Color.Parse(color));

    public DeviceTestWindow(string deviceId, OnboardDevice? connection, bool preview = false)
    {
        _device = DeviceCatalog.Get(deviceId);
        if (connection is not null && connection.DeviceId != deviceId) throw new ArgumentException("Device test model mismatch.", nameof(connection));
        _connection = preview ? null : connection;
        _typingPanel.IsVisible = preview;
        Title = L.T("Gerät testen") + " · " + _device.Name;
        Width = 1060; Height = 800; MinWidth = 760; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ColorBrush("#171B1F"); Foreground = ColorBrush("#E4E7EB");

        var body = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = _device.Name, FontSize = 24, FontWeight = FontWeight.SemiBold });
        body.Children.Add(Label(L.T("Die Tasten behalten ihre normale Funktion. Deine Einstellungen werden nicht verändert."), muted: true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        _start.IsEnabled = _connection is not null;
        _start.Classes.Add("primary");
        _start.Click += async (_, _) => { _starting = StartAsync(); await _starting; };
        _count.Name = "DeviceTestCount"; _count.VerticalAlignment = VerticalAlignment.Center; _count.IsVisible = false;
        actions.Children.Add(_start); actions.Children.Add(_count); body.Children.Add(actions);
        _typingPanel.Children.Add(new TextBlock { Text = L.T("Tipptest"), FontSize = 17, FontWeight = FontWeight.SemiBold });
        _typingPanel.Children.Add(Label(L.T("Klicke in das Feld und drücke die Tasten, die du prüfen möchtest.")));
        if (_device.Id == "numpad") _typingPanel.Children.Add(Label(L.T("Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0."), muted: true));
        _typingPanel.Children.Add(_typing);
        AutomationProperties.SetName(_typing, L.T("Hier Tasten testen"));
        _typing.AddHandler(KeyDownEvent, RecordTypingKey, RoutingStrategies.Tunnel);
        _lastKey.Name = "DeviceTypingLastKey";
        _typingPanel.Children.Add(_lastKey);
        _typingPanel.Children.Add(Label(L.T("Die Eingabe kann auch von anderen angeschlossenen Tastaturen kommen."), muted: true));
        body.Children.Add(_typingPanel);
        _physicalTest.Children.Add(Label(L.T("Drücke jede Taste einzeln und drehe den Knopf in beide Richtungen. Erkannte Tasten werden grün.")));
        _physicalTest.Children.Add(new Viewbox
        {
            Child = CreateKeyboard(), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = 310, Margin = new Thickness(0, 6)
        });
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
        body.Children.Add(_physicalTest);
        SetLightingEnabled(false);
        _status.Name = "DeviceTestStatus";
        var close = new Button { Name = "CloseDeviceTest", Content = L.T("Fertig"), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16, Margin = new Thickness(24, 8, 24, 20) };
        footer.Children.Add(_status); Grid.SetColumn(close, 1); footer.Children.Add(close);
        var layout = new Grid { RowDefinitions = new("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(footer, 1); layout.Children.Add(footer); Content = layout;
        _timer.Tick += async (_, _) => await PollAsync();
        AddHandler(KeyDownEvent, IgnoreTestKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, IgnoreTestKey, RoutingStrategies.Tunnel);
        Closing += OnClosing;
    }

    private Canvas CreateKeyboard()
    {
        const double unit = 42;
        var canvas = new Canvas { Name = "DeviceTestKeyboard", Width = _device.PreviewWidth * unit, Height = _device.PreviewHeight * unit };
        foreach (var key in _device.Keys)
        {
            var label = new TextBlock { Text = key.DisplayLabel, FontSize = key.DisplayLabel.Length > 5 ? 8 : 10, Foreground = ColorBrush("#F2F5F7"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
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
            { button.Click += async (_, _) => await LedAsync(led, 255, 0, 0); _lightControls.Add(button); }
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
    private void SetLightingEnabled(bool enabled) { foreach (var control in _lightControls) control.IsEnabled = enabled; }
    private void IgnoreTestKey(object? sender, KeyEventArgs e) { if (_session is not null) e.Handled = true; }
    private void RecordTypingKey(object? sender, KeyEventArgs e)
    {
        if (_session is not null || !_typingPanel.IsVisible) return;
        _lastKey.Text = L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel(e.Key.ToString()));
    }
    private void ShowTypingTest(bool capabilityKnown)
    {
        _physicalTest.IsVisible = false; _count.IsVisible = false; SetLightingEnabled(false);
        _typingPanel.IsVisible = true; _start.IsVisible = !capabilityKnown;
        _status.Text = "";
    }

    private async Task StartAsync()
    {
        if (_connection is null || _session is not null || _closing) return;
        _start.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var session = await DeviceDiagnosticSession.TryStartAsync(_connection, timeout.Token);
            if (_closing) { if (session is not null) await session.DisposeAsync(); return; }
            if (session is null) { ShowTypingTest(capabilityKnown: true); _typing.Focus(); return; }
            _session = session;
            _physicalTest.IsVisible = _count.IsVisible = true; _typingPanel.IsVisible = false;
            foreach (var color in _keyColors.Values) color(ColorBrush("#252A2F"));
            _count.Text = L.Format("{0} von {1} Eingaben erkannt", 0, _device.Keys.Count);
            _status.Text = L.T("Der Test läuft. Drücke die Tasten einzeln."); SetLightingEnabled(true); _timer.Start();
        }
        catch (Exception error) { if (!_closing) { _status.Text = Failure(error); _start.IsEnabled = true; } }
    }
    private async Task PollAsync()
    {
        if (_polling || _session is null || _closing) return;
        _polling = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var state = await _session.PollAsync(timeout.Token);
            var seen = 0;
            foreach (var key in _device.Keys)
            {
                var done = key.Id == "encoder:cw" ? state.Clockwise != 0 : key.Id == "encoder:ccw" ? state.Counterclockwise != 0 : state.WasPressed(key.Id);
                if (done) seen++;
                _keyColors[key.Id](ColorBrush(done ? "#23704A" : "#252A2F"));
            }
            _count.Text = L.Format("{0} von {1} Eingaben erkannt", seen, _device.Keys.Count);
        }
        catch (Exception error)
        {
            _timer.Stop(); await StopAsync();
            if (!_closing) { _status.Text = Failure(error); _start.IsEnabled = true; }
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
        if (session is not null)
        {
            try { await session.DisposeAsync(); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Device test cleanup failed: {0}", error); if (!_closing) _status.Text = Failure(error); }
        }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true; if (_closing) return;
        _closing = true; _timer.Stop(); _lifetime.Cancel();
        try { await _starting; await StopAsync(); }
        finally { _closed = true; _lifetime.Dispose(); Dispatcher.UIThread.Post(Close); }
    }
    private static string Failure(Exception error) => error is NotSupportedException ? error.Message : error is OnboardCommandException { Status: 3 }
        ? L.T("Lasse alle Tasten los und starte den Test erneut.") : L.T("Der Gerätetest wurde unterbrochen. Prüfe die USB-Verbindung und starte ihn erneut.");

    internal void RunOfflineChecks(Action<bool, string> check)
    {
        if (_connection is not null) throw new InvalidOperationException("Device test checks require an offline preview.");
        check(_typingPanel.IsVisible && _typing.IsEnabled && !_physicalTest.IsVisible && !_count.IsVisible,
            "Device test: typing is available before optional physical diagnostics without claiming a device key count");
        check(!_start.IsEnabled && _lightControls.All(control => !control.IsEnabled),
            "Device test: offline preview cannot start hardware diagnostics or change LEDs");
        _typing.Text = "1234567890";
        var navigation = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Left };
        RecordTypingKey(_typing, navigation);
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel("Left")) && !navigation.Handled && _typing.Text == "1234567890",
            "Device test: navigation keys remain visible without suppressing or replacing the real text input");
        var number = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.NumPad7 };
        RecordTypingKey(_typing, number);
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", "7") && !number.Handled,
            "Device test: numpad digits are shown without a platform enum name");
        ShowTypingTest(capabilityKnown: true);
        check(_typingPanel.IsVisible && !_start.IsVisible && !_physicalTest.IsVisible && !_count.IsVisible && _lightControls.All(control => !control.IsEnabled),
            "Device test: unsupported diagnostics retain typing only and never expose inactive physical results or LEDs");
        _lifetime.Dispose();
    }
}
