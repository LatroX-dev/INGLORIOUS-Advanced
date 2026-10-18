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
    private readonly HashSet<string> _typingKeys = [];
    private readonly Viewbox _keyboard = new() { Name = "DeviceTestKeyboardView", Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 12, 0, 12) };
    private readonly Button _start = new();
    private readonly StackPanel _lights = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _physical = new() { Name = "DevicePhysicalTest", Visibility = Visibility.Collapsed, IsEnabled = false }, _typing = new() { Name = "DeviceTypingTest", Visibility = Visibility.Collapsed };
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
        Width = Math.Min(1060, SystemParameters.WorkArea.Width); Height = Math.Min(720, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(760, SystemParameters.WorkArea.Width); MinHeight = Math.Min(600, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        var body = new StackPanel { Margin = new(24, 20, 24, 0) };
        body.Children.Add(new TextBlock { Text = _device.Name, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 4) });
        body.Children.Add(new TextBlock { Text = L.T("Die Tasten behalten ihre normale Funktion. Deine Einstellungen werden nicht verändert."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 12), Foreground = (Brush)FindResource("Muted") });
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        _start.Content = L.T("Test starten"); _start.IsEnabled = connection is not null && !preview;
        _start.Click += async (_, _) => { _starting = StartAsync(); await _starting; }; bar.Children.Add(_start);
        _count.Margin = new(16, 0, 0, 0); _count.VerticalAlignment = VerticalAlignment.Center; bar.Children.Add(_count); body.Children.Add(bar);
        var canvas = new Canvas { Name = "DeviceTestKeyboard", Width = _device.PreviewWidth * 42, Height = _device.PreviewHeight * 42 };
        foreach (var key in _device.Keys)
        {
            var label = new TextBlock { Text = key.DisplayLabel, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = key.DisplayLabel.Length > 5 ? 8 : 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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
            else if (key.Label == "Knob")
            {
                var shape = new System.Windows.Shapes.Ellipse { Fill = button.Background, Stroke = (Brush)FindResource("Muted"), StrokeThickness = 1 };
                button.Background = Brushes.Transparent; button.BorderThickness = new(0); button.Padding = new(0);
                cap.Children.Insert(0, shape); _keys[key.Id] = brush => shape.Fill = brush;
            }
            Canvas.SetLeft(button, (key.X - (key.IsIsoEnter ? .25 : 0)) * 42); Canvas.SetTop(button, (key.Y + _device.PreviewTopInset) * 42);
            if (key.LedIndex is { } led) button.Click += async (_, _) => await LedAsync(led, 255, 0, 0);
            canvas.Children.Add(button);
        }
        _keyboard.Child = canvas;
        _physical.Children.Add(new TextBlock { Text = L.T("Drücke jede Taste einzeln und drehe den Knopf in beide Richtungen. Erkannte Tasten werden grün."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        _physical.Children.Add(new TextBlock { Text = L.T("Beleuchtung prüfen"), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 4) });
        _physical.Children.Add(new TextBlock { Text = L.T("Klicke eine Taste an, um nur ihre LED rot einzuschalten. Vergleiche die Position mit der Tastatur."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
        foreach (var entry in new (string Name, byte R, byte G, byte B)[] { ("Alle weiß",255,255,255), ("Alle rot",255,0,0), ("Alle grün",0,255,0), ("Alle blau",0,0,255), ("Alle aus",0,0,0) })
        {
            var button = new Button { Content = L.T(entry.Name), Margin = new(0, 0, 8, 0) };
            button.Click += async (_, _) => await LedAsync(null, entry.R, entry.G, entry.B); _lights.Children.Add(button);
        }
        _lights.IsEnabled = false; _physical.Children.Add(_lights);
        var extras = new WrapPanel { Margin = new(0, 6, 0, 6) };
        foreach (var (name, indices) in new[] { ("Links", _device.LeftSideLedIndices), ("Rechts", _device.RightSideLedIndices) })
        {
            if (indices.Count == 0) continue;
            var group = new StackPanel { Margin = new(0, 0, 12, 0) };
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
        _typing.Children.Add(new TextBlock { Text = L.T("Drücke die Tasten, die du prüfen möchtest."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
        if (_device.Id == "numpad") _typing.Children.Add(new TextBlock { Text = L.T("Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
        _typing.Children.Add(_lastKey);
        _typing.Children.Add(new TextBlock { Text = L.T("Gelb zeigt die empfangene Eingabe. Bei eigenen Belegungen kann die Position abweichen."), Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) });
        _typing.Children.Add(new TextBlock { Text = L.T("Die Eingabe kann auch von anderen angeschlossenen Tastaturen kommen."), Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        PreviewKeyDown += (_, e) =>
        {
            if (_closing) return;
            if (_session is not null) { e.Handled = true; return; }
            if (_typing.Visibility != Visibility.Visible) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            RecordTypingKey(key);
            // Printable keys remain unhandled so the OS can produce localized text input.
            if (DeviceTypingTest.IsTestNavigationKey(key.ToString())) e.Handled = true;
        };
        PreviewKeyUp += (_, e) =>
        {
            if (_closing) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_session is not null || (_typing.Visibility == Visibility.Visible && DeviceTypingTest.IsTestNavigationKey(key.ToString()))) e.Handled = true;
        };
        PreviewTextInput += (_, e) =>
        {
            if (_closing) return;
            if (_session is not null) { e.Handled = true; return; }
            if (_typing.Visibility != Visibility.Visible) return;
            RecordTypingText(e.Text); e.Handled = true;
        };
        var lower = new StackPanel { Margin = new(24, 0, 24, 0) }; lower.Children.Add(_physical); lower.Children.Add(_typing);
        var close = new Button { Name = "CloseDeviceTest", Content = L.T("Fertig"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 90 }; close.Click += (_, _) => Close();
        _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new(0, 0, 16, 0);
        var footer = new Grid { Margin = new(24, 12, 24, 20) };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_status); Grid.SetColumn(close, 1); footer.Children.Add(close);
        var root = new Grid { Name = "DeviceTestSurface", Background = Background, Focusable = true };
        foreach (var height in new[] { GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        root.Children.Add(body); Grid.SetRow(_keyboard, 1); root.Children.Add(_keyboard); Grid.SetRow(lower, 2); root.Children.Add(lower); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _timer.Tick += async (_, _) => await PollAsync();
        if (preview) ShowTypingTest();
        Loaded += (_, _) => { if (_typing.Visibility == Visibility.Visible) root.Focus(); };
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
            _typing.Visibility = Visibility.Collapsed; _typingKeys.Clear();
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
        if (Content is UIElement surface) surface.Focus();
    }
    private void RecordTypingKey(Key key)
    {
        if (_closing || _session is not null || _typing.Visibility != Visibility.Visible) return;
        _lastKey.Text = L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel(key.ToString()));
        foreach (var id in DeviceTypingTest.MatchingDisplayKeys(_device, key.ToString()))
        { _typingKeys.Add(id); _keys[id](new SolidColorBrush(Color.FromRgb(137, 102, 30))); }
    }
    private void RecordTypingText(string text)
    {
        if (_closing || _session is not null || _typing.Visibility != Visibility.Visible || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) return;
        _lastKey.Text = L.Format("Zuletzt gedrückt: {0}", text);
        foreach (var id in DeviceTypingTest.MatchingTextDisplayKeys(_device, text))
        { _typingKeys.Add(id); _keys[id](new SolidColorBrush(Color.FromRgb(137, 102, 30))); }
    }
    internal void RunOfflineChecks(Action<bool, string> check)
    {
        if (_connection is not null) throw new InvalidOperationException("Offline device test required.");
        ShowTypingTest();
        check(_typing.Visibility == Visibility.Visible && _physical.Visibility == Visibility.Collapsed && _count.Text == "" && !_lights.IsEnabled,
            "Device test: local input never claims physical key hits or enables LED diagnostics: " + _device.Id);
        check(_keyboard.Visibility == Visibility.Visible && _keyboard.Child is Canvas { } keyboard && keyboard.Children.Count == _device.Keys.Count && _keyboard.IsHitTestVisible && _session is null,
            "Device test: every model retains its complete keyboard diagram and tooltips without a hardware session: " + _device.Id);
        check(_session is null && !_timer.IsEnabled && ((Grid)Content).Focusable,
            "Device test: the visible test surface accepts input without an entry field or hardware polling: " + _device.Id);
        RecordTypingKey(Key.Home);
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel("Home")),
            "Device test: navigation keys are visible without an entry field: " + _device.Id);
        RecordTypingKey(Key.NumPad7);
        check(DeviceTypingTest.MatchingDisplayKeys(_device, "NumPad7").All(_typingKeys.Contains) && _count.Text == "" && _session is null,
            "Device test: OS hints mark standard legends without claiming physical results: " + _device.Id);
        RecordTypingText("#");
        check(_lastKey.Text == L.Format("Zuletzt gedrückt: {0}", "#"),
            "Device test: actual symbol input supplies the readable label without collecting typed text: " + _device.Id);
        var root = (Grid)Content;
        foreach (var physical in new[] { false, true })
        foreach (var size in new[] { new Size(1012, 640), new Size(744, 560), new Size(1000, 500) })
        {
            _physical.Visibility = physical ? Visibility.Visible : Visibility.Collapsed;
            _typing.Visibility = physical ? Visibility.Collapsed : Visibility.Visible;
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            var footer = root.Children.OfType<Grid>().Single(element => Grid.GetRow(element) == 3);
            var bottom = footer.TransformToAncestor(root).Transform(new Point(0, footer.ActualHeight)).Y;
            check(_keyboard.ActualHeight > 80 && bottom <= size.Height + 1 && !root.Children.OfType<ScrollViewer>().Any(),
                "Device test: keyboard and controls fit a fixed footer layout: " + _device.Id + " " + size + " physical=" + physical + " diagram=" + _keyboard.ActualHeight + " footer=" + bottom);
        }
        ShowTypingTest();
        RecordTypingKey(Key.NumPad7);
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
