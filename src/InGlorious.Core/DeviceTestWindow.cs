using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace InGlorious.Core;

public sealed class DeviceTestWindow : Window
{
    private readonly DeviceDefinition _device;
    private readonly OnboardDevice? _connection;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap }, _count = new();
    private readonly Dictionary<string, Action<Brush>> _keys = [];
    private readonly Button _start = new();
    private readonly StackPanel _lights = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _physical = new() { Name = "DevicePhysicalTest", Visibility = Visibility.Collapsed, IsEnabled = false }, _typing = new() { Name = "DeviceTypingTest", Visibility = Visibility.Collapsed };
    private readonly TextBox _input = new() { Name = "DeviceTypingInput", MinHeight = 120, MaxLength = 2000, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _lastKey = new() { Name = "DeviceTypingLastKey", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private DeviceDiagnosticSession? _session;
    private Task _starting = Task.CompletedTask;
    private bool _polling, _closing, _closed;
    private readonly CancellationTokenSource _lifetime = new();

    public DeviceTestWindow(string deviceId, OnboardDevice? connection, bool preview = false)
    {
        _device = DeviceCatalog.Get(deviceId);
        if (connection is not null && connection.DeviceId != deviceId) throw new ArgumentException("Device test model mismatch.", nameof(connection));
        _connection = preview ? null : connection;
        Title = L.T("Gerät testen") + " · " + _device.Name;
        Width = 1060; Height = 730; MinWidth = 760; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        var body = new StackPanel { Margin = new(24) };
        body.Children.Add(new TextBlock { Text = _device.Name, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) });
        _physical.Children.Add(new TextBlock { Text = L.T("Drücke jede Taste einzeln und drehe den Knopf in beide Richtungen. Erkannte Tasten werden grün."), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = L.T("Die Tasten behalten ihre normale Funktion. Deine Einstellungen werden nicht verändert."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 12), Foreground = (Brush)FindResource("Muted") });
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        _start.Content = L.T("Test starten"); _start.IsEnabled = connection is not null && !preview;
        _start.Click += async (_, _) => { _starting = StartAsync(); await _starting; }; bar.Children.Add(_start);
        _count.Margin = new(16, 0, 0, 0); _count.VerticalAlignment = VerticalAlignment.Center; bar.Children.Add(_count); body.Children.Add(bar);
        body.Children.Add(_physical);
        var canvas = new Canvas { Width = _device.PreviewWidth * 42, Height = _device.PreviewHeight * 42, Margin = new(0, 18, 0, 18) };
        foreach (var key in _device.Keys)
        {
            var label = new TextBlock { Text = key.DisplayLabel, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cap = new Grid(); cap.Children.Add(label);
            var button = new Button { Content = cap, Width = Math.Max(18, (key.W + (key.IsIsoEnter ? .25 : 0)) * 42 - 4), Height = Math.Max(18, key.H * 42 - 4), Padding = new(1), FontSize = 10, Focusable = false, ToolTip = key.AccessibleLabel, Background = new SolidColorBrush(Color.FromRgb(36, 41, 46)), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            _keys.Add(key.Id, brush => button.Background = brush);
            if (key.IsIsoEnter)
            {
                var width = button.Width; var height = button.Height;
                var outline = Geometry.Parse(FormattableString.Invariant($"M 1,1 L {width-1},1 {width-1},{height-1} 11.5,{height-1} 11.5,37 1,37 Z"));
                var shape = new System.Windows.Shapes.Path { Data = outline, Fill = button.Background, Stroke = (Brush)FindResource("Muted"), StrokeThickness = 1 };
                button.Clip = outline; button.Background = Brushes.Transparent; button.BorderThickness = new(0); button.Padding = new(0);
                cap.Children.Insert(0, shape); label.Margin = new(10.5, 0, 0, 0); _keys[key.Id] = brush => shape.Fill = brush;
            }
            Canvas.SetLeft(button, (key.X - (key.IsIsoEnter ? .25 : 0)) * 42); Canvas.SetTop(button, (key.Y + _device.PreviewTopInset) * 42);
            if (key.LedIndex is { } led) button.Click += async (_, _) => await LedAsync(led, 255, 0, 0);
            canvas.Children.Add(button);
        }
        _physical.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, MaxHeight = 310, HorizontalAlignment = HorizontalAlignment.Left });
        _physical.Children.Add(new TextBlock { Text = L.T("Beleuchtung prüfen"), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
        _physical.Children.Add(new TextBlock { Text = L.T("Klicke eine Taste an, um nur ihre LED rot einzuschalten. Vergleiche die Position mit der Tastatur."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        foreach (var entry in new (string Name, byte R, byte G, byte B)[] { ("Alle weiß",255,255,255), ("Alle rot",255,0,0), ("Alle grün",0,255,0), ("Alle blau",0,0,255), ("Alle aus",0,0,0) })
        {
            var button = new Button { Content = L.T(entry.Name), Margin = new(0, 0, 8, 0) };
            button.Click += async (_, _) => await LedAsync(null, entry.R, entry.G, entry.B); _lights.Children.Add(button);
        }
        _lights.IsEnabled = false; _physical.Children.Add(_lights);
        var extras = new WrapPanel { Margin = new(0, 10, 0, 10) };
        foreach (var (name, indices) in new[] { ("Links", _device.LeftSideLedIndices), ("Rechts", _device.RightSideLedIndices) })
        {
            if (indices.Count == 0) continue;
            var group = new StackPanel { Margin = new(0, 0, 20, 0) };
            group.Children.Add(new TextBlock { Text = L.T(name), Margin = new(0, 0, 0, 5) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (led, index) in indices.Select((led, index) => (led, index)))
            {
                var button = new Button { Content = (index + 1).ToString(), MinWidth = 27, Padding = new(3), Margin = new(0, 0, 3, 0), FontSize = 11, Focusable = false, ToolTip = L.Format(name == "Links" ? "Links {0}" : "Rechts {0}", index + 1) };
                button.Click += async (_, _) => await LedAsync(led, 255, 0, 0); buttons.Children.Add(button);
            }
            group.Children.Add(buttons); extras.Children.Add(group);
        }
        foreach (var led in _device.NonKeyLedIndices.Except(_device.LeftSideLedIndices).Except(_device.RightSideLedIndices))
        { var logo = new Button { Content = L.T("Logo"), VerticalAlignment = VerticalAlignment.Bottom }; logo.Click += async (_, _) => await LedAsync(led, 255, 0, 0); extras.Children.Add(logo); }
        _physical.Children.Add(extras);
        var restore = new Button { Content = L.T("Eigene Beleuchtung anzeigen"), HorizontalAlignment = HorizontalAlignment.Left };
        restore.Click += async (_, _) => await ChangeLightingAsync((session, token) => session.RestoreLightingAsync(token)); _physical.Children.Add(restore);
        _typing.Margin = new(0, 18, 0, 0);
        _typing.Children.Add(new TextBlock { Text = L.T("Tipptest"), FontSize = 17, FontWeight = FontWeights.SemiBold });
        _typing.Children.Add(new TextBlock { Text = L.T("Klicke in das Feld und drücke die Tasten, die du prüfen möchtest."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 10) });
        if (_device.Id == "numpad") _typing.Children.Add(new TextBlock { Text = L.T("Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        System.Windows.Automation.AutomationProperties.SetName(_input, L.T("Hier Tasten testen"));
        _typing.Children.Add(_input); _typing.Children.Add(_lastKey);
        _typing.Children.Add(new TextBlock { Text = L.T("Die Eingabe kann auch von anderen angeschlossenen Tastaturen kommen."), Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) });
        body.Children.Add(_typing);
        _input.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            RecordTypingKey(key);
        };
        _status.Margin = new(0, 12, 0, 10); body.Children.Add(_status);
        var close = new Button { Content = L.T("Fertig"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(24, 12, 24, 20) }; close.Click += (_, _) => Close();
        var root = new DockPanel { Background = Background }; DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        root.Children.Add(new ScrollViewer { Content = body, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
        _timer.Tick += async (_, _) => await PollAsync();
        if (preview) _typing.Visibility = Visibility.Visible;
        PreviewKeyDown += (_, e) => { if (_session is not null) e.Handled = true; };
        Closing += async (_, e) =>
        {
            if (_closed) return; e.Cancel = true; if (_closing) return;
            _closing = true; _timer.Stop(); _lifetime.Cancel();
            try { await _starting; await StopAsync(); }
            finally { _closed = true; _lifetime.Dispose(); _ = Dispatcher.BeginInvoke(Close); }
        };
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
            if (session is null) { ShowTypingTest(); return; }
            _session = session;
            _physical.Visibility = Visibility.Visible;
            _physical.IsEnabled = true;
            foreach (var color in _keys.Values) color(new SolidColorBrush(Color.FromRgb(36, 41, 46)));
            _count.Text = L.Format("{0} von {1} Eingaben erkannt", 0, _device.Keys.Count);
            _status.Text = L.T("Der Test läuft. Drücke die Tasten einzeln."); _lights.IsEnabled = true; _timer.Start();
        }
        catch (Exception e) { if (!_closing) { _status.Text = Failure(e); _start.IsEnabled = true; } }
    }
    private void ShowTypingTest()
    {
        _physical.Visibility = Visibility.Collapsed;
        _typing.Visibility = Visibility.Visible;
        _start.Visibility = Visibility.Collapsed;
        _count.Text = ""; _status.Text = "";
        _lastKey.Text = L.T("Noch keine Taste gedrückt.");
        _input.Focus();
    }
    private void RecordTypingKey(Key key) =>
        _lastKey.Text = L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel(key.ToString()));
    internal void RunOfflineChecks(Action<bool, string> check)
    {
        if (_connection is not null) throw new InvalidOperationException("Offline device test required.");
        ShowTypingTest();
        check(_typing.Visibility == Visibility.Visible && _physical.Visibility == Visibility.Collapsed && _count.Text == "" && !_lights.IsEnabled,
            "Device test: local input never claims physical key hits or enables LED diagnostics: " + _device.Id);
        _input.Text = "1234567890";
        check(_input.Text == "1234567890" && _session is null && !_timer.IsEnabled,
            "Device test: input works without a diagnostic session or hardware polling: " + _device.Id);
        RecordTypingKey(Key.Home);
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel("Home")) && _input.Text == "1234567890",
            "Device test: navigation keys are visible alongside typed input: " + _device.Id);
    }
    private async Task PollAsync()
    {
        if (_polling || _session is null || _closing) return; _polling = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var state = await _session.PollAsync(timeout.Token);
            int seen = 0, total = 0;
            foreach (var key in _device.Keys)
            {
                bool done = key.Id == "encoder:cw" ? state.Clockwise != 0 : key.Id == "encoder:ccw" ? state.Counterclockwise != 0 : state.WasPressed(key.Id);
                total++; if (done) seen++;
                _keys[key.Id](new SolidColorBrush(done ? Color.FromRgb(35, 112, 74) : Color.FromRgb(36, 41, 46)));
            }
            _count.Text = L.Format("{0} von {1} Eingaben erkannt", seen, total);
        }
        catch (Exception e) { if (!_closing) _status.Text = Failure(e); _timer.Stop(); await StopAsync(); _start.IsEnabled = !_closing; }
        finally { _polling = false; }
    }
    private Task LedAsync(int? led, byte r, byte g, byte b) => ChangeLightingAsync((session, token) => session.SetLedAsync(led, r, g, b, token));
    private async Task ChangeLightingAsync(Func<DeviceDiagnosticSession, CancellationToken, Task> change)
    {
        var session = _session; if (session is null || _closing) return;
        try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3)); await change(session, timeout.Token); }
        catch (Exception e) { if (!_closing) _status.Text = Failure(e); }
    }
    private async Task StopAsync()
    {
        var session = _session; _session = null; _lights.IsEnabled = false; _physical.IsEnabled = false;
        if (session is null) return;
        try { await session.DisposeAsync(); }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Device test cleanup failed: {0}", error); if (!_closing) _status.Text = Failure(error); }
    }
    private static string Failure(Exception e) => e is NotSupportedException ? e.Message : e is OnboardCommandException { Status: 3 }
        ? L.T("Lasse alle Tasten los und starte den Test erneut.") : L.T("Der Gerätetest wurde unterbrochen. Prüfe die USB-Verbindung und starte ihn erneut.");
}
