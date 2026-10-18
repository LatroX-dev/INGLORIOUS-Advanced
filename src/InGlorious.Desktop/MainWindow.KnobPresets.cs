using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private sealed record KnobCycleSelection(int Mask, bool MacShortcuts, int UndoLayout, IReadOnlyList<int>? Order = null);
    private EditorState? _knobPressState;
    private int _knobPressLayer = -1, _knobPressTargetBit = 1;
    private string? _knobPressPresetId;

    private static IReadOnlyList<int> CycleOrderForSelection(IReadOnlyList<KnobCycleOption> selected, LayerProfile original)
    {
        var active = selected.Select(option => option.Bit).ToArray();
        var previous = original.KnobCycleOrder.Where(bit => (original.KnobCycleMask & bit) != 0);
        return active.SequenceEqual(previous) ? original.KnobCycleOrder.ToArray()
            : active.Concat(original.KnobCycleOrder.Where(bit => !active.Contains(bit))).ToArray();
    }

    private static bool MoveKnobCycleOption(List<KnobCycleOption> selected, int bit, int target)
    {
        var source = selected.FindIndex(option => option.Bit == bit);
        if (source < 0 || target < 0 || target >= selected.Count || source == target) return false;
        var option = selected[source]; selected.RemoveAt(source); selected.Insert(target, option); return true;
    }

    private void BuildKnobModeSelector(bool manual)
    {
        var presets = AsyncButton("Vorlagen", () => SetKnobModeAsync(false)); presets.Name = "KnobPresetsMode";
        var custom = AsyncButton("Manuell", () => SetKnobModeAsync(true)); custom.Name = "KnobManualMode";
        var modes = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 6 };
        foreach (var button in new[] { presets, custom })
        { button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center; }
        var active = manual ? custom : presets; active.BorderBrush = Brush("#B9535D"); active.Background = Brush("#3A252B");
        modes.Children.Add(presets); Grid.SetColumn(custom, 1); modes.Children.Add(custom); _editor.Children.Add(modes);
    }

    private async Task<bool> SetKnobModeAsync(bool manual, string? target = null)
    {
        var state = _state; var layer = state.LayerIndex;
        if (!IsKnobSelection() || !await GuardNavigation() || !ReferenceEquals(state, _state) || layer != state.LayerIndex || !IsKnobSelection()) return false;
        _knobModeState = state; _knobModeLayer = layer; _knobManual = manual;
        if (target is not null) state.SelectKey(target, false);
        Build(); return true;
    }

    private void BuildKnobTargetSelector()
    {
        var press = _state.Device.Keys.Single(key => key.Label == "Knob").Id;
        var targets = new[] { (Id: "encoder:ccw", Label: "Links", Tip: "Nach links drehen", Icon: "↶", Name: "Left"),
            (Id: "encoder:cw", Label: "Rechts", Tip: "Nach rechts drehen", Icon: "↷", Name: "Right"),
            (Id: press, Label: "Drücken", Tip: "Drücken", Icon: "", Name: "Press") };
        var row = new Grid { Name = "KnobManualTargets", ColumnDefinitions = new("*,*,*"), ColumnSpacing = 6 };
        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            var button = AsyncButton(target.Label, () => SetKnobModeAsync(true, target.Id)); button.Name = "KnobManualTarget" + target.Name;
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center; button.Padding = new Thickness(5, 6);
            if (target.Icon.Length > 0) button.Content = Row(Text(target.Icon, size: 20), Text(L.T(target.Label), size: 12));
            ToolTip.SetTip(button, L.T(target.Tip)); Avalonia.Automation.AutomationProperties.SetName(button, L.T(target.Tip));
            if (_state.SelectedKeys.Contains(target.Id)) { button.BorderBrush = Brush("#B9535D"); button.Background = Brush("#3A252B"); }
            Grid.SetColumn(button, index); row.Children.Add(button);
        }
        _editor.Children.Add(row);
    }

    private void BuildKnobShortPress()
    {
        var press = _state.Device.Keys.Single(key => key.Label == "Knob").Id;
        var title = Text(L.T("Kurzes Drücken"), true, 11);
        var state = _state; var layer = state.LayerIndex;
        var presetId = KnobPresets.Find(state.Layer)?.Id;
        if (!ReferenceEquals(_knobPressState, state) || _knobPressLayer != layer || _knobPressPresetId != presetId)
        {
            _knobPressState = state; _knobPressLayer = layer; _knobPressPresetId = presetId;
            var bit = KnobPresets.BitForPreset(presetId);
            _knobPressTargetBit = bit != 0 ? bit : 1;
        }
        var byFunction = state.Layer.KnobPressByFunction;
        var mode = Choice(new[] { L.T("Für alle Funktionen gleich"), L.T("Je Funktion") }, byFunction ? 1 : 0); mode.Name = "KnobShortPressMode";
        Avalonia.Automation.AutomationProperties.SetName(mode, L.T("Kurzes Drücken"));
        var restoringMode = false;
        mode.SelectionChanged += async (_, _) =>
        {
            if (_building || restoringMode || !ReferenceEquals(state, _state) || layer != state.LayerIndex) return;
            try { if (await SetKnobShortPressModeAsync(mode.SelectedIndex == 1)) return; }
            catch (Exception ex) { Status(ex.Message); }
            restoringMode = true; mode.SelectedIndex = state.Project.Layers[layer].KnobPressByFunction ? 1 : 0; restoringMode = false;
        };
        var content = Column(title, mode);
        if (byFunction)
        {
            var target = new ComboBox { Name = "KnobShortPressFunction", ItemsSource = KnobPresets.CycleOptions,
                DisplayMemberBinding = new Avalonia.Data.Binding("Label"), SelectedItem = KnobPresets.CycleOptions.Single(option => option.Bit == _knobPressTargetBit),
                HorizontalAlignment = HorizontalAlignment.Stretch };
            Avalonia.Automation.AutomationProperties.SetName(target, L.T("Funktion"));
            var restoringTarget = false;
            target.SelectionChanged += async (_, _) =>
            {
                if (_building || restoringTarget || !ReferenceEquals(state, _state) || layer != state.LayerIndex) return;
                if (target.SelectedItem is KnobCycleOption option && await SetKnobShortPressTargetAsync(option.Bit)) return;
                restoringTarget = true; target.SelectedItem = KnobPresets.CycleOptions.Single(option => option.Bit == _knobPressTargetBit); restoringTarget = false;
            };
            content.Children.Add(target);
        }
        var current = byFunction ? KnobPresets.GetPressBinding(state.Layer, _knobPressTargetBit) : state.Layer.Keys[press].Binding;
        var rows = KnobShortPressBindings().Append(current).Distinct().Select(code => new BindingRow(code,
            byFunction && code == "KC_TRNS" ? L.T("Wie allgemeiner kurzer Druck") : DisplayBinding(code))).ToArray();
        var choice = new ComboBox { Name = "KnobShortPressChoice", ItemsSource = rows, SelectedItem = rows.Single(row => row.Code == current),
            HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 280 };
        Avalonia.Automation.AutomationProperties.SetName(choice, L.T("Kurzes Drücken"));
        var restoring = false;
        choice.SelectionChanged += async (_, _) =>
        {
            if (_building || restoring || !ReferenceEquals(state, _state) || layer != state.LayerIndex || byFunction != state.Layer.KnobPressByFunction) return;
            try { if (choice.SelectedItem is BindingRow row && await SetKnobShortPressAsync(row.Code)) return; }
            catch (Exception ex) { Status(ex.Message); }
            var actual = byFunction ? KnobPresets.GetPressBinding(state.Project.Layers[layer], _knobPressTargetBit) : state.Project.Layers[layer].Keys[press].Binding;
            restoring = true; choice.SelectedItem = rows.FirstOrDefault(row => row.Code == actual); restoring = false;
        };
        content.Children.Add(choice);
        _editor.Children.Add(new Border { BorderBrush = Brush("#353A40"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(0, 14, 0, 0), Child = content });
    }

    private IEnumerable<string> KnobShortPressBindings()
    {
        var codes = new[] { "KC_MUTE", "KC_MPLY", "KC_NO", "KC_ENT", "KC_SPC", "KC_CALC", "KC_MYCM" }
        .Concat(BindingCatalog.All.Select(binding => binding.Code).Where(code => BindingCatalog.IsAssignable(code) && (code != "KC_TRNS" || _state.LayerIndex > 0)))
        .Concat(Enumerable.Range(0, 4).SelectMany(layer => new[] { "MO", "TG", "TO" }.Select(action => $"{action}({layer})")))
        .Concat(_state.Project.Macros.Select(macro => "MACRO:" + macro.Id)).Distinct();
        return _state.Layer.KnobPressByFunction ? new[] { "KC_TRNS" }.Concat(codes.Where(KnobPresets.IsPressOverrideAssignable)).Distinct() : codes;
    }

    private async Task<bool> SetKnobShortPressModeAsync(bool byFunction)
    {
        if (_busy || _startupLoading || !IsKnobSelection()) return false;
        var state = _state; var layer = state.LayerIndex;
        var fingerprint = EditorState.Fingerprint(state.Project); var path = state.Path;
        if (!await GuardNavigation() || !IsKeyBindingContextCurrent(state, fingerprint, path) || layer != state.LayerIndex || !IsKnobSelection()) return false;
        if (state.Layer.KnobPressByFunction == byFunction) return true;
        CommitCandidate(project => project.Layers[layer].KnobPressByFunction = byFunction);
        Changed(); Build(); return true;
    }

    private async Task<bool> SetKnobShortPressTargetAsync(int bit)
    {
        if (_busy || _startupLoading || !IsKnobSelection() || !_state.Layer.KnobPressByFunction || !KnobPresets.CycleOptions.Any(option => option.Bit == bit)) return false;
        var state = _state; var layer = state.LayerIndex;
        if (!await GuardNavigation() || !ReferenceEquals(state, _state) || layer != state.LayerIndex || !IsKnobSelection() || !state.Layer.KnobPressByFunction) return false;
        _knobPressTargetBit = bit; Build(); return true;
    }

    private async Task<bool> SetKnobShortPressAsync(string binding)
    {
        if (_busy || _startupLoading || !IsKnobSelection()) return false;
        var state = _state; var layer = state.LayerIndex;
        var fingerprint = EditorState.Fingerprint(state.Project); var path = state.Path;
        var selection = state.SelectedKeys.ToArray();
        var byFunction = state.Layer.KnobPressByFunction; var targetBit = _knobPressTargetBit;
        if (!await GuardNavigation() || !IsKeyBindingContextCurrent(state, fingerprint, path) || layer != state.LayerIndex ||
            !state.SelectedKeys.SetEquals(selection) || !IsKnobSelection() || state.Layer.KnobPressByFunction != byFunction || _knobPressTargetBit != targetBit) return false;
        var press = state.Device.Keys.Single(key => key.Label == "Knob").Id;
        if ((byFunction ? KnobPresets.GetPressBinding(state.Layer, targetBit) : state.Layer.Keys[press].Binding) == binding) return true;
        if (!KnobShortPressBindings().Contains(binding)) return false;
        CommitCandidate(project =>
        {
            if (byFunction) KnobPresets.SetPressBinding(project.Layers[layer], targetBit, binding);
            else project.Layers[layer].Keys[press].Binding = binding;
        });
        Changed(); Build();
        var label = byFunction && binding == "KC_TRNS" ? L.T("Wie allgemeiner kurzer Druck") : DisplayBinding(binding);
        Status(L.F($"Kurzes Drücken: {label}")); return true;
    }

    private async Task<bool> SetKnobStepAsync(int step)
    {
        if (step is < 1 or > 5 || _busy || _startupLoading || !IsKnobSelection()) return false;
        var state = _state; var layer = state.LayerIndex;
        if (!await GuardNavigation() || !ReferenceEquals(state, _state) || layer != state.LayerIndex || !IsKnobSelection()) return false;
        if (state.Layer.KnobStep == step) return true;
        CommitCandidate(project => project.Layers[layer].KnobStep = step);
        Changed(); Build(); return true;
    }

    private async Task<bool> SetKnobUndoLayoutAsync(int layout)
    {
        if (layout is < 0 or > 2 || _busy || _startupLoading || !IsKnobSelection()) return false;
        var state = _state; var layer = state.LayerIndex;
        if (!await GuardNavigation() || !ReferenceEquals(state, _state) || layer != state.LayerIndex || !IsKnobSelection()) return false;
        if (state.Layer.KnobUndoLayout == layout) return true;
        CommitCandidate(project => project.Layers[layer].KnobUndoLayout = layout);
        Changed(); Build(); return true;
    }

    private async Task ShowKnobCycleOptionsAsync()
    {
        if (Program.Offline || !IsKnobSelection() || !_state.Layer.KnobCycleEnabled || !await GuardNavigation()) return;
        _navigating = true;
        try
        {
            await EditKnobCycleOptionsCoreAsync(() =>
                CreateKnobCycleOptionsWindow(_state.Layer).ShowDialog<KnobCycleSelection?>(this));
        }
        finally { _navigating = false; }
    }

    private async Task<bool> EditKnobCycleOptionsCoreAsync(Func<Task<KnobCycleSelection?>> choose)
    {
        var state = _state; var layer = state.LayerIndex;
        var fingerprint = EditorState.Fingerprint(state.Project); var path = state.Path;
        bool Current() => IsKeyBindingContextCurrent(state, fingerprint, path) &&
            state.LayerIndex == layer && IsKnobSelection() && state.Layer.KnobCycleEnabled;
        if (!Current()) return false;
        var result = await choose();
        if (result is null || !Current() || result.Mask is < 1 or > 255 || result.UndoLayout is < 0 or > 2) return false;
        var order = result.Order ?? state.Layer.KnobCycleOrder;
        if (!KnobPresets.IsValidOrder(order)) return false;
        if (state.Layer.KnobCycleMask == result.Mask && state.Layer.KnobCycleMacZoom == result.MacShortcuts && state.Layer.KnobUndoLayout == result.UndoLayout && state.Layer.KnobCycleOrder.SequenceEqual(order)) return true;
        CommitCandidate(project =>
        {
            project.Layers[layer].KnobCycleMask = result.Mask;
            project.Layers[layer].KnobCycleMacZoom = result.MacShortcuts;
            project.Layers[layer].KnobUndoLayout = result.UndoLayout;
            project.Layers[layer].KnobCycleOrder = order.ToList();
        });
        Changed(); Build(); return true;
    }

    private Window CreateKnobCycleOptionsWindow(LayerProfile layer)
    {
        var dialog = new Window
        {
            Title = L.T("Drehregler-Funktionen"), Width = 580, MaxHeight = 720, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Background = Brush("#141618")
        };
        var title = Text(L.T("Drehregler-Funktionen"), size: 21); title.FontWeight = FontWeight.SemiBold;
        var ordered = KnobPresets.OrderedCycleOptions(layer).ToList();
        var selected = ordered.Where(option => (layer.KnobCycleMask & option.Bit) != 0).ToList();
        var options = new StackPanel { Name = "KnobCycleOrderedRows", Spacing = 6 };
        ScrollViewer? scroll = null;
        IPointer? draggedPointer = null;
        int draggedBit = 0, dropIndex = -1;
        Point dragStart = default;
        bool dragging = false;
        void ClearDrag()
        {
            draggedPointer = null; draggedBit = 0; dropIndex = -1; dragging = false;
            foreach (var item in options.Children.OfType<Grid>()) { item.Opacity = 1; item.Background = Brushes.Transparent; }
        }
        void StartDrag(int bit, Border handle, PointerPressedEventArgs args)
        {
            if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            draggedPointer = args.Pointer; draggedBit = bit; dragStart = args.GetPosition(options);
            dropIndex = selected.FindIndex(option => option.Bit == bit); dragging = false;
            args.Pointer.Capture(handle); args.Handled = true;
        }
        void ContinueDrag(PointerEventArgs args)
        {
            if (draggedPointer != args.Pointer) return;
            var position = args.GetPosition(options);
            if (!dragging && Math.Abs(position.X - dragStart.X) + Math.Abs(position.Y - dragStart.Y) < 4) return;
            dragging = true;
            var rows = options.Children.OfType<Grid>().ToArray();
            var inside = position.X >= 0 && position.X <= options.Bounds.Width && position.Y >= 0 && position.Y <= options.Bounds.Height;
            dropIndex = !inside || rows.Length == 0 ? -1 : Array.FindIndex(rows, row => position.Y <= row.Bounds.Bottom);
            if (inside && dropIndex < 0 && rows.Length > 0) dropIndex = rows.Length - 1;
            for (var index = 0; index < rows.Length; index++)
            {
                rows[index].Opacity = (int)rows[index].Tag! == draggedBit ? .55 : 1;
                rows[index].Background = index == dropIndex ? Brush("#4A2931") : Brushes.Transparent;
            }
            if (scroll is not null && scroll.Viewport.Height > 0)
            {
                var visiblePosition = args.GetPosition(scroll).Y;
                var movement = visiblePosition < 24 ? -16 : visiblePosition > scroll.Bounds.Height - 24 ? 16 : 0;
                if (movement != 0) scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + movement, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
            }
            args.Handled = true;
        }
        var available = new ComboBox { Name = "KnobCycleAddChoice", HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(available, L.T("Funktion hinzufügen"));
        var add = new Button { Name = "AddKnobCycleFunction", Content = ButtonContent("Hinzufügen") };
        var addRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; addRow.Children.Add(available); Grid.SetColumn(add, 1); addRow.Children.Add(add);
        var addLabel = Text(L.T("Funktion hinzufügen"), true, 11);
        var system = Choice(new[] { "Windows / Linux", "macOS" }, layer.KnobCycleMacZoom ? 1 : 0); system.Name = "KnobCycleSystem";
        Avalonia.Automation.AutomationProperties.SetName(system, L.T("Tastenkürzel für"));
        var systemLabel = Heading("Tastenkürzel für");
        var undoLayout = Choice(new[] { "QWERTY", "QWERTZ", "AZERTY" }, layer.KnobUndoLayout); undoLayout.Name = "KnobCycleUndoLayout";
        Avalonia.Automation.AutomationProperties.SetName(undoLayout, L.T("Tastaturlayout für Rückgängig"));
        var undoLabel = Heading("Tastaturlayout für Rückgängig");
        var body = Column(title, Text(L.T("Zum Verschieben ziehen"), true),
            new Border { Background = Brush("#1C1F22"), Padding = new Thickness(14), Margin = new Thickness(0, 10), Child = options },
            addLabel, addRow,
            systemLabel, system, undoLabel, undoLayout,
            Text(L.T("Zoom, Tabs und Rückgängig hängen von der geöffneten Anwendung ab."), true, 11));
        var cancel = new Button { Content = ButtonContent("Abbrechen"), IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();
        var apply = new Button { Name = "ConfirmKnobCycleSelection", Content = ButtonContent("Übernehmen") }; apply.Classes.Add("primary");
        void UpdateSelection()
        {
            options.Children.Clear();
            for (var index = 0; index < selected.Count; index++)
            {
                var option = selected[index]; var rowIndex = index;
                var row = new Grid { Name = "KnobCycleRow" + option.Bit, Tag = option.Bit, ColumnDefinitions = new("24,*,Auto,Auto,Auto"), ColumnSpacing = 5 };
                var grip = Text("⠿", true, 18); grip.VerticalAlignment = VerticalAlignment.Center;
                var label = Text(option.Label); label.VerticalAlignment = VerticalAlignment.Center;
                var dragContent = new Grid { ColumnDefinitions = new("24,*"), ColumnSpacing = 5 }; dragContent.Children.Add(grip); Grid.SetColumn(label, 1); dragContent.Children.Add(label);
                var handle = new Border { Name = "KnobCycleDrag" + option.Bit, Background = Brushes.Transparent, Child = dragContent, Cursor = new Cursor(StandardCursorType.SizeAll) };
                ToolTip.SetTip(handle, L.T("Zum Verschieben ziehen")); Avalonia.Automation.AutomationProperties.SetName(handle, option.Label + ". " + L.T("Zum Verschieben ziehen"));
                Grid.SetColumnSpan(handle, 2); row.Children.Add(handle);
                handle.PointerPressed += (_, args) => StartDrag(option.Bit, handle, args);
                handle.PointerMoved += (_, args) => ContinueDrag(args);
                handle.PointerReleased += (_, args) =>
                {
                    if (draggedPointer != args.Pointer || args.InitialPressMouseButton != MouseButton.Left) return;
                    var changed = dragging && MoveKnobCycleOption(selected, draggedBit, dropIndex);
                    ClearDrag(); args.Pointer.Capture(null); args.Handled = true;
                    if (changed) UpdateSelection();
                };
                handle.PointerCaptureLost += (_, _) => ClearDrag();
                Button Action(string caption, string tip, string name, bool enabled, Action action)
                {
                    var button = new Button { Name = name + option.Bit, Content = caption, Width = 30, Height = 30, MinHeight = 0, Padding = new Thickness(0), IsEnabled = enabled,
                        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                    ToolTip.SetTip(button, L.T(tip)); Avalonia.Automation.AutomationProperties.SetName(button, L.T(tip));
                    button.Click += (_, _) => { action(); UpdateSelection(); }; return button;
                }
                var up = Action("↑", "Nach oben", "KnobCycleUp", index > 0, () => MoveKnobCycleOption(selected, option.Bit, rowIndex - 1));
                var down = Action("↓", "Nach unten", "KnobCycleDown", index + 1 < selected.Count, () => MoveKnobCycleOption(selected, option.Bit, rowIndex + 1));
                var remove = Action("×", "Entfernen", "KnobCycleRemove", true, () => selected.Remove(option));
                Grid.SetColumn(up, 2); Grid.SetColumn(down, 3); Grid.SetColumn(remove, 4); row.Children.Add(up); row.Children.Add(down); row.Children.Add(remove); options.Children.Add(row);
            }
            var remaining = ordered.Where(option => !selected.Contains(option)).ToArray();
            available.ItemsSource = remaining; available.DisplayMemberBinding = new Avalonia.Data.Binding("Label");
            available.SelectedIndex = remaining.Length > 0 ? 0 : -1; add.IsEnabled = remaining.Length > 0; available.IsEnabled = remaining.Length > 0;
            addLabel.IsVisible = addRow.IsVisible = remaining.Length > 0;
            apply.IsEnabled = selected.Count > 0;
            systemLabel.IsVisible = system.IsVisible = selected.Any(option => option.Bit is 16 or 64 or 128);
            undoLabel.IsVisible = undoLayout.IsVisible = selected.Any(option => option.Bit == 128);
        }
        add.Click += (_, _) => { if (available.SelectedItem is not KnobCycleOption option) return; selected.Add(option); UpdateSelection(); };
        apply.Click += (_, _) =>
        {
            if (apply.IsEnabled)
                dialog.Close(new KnobCycleSelection(selected.Sum(option => option.Bit), system.SelectedIndex == 1, undoLayout.SelectedIndex,
                    CycleOrderForSelection(selected, layer)));
        };
        UpdateSelection();
        var actions = Row(cancel, apply); actions.Name = "KnobCycleFooter"; actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 16, 0, 0);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(24) };
        scroll = new ScrollViewer { Name = "KnobCycleScroll", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll); Grid.SetRow(actions, 1); layout.Children.Add(actions); dialog.Content = layout;
        dialog.Closed += (_, _) => { var pointer = draggedPointer; ClearDrag(); pointer?.Capture(null); };
        dialog.Deactivated += (_, _) => { var pointer = draggedPointer; ClearDrag(); pointer?.Capture(null); };
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || draggedPointer is null) return;
            var pointer = draggedPointer; ClearDrag(); pointer.Capture(null); args.Handled = true;
        };
        dialog.Opened += (_, _) =>
        {
            if (dialog.Screens.ScreenFromWindow(dialog) is not { } screen) return;
            ConstrainKnobDialog(dialog, screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
        };
        return dialog;
    }

    private static void ConstrainKnobDialog(Window dialog, double workWidth, double workHeight)
    {
        dialog.MaxHeight = Math.Min(720, Math.Max(1, workHeight - 48));
        dialog.MaxWidth = Math.Min(580, Math.Max(1, workWidth - 32));
    }
}
