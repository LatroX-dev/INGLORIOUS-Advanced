using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InGlorious.Core;

internal sealed record KnobCycleSelection(int Mask, bool MacShortcuts, int UndoLayout, IReadOnlyList<int>? Order = null);

internal sealed class KnobOptionsWindow : Window
{
    private readonly List<int> _active;
    private readonly List<int> _inactive;
    private readonly List<int> _originalOrder;
    private readonly StackPanel _rows = new();
    private readonly ComboBox _available = new() { DisplayMemberPath = "Label", MinWidth = 240 };
    private readonly Grid _addRow = new() { Margin = new(0, 10, 0, 0) };
    private readonly StackPanel _systemPanel = new();
    private readonly StackPanel _layoutPanel = new();
    private int? _dragBit;
    private Point _dragStart;
    private bool _dragging;
    private int _dropSlot = -1;
    internal IReadOnlyList<int> ActiveBits => _active;
    internal IReadOnlyList<int> ChosenOrder => _active.SequenceEqual(_originalOrder.Where(_active.Contains))
        ? _originalOrder.ToArray() : [.. _active, .. _inactive];
    internal ComboBox SystemChoice { get; }
    internal ComboBox LayoutChoice { get; }
    internal Button ConfirmButton { get; }
    public KnobCycleSelection? Selection { get; private set; }

