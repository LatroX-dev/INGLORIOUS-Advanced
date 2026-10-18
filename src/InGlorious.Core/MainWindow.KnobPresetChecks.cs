using System.Windows;
using System.Windows.Controls;

namespace InGlorious.Core;

public partial class MainWindow
{
    private async Task RunKnobRuntimeChecksAsync(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Offline checks only.");
        var savedRead = _readKnobRuntime; var devices = _devices;
        var failed = _connectionScanFailed; var scanning = _scanning;
        var revision = _connectionRevision;
        try
        {
            SetOfflineConnectionFixture(["numpad"]); _connectionScanFailed = false; _scanning = false;
            ResetWorkflowFixture("numpad"); SetTab(1); SelectKey("encoder:ccw");
            var snapshot = new OnboardKnobStatus("numpad", 0, 2, "brightness", true, false, 0x7806, 0x7805, 1, 4, 123, 0, 0, 1);
            var tip = (ToolTip)_keyButtons["encoder:ccw"].ToolTip;
            var baseText = tip.Content.ToString()!;
            UpdateLayout(); var editorPosition = KnobPresetPanel.TranslatePoint(new Point(0, 0), this);
            check(FindName("KnobRuntimeText") is null, "Knob status: the sidebar has no runtime row or reserved gap");
            var calls = 0;
            _readKnobRuntime = (_, _) => { calls++; return Task.FromResult<(OnboardKnobStatus?, string)>((snapshot, "fixture")); };
            await RefreshKnobStatusAsync();
            check(calls == 0 && tip.Content.ToString() == baseText, "Knob status: ordinary offline views preserve the normal tooltip without polling hardware");
            await RefreshKnobStatusAsync(true);
            check(calls == 1 && tip.Content.ToString() == baseText, "Knob status: a successful poll does not modify a visible tooltip");
            PopulateKnobRuntimeTooltip(tip, baseText, Device.Id);
            var confirmedTip = tip.Content.ToString()!;
            check(confirmedTip.StartsWith(baseText, StringComparison.Ordinal) && confirmedTip.Contains(L.F($"Auf der Tastatur aktiv: {snapshot.Label}")),
                "Knob status: tooltip opening adds confirmed function while retaining normal help");
            var project = Fingerprint(_project); var footer = StatusText.Text;
            _readKnobRuntime = (_, _) => { calls++; throw new System.IO.IOException("Injected unavailable device"); };
            await RefreshKnobStatusAsync(true);
            UpdateLayout();
            check(_knobStatus is null && tip.Content.ToString() == confirmedTip &&
                KnobPresetPanel.TranslatePoint(new Point(0, 0), this) == editorPosition && StatusText.Text == footer && Fingerprint(_project) == project,
                "Knob status: failed polls change no visible tooltip, sidebar position, footer or edits");
            PopulateKnobRuntimeTooltip(tip, baseText, Device.Id);
            check(tip.Content.ToString() == baseText, "Knob status: reopening after a failed read offers normal help without an unavailable notice");
            var attempts = calls;
            await RefreshKnobStatusAsync(true); await RefreshKnobStatusAsync(true);
            check(calls == attempts && tip.Content.ToString() == baseText, "Knob status: repeated ticks respect backoff without changing the tooltip");
            _knobStatusNextRead = default;
            _readKnobRuntime = (_, _) => Task.FromResult<(OnboardKnobStatus?, string)>((snapshot with { PresetId = "volume", ActivePresetBit = 1 }, "fixture"));
            await RefreshKnobStatusAsync(true);
            check(tip.Content.ToString() == baseText, "Knob status: recovery does not resize an open tooltip");
            PopulateKnobRuntimeTooltip(tip, baseText, Device.Id);
            check(tip.Content.ToString()!.Contains(L.F($"Auf der Tastatur aktiv: {L.T("Lautstärke")}")),
                "Knob status: reopening after recovery shows the newly confirmed function");
            _knobStatusReadAt = DateTime.UtcNow.AddSeconds(-4); PopulateKnobRuntimeTooltip(tip, baseText, Device.Id);
            check(tip.Content.ToString() == baseText, "Knob status: expired snapshots are never presented as active");
            _readKnobRuntime = (_, _) => Task.FromResult<(OnboardKnobStatus?, string)>((null, "fixture"));
            await RefreshKnobStatusAsync(true);
            PopulateKnobRuntimeTooltip(tip, baseText, Device.Id);
            check(tip.Content.ToString() == baseText && _knobStatusNextRead > DateTime.UtcNow.AddSeconds(10),
                "Knob status: unsupported firmware stays quiet with a slower retry");
            _knobStatusNextRead = default;
            _readKnobRuntime = (_, _) => Task.FromResult<(OnboardKnobStatus?, string)>((snapshot, "fixture"));
            await RefreshKnobStatusAsync(true);
            _scanning = true; await RefreshKnobStatusAsync(true);
            PopulateKnobRuntimeTooltip(tip, baseText, Device.Id);
            check(tip.Content.ToString() == baseText && _knobStatus is null, "Knob status: scanning offers normal help without a reading notice");
            _scanning = false; SetOfflineConnectionFixture([]); RefreshKnobRuntimeView();
            PopulateKnobRuntimeTooltip(tip, baseText, Device.Id);
            check(tip.Content.ToString() == baseText, "Knob status: disconnect does not add another persistent message");
            SetOfflineConnectionFixture(["numpad"]); _knobStatusNextRead = default;
            var pending = new TaskCompletionSource<(OnboardKnobStatus?, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _readKnobRuntime = (_, _) => pending.Task;
            var read = RefreshKnobStatusAsync(true); _connectionRevision++; pending.SetResult((snapshot, "fixture")); await read;
            check(_knobStatus is null && tip.Content.ToString() == baseText, "Knob status: responses from an outdated connection are ignored");
            Status("Prior message"); ShowKnobChangeNotice("First"); ShowKnobChangeNotice("Second");
            _knobNoticeUntil = DateTime.UtcNow.AddSeconds(-1); ExpireKnobChangeNotice();
            check(StatusText.Text == "Prior message", "Knob status: consecutive change notices expire to the original footer");
            ShowKnobChangeNotice("Third"); Status("Newer action result");
            _knobNoticeUntil = DateTime.UtcNow.AddSeconds(-1); ExpireKnobChangeNotice();
            check(StatusText.Text == "Newer action result", "Knob status: notice expiry preserves later action or error messages");
        }
        finally
        {
            _readKnobRuntime = savedRead; _devices = devices; _connectionScanFailed = failed; _scanning = scanning;
            _connectionRevision = revision; _knobStatus = null; _knobStatusPath = null; _knobStatusNextRead = default;
            _knobNoticeRestore = null;
            ResetWorkflowFixture("pro-iso");
        }
    }

    private void RunKnobPresetChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Offline checks only.");
        var decision = _pendingEditDecisionOverride;
        var language = L.CurrentLanguage;
        try
        {
            foreach (var id in new[] { "pro-iso", "numpad", "gmmk3-pro-100-iso" })
            {
                foreach (var preset in KnobPresets.All)
                {
                    ResetWorkflowFixture(id); SetTab(1); _layerIndex = 2;
                    SelectKey(Device.Keys.Single(key => key.Label == "Knob").Id);
                    var before = Fingerprint(_project); var expected = ProjectService.Clone(_project);
                    KnobPresets.Apply(expected.Layers[2], preset.Id); ApplyKnobPreset(preset.Id);
                    check(Fingerprint(_project) == Fingerprint(expected), id + ": " + preset.Id + " preserves all other layer and key settings");
                    var changes = _undo.Count; ApplyKnobPreset(preset.Id);
                    check(_undo.Count == changes && (KnobPresetChoice.SelectedItem as KnobPresetRow)?.Id == preset.Id, id + ": preset no-op and selected label");
                    if (changes != 0) { Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == before, id + ": preset has one complete undo"); }
                }
                ResetWorkflowFixture(id); SetTab(1); SelectKey("encoder:ccw");
                SetKnobEditorMode(false);
                var original = Fingerprint(_project);
                var originalDirty = IsDirty; var originalUndo = _undo.Count;
                SetKnobEditorMode(true);
                check(KnobPresetPanel.Visibility == Visibility.Collapsed && KnobShortPressPanel.Visibility == Visibility.Collapsed
                    && ManualBindingEditor.Visibility == Visibility.Visible && KnobManualTargets.Visibility == Visibility.Visible
                    && ManualBindingActions.Visibility == Visibility.Visible && OsActionShortcut.Visibility == Visibility.Visible,
                    id + ": manual mode only offers individual bindings and a compact action shortcut");
                SetKnobEditorMode(false);
                check(KnobPresetPanel.Visibility == Visibility.Visible && KnobShortPressPanel.Visibility == Visibility.Visible
                    && ManualBindingEditor.Visibility == Visibility.Collapsed && ManualBindingActions.Visibility == Visibility.Collapsed
                    && OsActionShortcut.Visibility == Visibility.Collapsed,
                    id + ": presets contain step and hold settings without the binding editor");
                check(Fingerprint(_project) == original && IsDirty == originalDirty && _undo.Count == originalUndo,
                    id + ": switching editor modes is presentation only");
                var pressId = Device.Keys.Single(key => key.Label == "Knob").Id;
                var pressExpected = ProjectService.Clone(_project); pressExpected.Layers[0].Keys[pressId].Binding = "KC_MPLY";
                KnobPressChoice.SelectedValue = "KC_MPLY";
                check(!IsKnobManualMode && _selection.SetEquals(["encoder:ccw"]) &&
                    Fingerprint(_project) == Fingerprint(pressExpected) && _undo.Count == originalUndo + 1,
                    id + ": inline short press edits only press and remains in presets");
                ApplyKnobPress("KC_MPLY"); check(_undo.Count == originalUndo + 1, id + ": unchanged short press has no extra undo");
                Undo_Click(this, new RoutedEventArgs());
                check(Fingerprint(_project) == original && KnobPressChoice.SelectedValue?.ToString() == Layer.Keys[pressId].Binding,
                    id + ": short press undo restores the dropdown and all settings");
                var originalPressBinding = Layer.Keys[pressId].Binding;
                KnobPressModeChoice.SelectedIndex = 1;
                check(Layer.KnobPressByFunction && Layer.KnobPressBindings.Count == 0 && KnobPressFunctionChoice.Visibility == Visibility.Visible &&
                    KnobPressChoice.SelectedValue?.ToString() == "KC_TRNS", id + ": per-function mode starts by inheriting without overwriting existing bindings");
                var targetOnly = Fingerprint(_project); var targetUndo = _undo.Count;
                KnobPressFunctionChoice.SelectedValue = 4;
                check(Fingerprint(_project) == targetOnly && _undo.Count == targetUndo && _knobPressTargetBit == 4,
                    id + ": selecting a press function does not change the configured rotation preset");
                KnobPressChoice.SelectedValue = "KC_MPLY";
                check(Layer.KnobPressBindings.Count == 1 && Layer.KnobPressBindings[4] == "KC_MPLY" && Layer.Keys[pressId].Binding == originalPressBinding &&
                    OnboardSerializer.Prepare(_project).SchemaVersion == 7, id + ": per-function action is independently transferable");
                var safePress = Fingerprint(_project); ApplyKnobPress("MO(1)");
                check(Fingerprint(_project) == safePress && !KnobPressChoice.Items.Cast<BindingOption>().Any(option => option.Code == "MO(1)" || option.Code == "KC_LCTL"),
                    id + ": per-function choices reject held layer and modifier actions");
                KnobPressModeChoice.SelectedIndex = 0;
                check(!Layer.KnobPressByFunction && Layer.KnobPressBindings[4] == "KC_MPLY" && KnobPressFunctionChoice.Visibility == Visibility.Collapsed,
                    id + ": common press mode keeps stored per-function actions");
                Undo_Click(this, new RoutedEventArgs()); Undo_Click(this, new RoutedEventArgs()); Undo_Click(this, new RoutedEventArgs());
                check(Fingerprint(_project) == original && _undo.Count == originalUndo, id + ": per-function edits undo to the exact original configuration");
                SelectKnobManualTarget("encoder:ccw"); SetKnobEditorMode(false);
                _selection.Add("encoder:cw"); SetKnobEditorMode(true);
                check(InspectorSubtitle.Text.EndsWith(L.T("Drehen"), StringComparison.Ordinal) && Fingerprint(_project) == original,
                    id + ": both rotation targets are not mislabeled as a press");
                SelectKnobManualTarget("encoder:ccw"); SetKnobEditorMode(false);
                ChangeKnobSettings(layer => layer.KnobStep = 3);
                check(Layer.KnobStep == 3 && _undo.Count == 1 && OnboardSerializer.Prepare(_project).SchemaVersion == 5, id + ": step setting stored without enabling hold");
                ChangeKnobSettings(layer => layer.KnobStep = 3);
                check(_undo.Count == 1, id + ": unchanged step does not add undo");
                Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == original, id + ": undo restores step");
                KnobCycleCheck.IsChecked = true; KnobCycle_Click(this, new RoutedEventArgs());
                check(Layer.KnobCycleEnabled && KnobCycleOptionsButton.Visibility == Visibility.Visible, id + ": hold reveals function selection");
                var beforeCycle = Fingerprint(_project); var cycleUndo = _undo.Count;
                ApplyKnobPress("MO(1)");
                check(Fingerprint(_project) == beforeCycle && _undo.Count == cycleUndo &&
                    KnobPressChoice.SelectedValue?.ToString() == Layer.Keys[pressId].Binding,
                    id + ": inline press rejects incompatible hold action without changing selection or configuration");
                ChooseKnobCycle(_ => null);
                ChooseKnobCycle(_ => new(0, false, 0));
                check(Fingerprint(_project) == beforeCycle && _undo.Count == cycleUndo, id + ": canceled or empty cycle choices preserve state");
                ChooseKnobCycle(_ => new(1 | 2 | 128, true, 1));
                check(Layer.KnobCycleMask == 131 && Layer.KnobCycleMacZoom && Layer.KnobUndoLayout == 1 && _undo.Count == cycleUndo + 1,
                    id + ": cycle subset, platform and layout are one undoable edit");
                check(KnobCycleHint.Text.Contains(L.T("Rückgängig / Wiederholen")) && !KnobCycleHint.Text.Contains(L.T("Musiktitel")), id + ": hint reflects selected cycle functions");
                Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == beforeCycle, id + ": cycle options undo restores all settings");
                ChooseKnobCycle(_ => new(Layer.KnobCycleMask, Layer.KnobCycleMacZoom, Layer.KnobUndoLayout, [2, 1, 4, 8, 16, 32, 64, 128]));
                check(Layer.KnobCycleOrder[0] == 2 && OnboardSerializer.Prepare(_project).SchemaVersion == 6 && _undo.Count == cycleUndo + 1,
                    id + ": reordered functions are one transferable change");
                Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == beforeCycle, id + ": order undo restores the complete configuration");
                var press = Device.Keys.Single(key => key.Label == "Knob").Id;
                SelectKey(press); var beforeInvalid = Fingerprint(_project);
                ApplyBinding("MO(1)");
                check(Fingerprint(_project) == beforeInvalid, id + ": Fn binding rejected while long-press is active");
                KnobCycleCheck.IsChecked = false; KnobCycle_Click(this, new RoutedEventArgs()); ApplyBinding("MO(1)");
                check(!KnobCycleCheck.IsEnabled && !Layer.KnobCycleEnabled && Layer.Keys[press].Binding == "MO(1)", id + ": Fn remains available after disabling hold");
                ResetWorkflowFixture(id); SetTab(1); SelectKey("encoder:ccw");
                DebounceInput.Text = "13"; _pendingEditDecisionOverride = () => PendingEditChoice.Cancel;
                var pending = Fingerprint(_project); ChangeKnobSettings(layer => layer.KnobStep = 5);
                check(HasPendingTiming && Fingerprint(_project) == pending && _undo.Count == 0, id + ": pending-edit cancellation protects step change");
                DiscardPendingEdits(); _pendingEditDecisionOverride = null;
            }
            var model = ProjectService.Create("numpad");
            model.Layers[0].KnobCycleMask = 1 | 2 | 8 | 16;
            var dialog = new KnobOptionsWindow(model.Layers[0]);
            check(dialog.ActiveBits.SequenceEqual(new[] { 1, 2, 8, 16 }) && dialog.ConfirmButton.IsEnabled, "Knob dialog shows active functions in order");
            check(dialog.ChosenOrder.SequenceEqual(model.Layers[0].KnobCycleOrder), "Opening order settings preserves gaps and the complete existing order");
            dialog.MoveOption(1, -1); dialog.MoveOption(16, 1);
            check(dialog.ChosenOrder.SequenceEqual(model.Layers[0].KnobCycleOrder), "Moving past either order boundary leaves the order unchanged");
            dialog.MoveOption(2, -1);
            check(dialog.ActiveBits.SequenceEqual(new[] { 2, 1, 8, 16 }) && dialog.ChosenOrder[0] == 2
                && KnobPresets.IsValidOrder(dialog.ChosenOrder), "Order dialog moves selected functions and keeps inactive functions available");
            dialog.MoveOption(2, 1);
            check(dialog.ChosenOrder.SequenceEqual(model.Layers[0].KnobCycleOrder), "Moving back restores the original full order");
            check(dialog.DropOption(1, 4) && dialog.ActiveBits.SequenceEqual(new[] { 2, 8, 16, 1 }) &&
                KnobPresets.IsValidOrder(dialog.ChosenOrder), "Dragging the first function below the last keeps all active and inactive functions");
            check(dialog.DropOption(1, 0) && dialog.ChosenOrder.SequenceEqual(model.Layers[0].KnobCycleOrder),
                "Dragging back to the first slot restores original order including inactive gaps");
            check(!dialog.DropOption(2, 1) && !dialog.DropOption(2, 2) && !dialog.DropOption(4, 0) &&
                !dialog.DropOption(1, -1) && !dialog.DropOption(1, 5) && dialog.ChosenOrder.SequenceEqual(model.Layers[0].KnobCycleOrder),
                "Same-position, outside and foreign drag drops preserve the order");
            foreach (var bit in dialog.ActiveBits.ToArray()) dialog.RemoveOption(bit);
            check(!dialog.ConfirmButton.IsEnabled, "Knob dialog cannot accept an empty cycle");
            dialog.AddOption(128);
            check(dialog.ConfirmButton.IsEnabled, "One selected cycle function can be accepted");
            dialog.Close();
            ResetWorkflowFixture("numpad"); SetTab(1); SelectKey("encoder:ccw");
            Layer.KnobCycleEnabled = true;
            foreach (var option in L.SupportedLanguages)
            {
                L.SetLanguage(option.Code); RefreshKnobPresets();
                check(KnobCycleOptionsButton.Content?.ToString() == L.T("Reihenfolge …"), option.Code + ": cycle function button follows the UI language");
                check(KnobCycleHint.Text == L.F($"700 ms halten: {KnobPresets.CycleSummary(Layer)}. Kurzes Drücken behält seine Belegung."), option.Code + ": cycle hint follows the UI language");
            }
            L.SetLanguage(language);
            ResetWorkflowFixture("numpad"); SetTab(1); SelectKey("encoder:ccw");
            for (var index = 0; index < 6; index++) _project.Macros.Add(new MacroDefinition
            {
                Id = "capacity-" + index, Name = "Capacity " + index,
                Steps = Enumerable.Range(0, 128).Select(step => new MacroStep { Kind = step % 2 == 0 ? "down" : "up", Key = "KC_A" }).ToList()
            });
            var full = Fingerprint(_project);
            check(OnboardSerializer.Prepare(_project).MacroEvents == 768, "Legacy macro-capacity fixture is valid before adding knob settings");
            ChangeKnobSettings(layer => layer.KnobStep = 2);
            KnobCycleCheck.IsChecked = true; KnobCycle_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == full && !Layer.KnobCycleEnabled && _undo.Count == 0,
                "Knob settings reject insufficient macro space without an unhandled error, lost actions or an undo entry");
        }
        finally { _knobManualModes.Clear(); L.SetLanguage(language); _pendingEditDecisionOverride = decision; DiscardPendingEdits(); ResetWorkflowFixture("pro-iso"); }
    }
}
