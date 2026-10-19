using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace InGlorious.Core;

public partial class MainWindow
{
    private readonly Dictionary<(string Device, int Layer), bool> _knobManualModes = [];
    private DispatcherTimer? _knobStatusTimer;
    private CancellationTokenSource? _knobStatusCancellation;
    private bool _knobStatusReading;
    private int _knobStatusPauseDepth;
    private Task _knobStatusCompletion = Task.CompletedTask;
    private string? _knobStatusDevice, _knobStatusPath;
    private OnboardKnobStatus? _knobStatus;
    private DateTime _knobStatusNextRead;
    private DateTime _knobStatusReadAt, _knobNoticeUntil;
    private long _knobStatusConnectionRevision, _knobNoticeRevision;
    private string? _knobNoticeRestore;
    private Func<string, CancellationToken, Task<(OnboardKnobStatus? Status, string Path)>> _readKnobRuntime = ReadKnobRuntimeAsync;
    private bool CanReadKnobStatus => _knobStatusPauseDepth == 0 && IsVisible && WindowState != WindowState.Minimized &&
        !_startupClosed && !_startupLoading && !_startupFinishing && !_readingDevice && !_checkingFirmwareGuide && !_scanning &&
        IsEnabled && EditorSurface.IsEnabled && _activeTab == 1 && (IsKnobPresetSelection() || IsKnobHovered) && IsActiveDeviceConnected &&
        !OwnedWindows.Cast<Window>().Any(window => window.IsVisible);
    private bool IsKnobHovered => Device.Keys.Where(key => key.IsEncoder || key.Label == "Knob")
        .Any(key => _keyButtons.TryGetValue(key.Id, out var button) && button.IsMouseOver);
    private void RefreshKnobRuntimeView()
    {
        if (_activeTab != 1 || _knobStatusDevice != Device.Id || !IsActiveDeviceConnected)
        {
            if (!_hardwareSwitchReadingActive) _knobStatusCancellation?.Cancel();
            _knobStatus = null; _knobStatusPath = null;
            _knobStatusDevice = Device.Id; _knobStatusNextRead = DateTime.MinValue;
        }
        if (_knobStatusTimer is null && !_startupClosed && !_screenshotMode)
        {
            _knobStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _knobStatusTimer.Tick += KnobStatusTick; _knobStatusTimer.Start();
        }
    }
    private void AttachKnobRuntimeTooltip(Button button)
    {
        var baseText = button.ToolTip?.ToString() ?? "";
        var deviceId = Device.Id;
        var tip = new ToolTip { Content = baseText };
        button.ToolTip = tip;
        button.MouseEnter += async (_, _) => await RefreshKnobStatusAsync();
        button.ToolTipOpening += (_, _) => PopulateKnobRuntimeTooltip(tip, baseText, deviceId);
    }
    private void PopulateKnobRuntimeTooltip(ToolTip tip, string baseText, string deviceId)
    {
        // Snapshot on opening only. Polling must never resize an already open tooltip.
        var current = CanReadKnobStatus && Device.Id == deviceId && _knobStatus?.DeviceId == deviceId &&
            _knobStatusPath is not null && _knobStatusConnectionRevision == _connectionRevision &&
            DateTime.UtcNow - _knobStatusReadAt <= TimeSpan.FromSeconds(3) ? _knobStatus : null;
        tip.Content = current is null ? baseText : baseText + "\n\n" + L.F($"Auf der Tastatur aktiv: {current.Label}") + "\n" + L.F($"Ebene {current.Layer}");
    }
    private void ShowKnobChangeNotice(string label)
    {
        if (_knobNoticeRestore is null || _statusRevision != _knobNoticeRevision) _knobNoticeRestore = StatusText.Text;
        Status(L.F($"Drehregler: {label}"));
        _knobNoticeRevision = _statusRevision; _knobNoticeUntil = DateTime.UtcNow.AddSeconds(3);
    }
    private void ExpireKnobChangeNotice()
    {
        if (_knobNoticeRestore is null) return;
        if (_statusRevision != _knobNoticeRevision) { _knobNoticeRestore = null; return; }
        if (DateTime.UtcNow < _knobNoticeUntil) return;
        var restore = _knobNoticeRestore; _knobNoticeRestore = null; Status(restore);
    }
    private async void KnobStatusTick(object? sender, EventArgs e)
    {
        await RefreshHardwareSwitchesAsync();
        await RefreshKnobStatusAsync();
    }
    private static Task<(OnboardKnobStatus? Status, string Path)> ReadKnobRuntimeAsync(string deviceId, CancellationToken token) =>
        Task.Run(async () =>
        {
            var matches = OnboardClient.FindDevices().Where(device => device.DeviceId == deviceId).ToArray();
            token.ThrowIfCancellationRequested();
            if (matches.Length != 1) throw new System.IO.IOException("No unique knob endpoint.");
            await using var client = OnboardClient.Open(matches[0]);
            return (await client.GetKnobStatusAsync(token), matches[0].Path);
        }, token);
    private async Task RefreshKnobStatusAsync(bool allowOfflineFixture = false)
    {
        if (_screenshotMode && !allowOfflineFixture) return;
        ExpireKnobChangeNotice();
        // A foreground operation waits for this poll to release its pooled HID lease.
        // Do not cancel it merely because the owner is disabled or a dialog is opening.
        if (_knobStatusPauseDepth != 0) return;
        if (!CanReadKnobStatus)
        {
            if (!_hardwareSwitchReadingActive) _knobStatusCancellation?.Cancel();
            _knobStatus = null; _knobStatusPath = null;
            return;
        }
        if (_knobStatusReading || DateTime.UtcNow < _knobStatusNextRead) return;
        var deviceId = Device.Id; var revision = _connectionRevision;
        _knobStatusReading = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _knobStatusCompletion = completion.Task;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cancellation.Token;
        _knobStatusCancellation = cancellation;
        try
        {
            // The read includes asynchronous transport disposal. An outer WaitAsync
            // could finish on cancellation while that native handle is still closing.
            var result = await _readKnobRuntime(deviceId, token);
            if (!CanReadKnobStatus || Device.Id != deviceId || revision != _connectionRevision || cancellation.IsCancellationRequested) return;
            if (result.Status is not null && result.Status.DeviceId != deviceId) throw new System.IO.InvalidDataException("Unexpected knob status device.");
            var previous = _knobStatus;
            _knobStatusDevice = deviceId;
            _knobStatus = result.Status;
            _knobStatusReadAt = DateTime.UtcNow; _knobStatusConnectionRevision = revision;
            if (result.Status is { } status)
            {
                _knobStatusNextRead = default;
                if (_knobStatusPath == result.Path && previous is not null && previous.ChangeCounter != status.ChangeCounter &&
                    previous.Generation == status.Generation && previous.ActiveCrc == status.ActiveCrc)
                    ShowKnobChangeNotice(status.Label);
            }
            else
            {
                _knobStatusNextRead = DateTime.UtcNow.AddSeconds(15);
            }
            _knobStatusPath = result.Path;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("Knob status: " + error.Message);
            if (Device.Id != deviceId || revision != _connectionRevision) return;
            _knobStatus = null; _knobStatusPath = null;
            _knobStatusNextRead = DateTime.UtcNow.AddSeconds(3);
        }
        finally
        {
            _knobStatusCancellation = null; _knobStatusReading = false;
            completion.TrySetResult();
        }
    }
    private async Task<IDisposable> PauseKnobStatusAsync()
    {
        _knobStatusPauseDepth++;
        _knobStatus = null; _knobStatusPath = null;
        try
        {
            await _knobStatusCompletion;
            return new KnobStatusPause(this);
        }
        catch { _knobStatusPauseDepth--; throw; }
    }
    private sealed class KnobStatusPause(MainWindow owner) : IDisposable
    {
        private MainWindow? _owner = owner;
        public void Dispose()
        {
            if (_owner is not { } current) return;
            _owner = null; current._knobStatusPauseDepth--;
            if (current._knobStatusPauseDepth == 0)
            { current._knobStatusNextRead = default; current._hardwareSwitchNextRead = default; }
        }
    }
    private void StopKnobStatusMonitor()
    {
        _knobStatusCancellation?.Cancel();
        if (_knobStatusTimer is null) return;
        _knobStatusTimer.Stop(); _knobStatusTimer.Tick -= KnobStatusTick; _knobStatusTimer = null;
    }
    private bool IsKnobManualMode => _knobManualModes.GetValueOrDefault((Device.Id, _layerIndex), KnobPresets.Find(Layer) is null);
    private void KnobPresetMode_Click(object sender, RoutedEventArgs e) => SetKnobEditorMode(false);
    private void KnobManualMode_Click(object sender, RoutedEventArgs e) => SetKnobEditorMode(true);
    private void SetKnobEditorMode(bool manual)
    {
        if (!IsKnobPresetSelection() || !ResolvePendingEdits()) return;
        _knobManualModes[(Device.Id, _layerIndex)] = manual;
        RefreshKnobPresets(); RefreshBindingTarget(); UpdateLayout();
        if (manual) RevealSelectedKeyBinding();
        InspectorScroll.ScrollToTop();
    }
    private void KnobManualTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target }) SelectKnobManualTarget(target);
    }
    private bool _refreshingKnobPress;
    private string? _knobPressDevice, _knobPressPresetId;
    private int _knobPressLayer = -1, _knobPressTargetBit = 1;
    private void RefreshKnobPressChoices()
    {
        var press = Device.Keys.Single(key => key.Label == "Knob").Id;
        var presetId = KnobPresets.Find(Layer)?.Id;
        if (_knobPressDevice != Device.Id || _knobPressLayer != _layerIndex || _knobPressPresetId != presetId)
        {
            _knobPressDevice = Device.Id; _knobPressLayer = _layerIndex; _knobPressPresetId = presetId;
            var bit = KnobPresets.BitForPreset(presetId); _knobPressTargetBit = bit == 0 ? 1 : bit;
        }
        var byFunction = Layer.KnobPressByFunction;
        var current = byFunction ? KnobPresets.GetPressBinding(Layer, _knobPressTargetBit) : Layer.Keys[press].Binding;
        var common = new[] { "KC_MUTE", "KC_MPLY", "KC_NO", "KC_ENT", "KC_SPC", "KC_CALC", "KC_MYCM" };
        var options = common.Select(code => new BindingOption(code, BindingLabel(code)))
            .Concat(BindingCatalog.All.Where(option => BindingCatalog.IsAssignable(option.Code) && (option.Code != "KC_TRNS" || _layerIndex > 0))
                .Select(option => new BindingOption(option.Code, BindingLabel(option.Code))))
            .Concat(Enumerable.Range(0, 4).SelectMany(layer => new[] { "MO", "TG", "TO" }
                .Select(mode => new BindingOption($"{mode}({layer})", BindingLabel($"{mode}({layer})")))))
            .Concat(_project.Macros.Select(macro => new BindingOption("MACRO:" + macro.Id, L.T("Makro · ") + macro.Name)))
            .DistinctBy(option => option.Code).ToList();
        if (byFunction)
        {
            options = options.Where(option => option.Code != "KC_TRNS" && KnobPresets.IsPressOverrideAssignable(option.Code)).ToList();
            options.Insert(0, new("KC_TRNS", L.T("Wie allgemeiner kurzer Druck")));
        }
        if (options.All(option => option.Code != current)) options.Insert(0, new(current, BindingLabel(current)));
        _refreshingKnobPress = true;
        try
        {
            KnobPressModeChoice.ItemsSource = new[] { L.T("Für alle Funktionen gleich"), L.T("Je Funktion") };
            KnobPressModeChoice.SelectedIndex = byFunction ? 1 : 0;
            KnobPressFunctionChoice.Visibility = byFunction ? Visibility.Visible : Visibility.Collapsed;
            KnobPressFunctionChoice.ItemsSource = KnobPresets.CycleOptions;
            KnobPressFunctionChoice.SelectedValue = _knobPressTargetBit;
            KnobPressChoice.ItemsSource = options; KnobPressChoice.SelectedValue = current;
        }
        finally { _refreshingKnobPress = false; }
    }
    private void KnobPressMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingKnobPress || KnobPressModeChoice.SelectedIndex is < 0 or > 1) return;
        var byFunction = KnobPressModeChoice.SelectedIndex == 1;
        ChangeKnobSettings(layer => layer.KnobPressByFunction = byFunction);
        RefreshKnobPresets();
    }
    private void KnobPressFunction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingKnobPress || KnobPressFunctionChoice.SelectedItem is not KnobCycleOption option) return;
        if (_readingDevice || _startupLoading || _startupFinishing || !IsKnobPresetSelection() || !ResolvePendingEdits())
        { RefreshKnobPresets(); return; }
        _knobPressTargetBit = option.Bit; RefreshKnobPressChoices();
    }
    private void KnobPress_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingKnobPress && KnobPressChoice.SelectedItem is BindingOption option) ApplyKnobPress(option.Code);
    }
    private void ApplyKnobPress(string binding)
    {
        if (_readingDevice || _startupLoading || _startupFinishing || !IsKnobPresetSelection() || !ResolvePendingEdits())
        { RefreshKnobPresets(); return; }
        var press = Device.Keys.Single(key => key.Label == "Knob").Id;
        var byFunction = Layer.KnobPressByFunction;
        if ((byFunction ? KnobPresets.GetPressBinding(Layer, _knobPressTargetBit) : Layer.Keys[press].Binding) == binding) return;
        if (byFunction && !KnobPresets.IsPressOverrideAssignable(binding)) { RefreshKnobPresets(); return; }
        void Change(LayerProfile layer)
        {
            if (byFunction) KnobPresets.SetPressBinding(layer, _knobPressTargetBit, binding);
            else layer.Keys[press].Binding = binding;
        }
        var candidate = ProjectService.Clone(_project); Change(candidate.Layers[_layerIndex]);
        if (KnobPresets.HasInvalidCycle(candidate))
        { RefreshKnobPresets(); Status(L.T("Deaktiviere zuerst den Wechsel per langem Druck.")); return; }
        if (!ValidateKnobCandidate(candidate)) { RefreshKnobPresets(); return; }
        BeginChange(); Change(Layer);
        FinishChange(L.F($"Kurzes Drücken: {(byFunction && binding == "KC_TRNS" ? L.T("Wie allgemeiner kurzer Druck") : BindingLabel(binding))}"));
    }
    private void SelectKnobManualTarget(string target)
    {
        if (!Device.HasEncoder || !ResolvePendingEdits()) return;
        var id = target == "press" ? Device.Keys.Single(key => key.Label == "Knob").Id : target;
        if (!Device.Keys.Any(key => key.Id == id)) return;
        _knobManualModes[(Device.Id, _layerIndex)] = true;
        _selection.Clear(); _selection.Add(id); _editingSlider = false;
        RenderKeyboard(); RefreshInspector(); RevealSelectedKeyBinding();
    }
    private void RefreshKnobEditorView()
    {
        if (KnobModeTabs is null || ManualBindingActions is null) return;
        var knob = IsKnobPresetSelection(); var manual = !knob || IsKnobManualMode;
        RefreshKnobRuntimeView();
        KnobModeTabs.Visibility = knob ? Visibility.Visible : Visibility.Collapsed;
        KnobManualTargets.Visibility = knob && manual ? Visibility.Visible : Visibility.Collapsed;
        KnobPresetPanel.Visibility = knob && !manual ? Visibility.Visible : Visibility.Collapsed;
        KnobShortPressPanel.Visibility = KnobPresetPanel.Visibility;
        ManualBindingEditor.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ManualBindingActions.Visibility = manual && !_editingSlider ? Visibility.Visible : Visibility.Collapsed;
        OsActionShortcut.Visibility = _activeTab == 1 && manual && !_editingSlider ? Visibility.Visible : Visibility.Collapsed;
        OsActionShortcut.IsEnabled = _selection.Count > 0;
        foreach (var (button, selected) in new[] { (KnobPresetModeButton, !manual), (KnobManualModeButton, manual) })
        { button.Background = Brush(selected ? "#302025" : "#1C1F22"); button.BorderBrush = Brush(selected ? "#EF646B" : "#353A40"); }
        if (!knob) return;
        var press = Device.Keys.Single(key => key.Label == "Knob").Id;
        RefreshKnobPressChoices();
        var target = _selection.Count == 1 ? _selection.Single() : "";
        foreach (var (button, id) in new[] { (KnobLeftTarget, "encoder:ccw"), (KnobRightTarget, "encoder:cw"), (KnobPressTarget, press) })
        { button.Background = Brush(_selection.Contains(id) ? "#3A252B" : "#252A2F"); button.BorderBrush = Brush(_selection.Contains(id) ? "#A34F59" : "#353A40"); }
        InspectorTitle.Text = L.T("Drehregler");
        InspectorSubtitle.Text = L.F($"Ebene {_layerIndex}") + (manual ? " · " + L.T(target == "encoder:ccw" ? "Nach links drehen" : target == "encoder:cw" ? "Nach rechts drehen" : target == press ? "Drücken" : "Drehen") : "");
    }
    private void KnobStep_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingKnobPresets || KnobStepChoice.SelectedItem is not int value) return;
        ChangeKnobSettings(layer => layer.KnobStep = value);
    }
    private void KnobUndoLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingKnobPresets || KnobUndoLayoutChoice.SelectedIndex is < 0 or > 2) return;
        var value = KnobUndoLayoutChoice.SelectedIndex;
        ChangeKnobSettings(layer => layer.KnobUndoLayout = value);
    }
    private void ChangeKnobSettings(Action<LayerProfile> change)
    {
        if (_readingDevice || _startupLoading || _startupFinishing || !IsKnobPresetSelection() || !ResolvePendingEdits()) { RefreshKnobPresets(); return; }
        var candidate = ProjectService.Clone(_project); change(candidate.Layers[_layerIndex]);
        if (Fingerprint(candidate) == Fingerprint(_project)) return;
        if (!ValidateKnobCandidate(candidate)) { RefreshKnobPresets(); return; }
        BeginChange(); change(Layer); FinishChange(L.T("Drehregler-Vorlage zugewiesen."));
    }
    private bool ValidateKnobCandidate(AppProject candidate)
    {
        try { OnboardSerializer.ValidateEditorProject(candidate); return true; }
        catch (System.IO.InvalidDataException error) { Status(error.Message); return false; }
    }
    private void KnobCycleOptions_Click(object sender, RoutedEventArgs e) => ChooseKnobCycle(layer =>
    {
        var dialog = new KnobOptionsWindow(layer) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    });
    private void ChooseKnobCycle(Func<LayerProfile, KnobCycleSelection?> choose)
    {
        if (_readingDevice || _startupLoading || _startupFinishing || !IsKnobPresetSelection() || !Layer.KnobCycleEnabled || !ResolvePendingEdits()) return;
        var context = _project; var index = _layerIndex; var fingerprint = Fingerprint(context);
        var selected = choose(ProjectService.Clone(_project).Layers[index]);
        if (selected is null || !ReferenceEquals(context, _project) || fingerprint != Fingerprint(_project) || index != _layerIndex || !IsKnobPresetSelection() || !Layer.KnobCycleEnabled) return;
        if (selected.Mask is < 1 or > 255 || selected.UndoLayout is < 0 or > 2) return;
        var order = selected.Order ?? Layer.KnobCycleOrder;
        if (!KnobPresets.IsValidOrder(order)) return;
        ChangeKnobSettings(layer => { layer.KnobCycleMask = selected.Mask; layer.KnobCycleMacZoom = selected.MacShortcuts; layer.KnobUndoLayout = selected.UndoLayout; layer.KnobCycleOrder = [.. order]; });
    }
}