    public KnobOptionsWindow(LayerProfile layer)
    {
        _originalOrder = [.. layer.KnobCycleOrder];
        _active = layer.KnobCycleOrder.Where(bit => (layer.KnobCycleMask & bit) != 0).ToList();
        _inactive = layer.KnobCycleOrder.Except(_active).ToList();
        Title = L.T("Drehregler-Funktionen"); Width = 580; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 48);
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        FontFamily = new("Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        var host = new DockPanel();
        var root = new StackPanel(); Content = new Border { Background = Background, Padding = new(24), Child = host };
        root.Children.Add(new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(Help("Zum Verschieben ziehen", new(0, 16, 0, 8)));
        root.Children.Add(_rows);
        _rows.PreviewMouseMove += DragMove;
        _rows.PreviewMouseLeftButtonUp += DragEnd;
        _rows.LostMouseCapture += (_, _) => CancelDrag();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _dragBit is not null) { CancelDrag(); e.Handled = true; } };
        Deactivated += (_, _) => CancelDrag();
        _addRow.ColumnDefinitions.Add(new()); _addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _addRow.Children.Add(_available);
        var add = new Button { Content = L.T("Hinzufügen"), Margin = new(8, 0, 0, 0) }; Grid.SetColumn(add, 1); _addRow.Children.Add(add);
        System.Windows.Automation.AutomationProperties.SetName(_available, L.T("Funktion hinzufügen"));
        add.Click += (_, _) => { if (_available.SelectedItem is KnobCycleOption option) AddOption(option.Bit); };
        root.Children.Add(_addRow);
        _systemPanel.Children.Add(Help("Tastenkürzel für", new(0, 18, 0, 6)));
        SystemChoice = new ComboBox { ItemsSource = new[] { "Windows / Linux", "macOS" }, SelectedIndex = layer.KnobCycleMacZoom ? 1 : 0 };
        _systemPanel.Children.Add(SystemChoice); root.Children.Add(_systemPanel);
        _layoutPanel.Children.Add(Help("Tastaturlayout für Rückgängig", new(0, 12, 0, 6)));
        LayoutChoice = new ComboBox { ItemsSource = new[] { "QWERTY", "QWERTZ", "AZERTY" }, SelectedIndex = layer.KnobUndoLayout };
        _layoutPanel.Children.Add(LayoutChoice); root.Children.Add(_layoutPanel);
        root.Children.Add(Help("Zoom, Tabs und Rückgängig hängen von der geöffneten Anwendung ab.", new(0, 16, 0, 0)));
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); host.Children.Add(footer);
        host.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        footer.Children.Add(new Button { Content = L.T("Abbrechen"), IsCancel = true, MinWidth = 100, Margin = new(0, 0, 10, 0) });
        ConfirmButton = new Button { Content = L.T("Übernehmen"), IsDefault = true, MinWidth = 112, Style = (Style)FindResource("PrimaryButton") };
        footer.Children.Add(ConfirmButton);
        ConfirmButton.Click += (_, _) =>
        {
            if (_active.Count == 0 || SystemChoice.SelectedIndex is < 0 or > 1 || LayoutChoice.SelectedIndex is < 0 or > 2) return;
            Selection = new(_active.Sum(), SystemChoice.SelectedIndex == 1, LayoutChoice.SelectedIndex, ChosenOrder); DialogResult = true;
        };
        Refresh();
    }
    internal void AddOption(int bit) { if (_inactive.Remove(bit)) { _active.Add(bit); Refresh(); } }
    internal void RemoveOption(int bit) { if (_active.Remove(bit)) { _inactive.Add(bit); Refresh(); } }
    internal void MoveOption(int bit, int direction)
    {
        var index = _active.IndexOf(bit); var next = index + direction;
        if (direction is not (-1 or 1) || index < 0 || next < 0 || next >= _active.Count) return;
        (_active[index], _active[next]) = (_active[next], _active[index]); Refresh();
    }
    internal bool DropOption(int bit, int slot)
    {
        var index = _active.IndexOf(bit);
        if (index < 0 || slot < 0 || slot > _active.Count) return false;
        var target = slot > index ? slot - 1 : slot;
        if (index == target) return false;
        _active.RemoveAt(index); _active.Insert(target, bit); Refresh(); return true;
    }
    private void StartDrag(int bit, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragBit = bit; _dragStart = e.GetPosition(_rows); _dragging = false; _dropSlot = -1;
        _rows.CaptureMouse(); e.Handled = true;
    }
    private void DragMove(object sender, MouseEventArgs e)
    {
        if (_dragBit is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }
        var point = e.GetPosition(_rows);
        if (!_dragging && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        _dragging = true; _rows.Cursor = Cursors.SizeNS;
        _dropSlot = point.X < 0 || point.X > _rows.ActualWidth || point.Y < 0 || point.Y > _rows.ActualHeight ? -1 : _active.Count;
        var rows = _rows.Children.OfType<Border>().ToArray();
        if (_dropSlot >= 0)
            for (var index = 0; index < rows.Length; index++)
                if (point.Y < rows[index].TranslatePoint(new Point(0, rows[index].ActualHeight / 2), _rows).Y)
                { _dropSlot = index; break; }
        foreach (var row in rows) row.BorderThickness = new(0, 2, 0, 2);
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index].BorderBrush = Brushes.Transparent;
            if (_dropSlot == index || _dropSlot == rows.Length && index == rows.Length - 1)
            { rows[index].BorderBrush = (Brush)FindResource("Accent"); rows[index].BorderThickness = _dropSlot == rows.Length ? new(0, 0, 0, 4) : new(0, 4, 0, 0); }
        }
        e.Handled = true;
    }
    private void DragEnd(object sender, MouseButtonEventArgs e)
    {
        var bit = _dragBit; var slot = _dropSlot; var moved = _dragging;
        CancelDrag();
        if (moved && bit.HasValue) { DropOption(bit.Value, slot); e.Handled = true; }
    }
    private void CancelDrag()
    {
        _dragBit = null; _dragging = false; _dropSlot = -1; _rows.Cursor = null;
        if (_rows.IsMouseCaptured) _rows.ReleaseMouseCapture();
        foreach (var row in _rows.Children.OfType<Border>()) { row.BorderBrush = Brushes.Transparent; row.BorderThickness = new(0, 2, 0, 2); }
    }
    private void Refresh()
    {
        _rows.Children.Clear();
        for (var index = 0; index < _active.Count; index++)
        {
            var bit = _active[index]; var option = KnobPresets.CycleOptions.Single(item => item.Bit == bit);
            var row = new Grid { Margin = new(0, 0, 0, 4), MinHeight = 36 };
            row.ColumnDefinitions.Add(new());
            for (var column = 0; column < 3; column++) row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var handle = new TextBlock { Text = "⠿  " + (index + 1) + "  " + option.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 10, 0), Cursor = Cursors.SizeNS, ToolTip = L.T("Zum Verschieben ziehen") };
            handle.PreviewMouseLeftButtonDown += (_, e) => StartDrag(bit, e);
            row.Children.Add(handle);
            foreach (var (column, text, label, enabled, action) in new (int, string, string, bool, Action)[]
            {
                (1, "↑", "Nach oben", index > 0, () => MoveOption(bit, -1)),
                (2, "↓", "Nach unten", index + 1 < _active.Count, () => MoveOption(bit, 1)),
                (3, "×", "Entfernen", true, () => RemoveOption(bit))
            })
            {
                var button = new Button { Content = new TextBlock { Text = text, FontSize = 16 }, ToolTip = L.T(label), IsEnabled = enabled, Width = 32, Padding = new(0), Margin = new(4, 0, 0, 0), Style = (Style)FindResource("QuietButton") };
                System.Windows.Automation.AutomationProperties.SetName(button, L.T(label) + ": " + option.Label);
                button.Click += (_, _) => action(); Grid.SetColumn(button, column); row.Children.Add(button);
            }
            _rows.Children.Add(new Border { Child = row, BorderBrush = Brushes.Transparent, BorderThickness = new(0, 2, 0, 2) });
        }
        _available.ItemsSource = _inactive.Select(bit => KnobPresets.CycleOptions.Single(option => option.Bit == bit)).ToArray();
        _available.SelectedIndex = _inactive.Count == 0 ? -1 : 0;
        _addRow.Visibility = _inactive.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _systemPanel.Visibility = _active.Any(bit => bit is 16 or 64 or 128) ? Visibility.Visible : Visibility.Collapsed;
        _layoutPanel.Visibility = _active.Contains(128) ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = _active.Count > 0;
    }
    private TextBlock Help(string source, Thickness margin) => new()
    { Text = L.T(source), Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = margin, FontSize = 12 };
}
