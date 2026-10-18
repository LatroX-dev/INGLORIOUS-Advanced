using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private async Task RunKeyBindingChecksAsync(Action<bool, string> check)
    {
        if (!Program.Offline) throw new InvalidOperationException("Key binding checks require offline mode.");
        await RunKnobPresetChecksAsync(check);
        var savedState = _state; var savedPage = _page; var savedSlider = _editingSlider;
        var savedBusy = _busy; var savedLoading = _startupLoading; var savedInitial = _initialConnectionsPending;
        var savedPending = _pendingApply; var savedNavigating = _navigating;
        try
        {
            _busy = _startupLoading = _initialConnectionsPending = _navigating = false; _pendingApply = null;
            var target = ProjectService.Create("numpad");
            var key = DeviceCatalog.Get("numpad").Keys.First(k => !k.IsEncoder).Id;
            target.Name = "Local profile"; target.Layers[0].Keys[key].Color = "#123456";
            var source = ProjectService.Create("numpad");
            source.Layers[0].Keys[key].Binding = "KC_A"; source.Layers[1].Name = "Work";
            source.Slider.Mode = 1;
            source.Macros.Add(new() { Id = "other_layer_macro", Name = "Other layer", Steps = [new() { Kind = "down", Key = "KC_B" }, new() { Kind = "up", Key = "KC_B" }] });
            source.Layers[3].Keys[key].Binding = "MACRO:other_layer_macro";
            var document = KeyBindingTransfer.Capture(source);
            var all = new KeyBindingSelection([0, 1, 2, 3], true);
            var exportDialog = CreateKeyBindingSelectionWindow(false, source.Layers.Select((layer, index) => (index, layer.Name)).ToArray(), true);
            var exportLayers = exportDialog.GetLogicalDescendants().OfType<CheckBox>().Where(box => box.Tag is int).ToArray();
            var exportSlider = exportDialog.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "KeyBindingSlider");
            var exportConfirm = exportDialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ConfirmKeyBindingSelection");
            check(exportLayers.Length == 4 && exportLayers.All(box => box.IsChecked == true) && exportSlider.IsChecked == false,
                "binding export offers all four layers selected and the optional global slider unchecked");
            foreach (var layer in exportLayers) layer.IsChecked = false;
            check(!exportConfirm.IsEnabled, "binding selection requires at least one layer");
            exportSlider.IsChecked = true;
            check(!exportConfirm.IsEnabled, "selecting the global slider alone cannot start binding export");
            exportLayers.Single(box => (int)box.Tag! == 1).IsChecked = true;
            var chosen = ReadKeyBindingSelection(exportLayers, exportSlider);
            var subset = KeyBindingTransfer.Capture(source, chosen.Layers, chosen.IncludeSlider);
            check(exportConfirm.IsEnabled && chosen.Layers.SequenceEqual([1]) && subset.Layers.Single().Index == 1 && subset.Slider is not null && subset.Macros.Count == 0,
                "binding export uses the checked original layer index and slider choice, excluding macros used only by other layers");
            var importDialog = CreateKeyBindingSelectionWindow(true, subset.Layers.Select(layer => (layer.Index, layer.Name)).ToArray(), subset.Slider is not null);
            var importLayers = importDialog.GetLogicalDescendants().OfType<CheckBox>().Where(box => box.Tag is int).ToArray();
            var importSlider = importDialog.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "KeyBindingSlider");
            check(importLayers.Length == 1 && (int)importLayers[0].Tag! == 1 && importLayers[0].IsChecked == true && importSlider.IsChecked == false,
                "binding import offers only file layers and leaves the global slider opt-in");
            var withoutSlider = CreateKeyBindingSelectionWindow(true, [(2, "Alternative")], false);
            check(!withoutSlider.GetLogicalDescendants().OfType<CheckBox>().Any(box => box.Name == "KeyBindingSlider"),
                "a binding file without slider settings has no slider import option");
            EditorState Fresh()
            {
                _state = new EditorState(target, Path.Combine(Path.GetTempPath(), "inglorious-bindings-check-profile.json"));
                _page = "Tastenbelegung"; Build(); return _state;
            }
            var state = Fresh(); var initial = EditorState.Fingerprint(state.Project); var profilePath = state.Path;
            _editingSlider = false; Build();
            var encoder = _state.Device.Keys.First(candidate => candidate.IsEncoder);
            _root.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "BindingSearch").Text = "not-a-binding";
            _keyButtons[encoder.Id].RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await SetKnobModeAsync(true);
            UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            UpdateLayout();
            var bindings = _root.GetLogicalDescendants().OfType<ListBox>().Single(control => control.Name == "BindingChoices");
            var listScroll = bindings.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            check((bindings.SelectedItem as BindingRow)?.Code == state.Layer.Keys[encoder.Id].Binding &&
                listScroll is not null && listScroll.Offset.Y > 0 && bindings.ContainerFromIndex(bindings.SelectedIndex) is not null,
                "clicking the numpad encoder reveals and scrolls to its assigned direction in the rendered binding list");
            check(_root.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "NumpadNumLockHint").Text ==
                L.T("Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0.") &&
                EditorState.Fingerprint(state.Project) == initial && !state.CanUndo,
                "numpad bindings explain the default Num-Lock shortcut without modifying the profile");
            foreach (var slider in new[] { false, true })
            {
                _editingSlider = slider; Build();
                var controls = this.GetLogicalDescendants().OfType<Button>().ToArray();
                check(controls.Count(b => b.Name == "ImportKeyBindings") == 1 && controls.Count(b => b.Name == "ExportKeyBindings") == 1,
                    "key binding file actions are available in the key and slider editors");
            }
            check(!await ImportKeyBindingsCoreAsync(state, initial, profilePath, document, () => Task.FromResult<KeyBindingSelection?>(null)) &&
                EditorState.Fingerprint(state.Project) == initial && !state.CanUndo,
                "canceling binding import leaves the entire editor and undo history unchanged");
            check(await ImportKeyBindingsCoreAsync(state, initial, profilePath, document, () => Task.FromResult<KeyBindingSelection?>(all)), "confirmed bindings are imported");
            check(state.Project.Layers[0].Keys[key].Binding == "KC_A" && state.Project.Layers[1].Name == "Work" && state.Project.Slider.Mode == 1 &&
                state.Project.Layers[0].Keys[key].Color == "#123456" && state.Project.Name == target.Name && state.Path == profilePath && state.Dirty,
                "binding import updates assignments, layer names and slider while preserving lighting and profile identity");
            state.Undo(); check(EditorState.Fingerprint(state.Project) == initial && !state.CanUndo && !state.Dirty,
                "one undo restores the complete pre-import profile");
            check(await ImportKeyBindingsCoreAsync(state, initial, profilePath, document, () => Task.FromResult<KeyBindingSelection?>(new([1], false))) &&
                state.Project.Layers[0].Keys[key].Binding == target.Layers[0].Keys[key].Binding && state.Project.Layers[1].Name == "Work" &&
                state.Project.Slider.Mode == target.Slider.Mode && state.Project.Layers[0].Keys[key].Color == "#123456" && state.Project.Macros.Count == 0,
                "importing one layer leaves unselected layers, slider and colors intact without adding unrelated macros");
            state.Undo(); check(EditorState.Fingerprint(state.Project) == initial && !state.CanUndo,
                "a selected-layer import is also exactly one undo step");

            var confirmations = 0; var rejected = false;
            try { await ImportKeyBindingsCoreAsync(state, initial, profilePath, KeyBindingTransfer.Capture(ProjectService.Create("pro-iso")), () => { confirmations++; return Task.FromResult<KeyBindingSelection?>(all); }); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && confirmations == 0 && EditorState.Fingerprint(state.Project) == initial,
                "a different keyboard model is rejected before confirmation without changing the editor");

            var accepted = await ImportKeyBindingsCoreAsync(state, initial, profilePath, document, () =>
            { state.Change(p => p.Name = "Changed while choosing"); return Task.FromResult<KeyBindingSelection?>(all); });
            check(!accepted && state.Project.Name == "Changed while choosing" && state.Project.Layers[0].Keys[key].Binding == target.Layers[0].Keys[key].Binding,
                "confirmation never overwrites changes made while the import dialog was open");

            state = Fresh(); initial = EditorState.Fingerprint(state.Project);
            accepted = await ImportKeyBindingsCoreAsync(state, initial, state.Path, document, () =>
            { _state = new EditorState("pro-iso"); return Task.FromResult<KeyBindingSelection?>(all); });
            check(!accepted && EditorState.Fingerprint(state.Project) == initial && _state.Device.Id == "pro-iso",
                "switching device invalidates a pending binding import");

            state = Fresh(); initial = EditorState.Fingerprint(state.Project); var oldPath = state.Path;
            accepted = await ImportKeyBindingsCoreAsync(state, initial, oldPath, document, () =>
            { state.MarkSaved(Path.Combine(Path.GetTempPath(), "inglorious-bindings-check-renamed.json")); return Task.FromResult<KeyBindingSelection?>(all); });
            check(!accepted && EditorState.Fingerprint(state.Project) == initial, "saving under a new profile path invalidates an open binding import");
            check(IsOpenProfilePath(state.Path!), "binding export protects the current profile file");

            var pendingCalls = 0; _pendingApply = () => { pendingCalls++; return true; };
            check(!await ImportKeyBindingsCoreAsync(state, initial, state.Path, document, () => { confirmations++; return Task.FromResult<KeyBindingSelection?>(all); }) && confirmations == 0,
                "binding import refuses to overwrite pending editor input");
            await ImportKeyBindingsAsync(); await ExportKeyBindingsAsync();
            check(pendingCalls == 0 && _pendingApply is not null && EditorState.Fingerprint(state.Project) == initial,
                "offline file actions neither open user file dialogs nor apply pending editor changes");
            _pendingApply = null; _busy = true;
            check(!IsKeyBindingContextCurrent(state, initial, state.Path), "active device operations block binding import and export");
            _busy = false; _startupLoading = true;
            check(!IsKeyBindingContextCurrent(state, initial, state.Path), "device startup reads block binding import and export");
        }
        finally
        {
            _state = savedState; _page = savedPage; _editingSlider = savedSlider;
            _busy = savedBusy; _startupLoading = savedLoading; _initialConnectionsPending = savedInitial;
            _navigating = savedNavigating; Build(); _pendingApply = savedPending;
        }
    }

    private async Task RunKnobPresetChecksAsync(Action<bool, string> check)
    {
        await RunKnobStatusChecksAsync(check);
        var savedState = _state; var savedPage = _page; var savedSlider = _editingSlider;
        var savedBusy = _busy; var savedLoading = _startupLoading; var savedInitial = _initialConnectionsPending;
        var savedPending = _pendingApply; var savedNavigating = _navigating; var savedPrompt = _offlinePromptChoice;
        try
        {
            _busy = _startupLoading = _initialConnectionsPending = _navigating = false; _pendingApply = null; _editingSlider = false;
            foreach (var id in new[] { "pro-iso", "numpad", "gmmk3-pro-100-iso" })
            {
                _state = EditorState.FromDevice(ProjectService.Create(id)); _page = "Tastenbelegung";
                T Find<T>(string name) where T : Control => _root.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
                bool HasPresets() => _root.GetLogicalDescendants().OfType<ComboBox>().Any(control => control.Name == "KnobPresetChoice");
                var press = _state.Device.Keys.Single(key => key.Label == "Knob").Id;
                _state.SelectKey(press, false); Build();
                check(HasPresets(), id + ": selecting the knob press offers rotation presets");
                if (id != "numpad")
                {
                    _editingSlider = true; Build();
                    check(HasPresets(), id + ": a stale numpad slider flag does not hide another keyboard's knob presets");
                    await SetKnobModeAsync(true);
                    check(Find<Button>("OsActionHeaderButton").IsEnabled && Find<Button>("KnobManualTargetPress") is not null,
                        id + ": a stale numpad slider flag preserves another keyboard's manual knob editor and OS actions");
                    _editingSlider = false; await SetKnobModeAsync(false);
                }
                var viewFingerprint = EditorState.Fingerprint(_state.Project);
                check(!_root.GetLogicalDescendants().OfType<TextBox>().Any(control => control.Name == "BindingSearch") &&
                    !_root.GetLogicalDescendants().OfType<Button>().Any(control => control.Name is "OpenLayerBindingEditor" or "OsActionHeaderButton"),
                    id + ": presets mode hides manual binding tools");
                _pendingApply = () => true; _offlinePromptChoice = 0;
                await SetKnobModeAsync(true);
                check(HasPresets() && _pendingApply is not null && EditorState.Fingerprint(_state.Project) == viewFingerprint,
                    id + ": canceling pending changes preserves the selected knob view and all profile data");
                _pendingApply = null;
                await SetKnobModeAsync(true, press);
                check(!HasPresets() && Find<Button>("KnobManualTargetPress") is not null && _state.SelectedKeys.SetEquals([press]) && Find<TextBox>("BindingSearch") is not null,
                    id + ": manual mode offers knob targets and binding search without preset settings");
                check(Find<Button>("OsActionHeaderButton").IsEnabled &&
                    Avalonia.Automation.AutomationProperties.GetName(Find<Button>("OsActionHeaderButton")) == L.T("Betriebssystem-Aktion …"),
                    id + ": manual assignments expose the accessible OS-action header icon");
                var osDialog = CreateOsActionWindow();
                osDialog.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "OsActionSystem").SelectedIndex = 1;
                check(osDialog.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "OsActionChoice").Items.Count == OsActionCatalog.For("macos", "de").Count &&
                    osDialog.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "OsActionKeyboardLayout").Items.Count == 3,
                    id + ": OS-action dialog preserves platform actions and keyboard-layout choices");
                check(!await AssignOsActionCoreAsync(() => Task.FromResult<OsAction?>(null)) && EditorState.Fingerprint(_state.Project) == viewFingerprint && !_state.CanUndo,
                    id + ": canceling OS-action selection changes no assignment or history");
                var fileAction = OsActionCatalog.For("windows").Single(action => action.Id == "files");
                check(!await AssignOsActionCoreAsync(() => { _state.SelectKey("encoder:cw", false); return Task.FromResult<OsAction?>(fileAction); }) &&
                    EditorState.Fingerprint(_state.Project) == viewFingerprint && !_state.CanUndo,
                    id + ": changing selected keys invalidates a pending OS-action result");
                _state.SelectKey(press, false); Build();
                check(await AssignOsActionCoreAsync(() => Task.FromResult<OsAction?>(fileAction)) && _state.Layer.Keys[press].Binding.StartsWith("MACRO:", StringComparison.Ordinal),
                    id + ": the OS-action dialog applies through the existing checked assignment path");
                _state.Undo(); Build();
                check(EditorState.Fingerprint(_state.Project) == viewFingerprint && !_state.CanUndo,
                    id + ": an OS action and its shortcut macro undo together once");
                check(!_root.GetLogicalDescendants().OfType<Button>().Any(control => control.Name == "OpenLayerBindingEditor"),
                    id + ": the manual editor has no duplicate Fn/layer navigation button");
                Find<ComboBox>("BindingCategory").SelectedIndex = 3;
                check(Find<ListBox>("BindingChoices").Items.Cast<BindingRow>().All(row => LayerGuidance.TryLayerAction(row.Code, out _, out _)) &&
                    Find<ListBox>("BindingChoices").Items.Count == 12,
                    id + ": the layers category retains all twelve direct layer assignments");
                Find<ComboBox>("BindingCategory").SelectedIndex = 0;
                _pendingApply = () => true; _offlinePromptChoice = 0;
                Find<Button>("KnobManualTargetRight").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                check(_state.SelectedKeys.SetEquals([press]) && _pendingApply is not null,
                    id + ": canceling a manual target change retains its keyboard selection");
                _pendingApply = null;
                check(!_root.GetLogicalDescendants().OfType<Control>().Any(control => control.Name is "KnobStep" or "KnobCycleEnabled" or "KnobCycleOptions"),
                    id + ": manual mode never displays step, long-press or cycle-order settings");
                await SetKnobModeAsync(false);
                check(EditorState.Fingerprint(_state.Project) == viewFingerprint && !_state.Dirty && !_state.CanUndo,
                    id + ": switching knob modes changes only the view");
                var shortPress = Find<ComboBox>("KnobShortPressChoice");
                check(Find<ComboBox>("KnobShortPressMode").Items.Cast<string>().SequenceEqual(new[] { L.T("Für alle Funktionen gleich"), L.T("Je Funktion") }),
                    id + ": both short-press modes use the active interface language");
                check(shortPress.Items.Cast<BindingRow>().Any(row => row.Code == "KC_NO") && shortPress.Items.Cast<BindingRow>().Any(row => row.Code == "MO(1)") &&
                    shortPress.SelectedItem is BindingRow currentPress && currentPress.Code == _state.Layer.Keys[press].Binding,
                    id + ": presets show an inline short-press picker including disabled and layer actions with its actual assignment selected");
                var shortPressExpected = ProjectService.Clone(_state.Project); shortPressExpected.Layers[0].Keys[press].Binding = "KC_MPLY";
                shortPress.SelectedItem = shortPress.Items.Cast<BindingRow>().Single(row => row.Code == "KC_MPLY");
                check(HasPresets() && _state.SelectedKeys.SetEquals([press]) && EditorState.Fingerprint(_state.Project) == EditorState.Fingerprint(shortPressExpected),
                    id + ": changing the inline short press changes only that physical binding and preserves the presets view");
                check(await SetKnobShortPressAsync("KC_MPLY") && !await SetKnobShortPressAsync("INVALID_BINDING"),
                    id + ": inline short press accepts a no-op and rejects unknown functions");
                _state.Undo(); Build();
                check(EditorState.Fingerprint(_state.Project) == viewFingerprint && !_state.CanUndo,
                    id + ": inline assignment and a repeated selection undo exactly once");
                _pendingApply = () => true; _offlinePromptChoice = 0;
                check(!await SetKnobShortPressAsync("KC_MPLY") && EditorState.Fingerprint(_state.Project) == viewFingerprint,
                    id + ": canceled pending input blocks the inline short-press assignment");
                _pendingApply = null;
                CommitCandidate(project => project.Layers[0].KnobCycleEnabled = true); Build();
                var enabledFingerprint = EditorState.Fingerprint(_state.Project);
                var unsafePress = Find<ComboBox>("KnobShortPressChoice");
                unsafePress.SelectedItem = unsafePress.Items.Cast<BindingRow>().Single(row => row.Code == "MO(1)");
                check(EditorState.Fingerprint(_state.Project) == enabledFingerprint && _state.Layer.KnobCycleEnabled &&
                    unsafePress.SelectedItem is BindingRow restoredPress && restoredPress.Code == _state.Layer.Keys[press].Binding,
                    id + ": an unsafe inline Fn assignment is rejected and its selection restored without disabling long press");
                _state.Undo(); Build();
                CommitCandidate(project =>
                {
                    project.Macros.Add(new() { Id = "knob_fixture", Name = "Custom press", Steps = [new() { Kind = "down", Key = "KC_A" }, new() { Kind = "up", Key = "KC_A" }] });
                    project.Layers[0].Keys[press].Binding = "MACRO:knob_fixture";
                }); Build();
                check(Find<ComboBox>("KnobShortPressChoice").SelectedItem is BindingRow macroPress && macroPress.Code == "MACRO:knob_fixture" && macroPress.Label == "Custom press",
                    id + ": the inline picker preserves an existing custom macro and its user-visible name");
                _state.Undo(); Build();
                var perFunctionStart = EditorState.Fingerprint(_state.Project);
                var selectedBeforePressMode = _state.SelectedKeys.ToArray();
                var rotationBeforePressMode = KnobPresets.Find(_state.Layer)?.Id;
                check(await SetKnobShortPressModeAsync(true) && _state.Layer.KnobPressByFunction && _state.Layer.KnobPressBindings.Count == 0,
                    id + ": per-function short presses start by inheriting the general press with no automatic assignment changes");
                var enabledMode = EditorState.Fingerprint(_state.Project);
                check(await SetKnobShortPressTargetAsync(2) && EditorState.Fingerprint(_state.Project) == enabledMode &&
                    _state.SelectedKeys.SetEquals(selectedBeforePressMode) && KnobPresets.Find(_state.Layer)?.Id == rotationBeforePressMode,
                    id + ": choosing the short-press function changes only the editing target, not the rotation preset or selected key");
                check(Find<ComboBox>("KnobShortPressChoice").SelectedItem is BindingRow inheritedPress && inheritedPress.Code == "KC_TRNS" &&
                    inheritedPress.Label == L.T("Wie allgemeiner kurzer Druck"),
                    id + ": a missing function override clearly inherits the general short press");
                check(await SetKnobShortPressAsync("KC_MPLY") && _state.Layer.KnobPressBindings.GetValueOrDefault(2) == "KC_MPLY" &&
                    _state.Layer.Keys[press].Binding == ProjectService.Create(id).Layers[0].Keys[press].Binding,
                    id + ": changing a per-function press updates only that override and leaves the general physical press intact");
                check(!await SetKnobShortPressAsync("MO(1)") && !await SetKnobShortPressAsync("KC_LCTL") &&
                    !Find<ComboBox>("KnobShortPressChoice").Items.Cast<BindingRow>().Any(row => row.Code is "MO(1)" or "KC_LCTL"),
                    id + ": per-function press options exclude held modifiers and layer switches");
                check(await SetKnobShortPressAsync("KC_TRNS") && !_state.Layer.KnobPressBindings.ContainsKey(2),
                    id + ": choosing the general short press removes the override instead of disabling the key");
                _state.Undo(); Build();
                check(await SetKnobShortPressModeAsync(false) && _state.Layer.KnobPressBindings.GetValueOrDefault(2) == "KC_MPLY",
                    id + ": turning per-function mode off retains its saved assignments");
                check(await SetKnobShortPressModeAsync(true) && _state.Layer.KnobPressBindings.GetValueOrDefault(2) == "KC_MPLY",
                    id + ": turning per-function mode back on restores the configured override");
                while (_state.CanUndo) _state.Undo(); Build();
                check(EditorState.Fingerprint(_state.Project) == perFunctionStart && !_state.Dirty,
                    id + ": all per-function changes can be undone to the exact original clean profile");
                check(Find<ComboBox>("KnobPresetChoice").Items.Count == KnobPresets.All.Count && Find<CheckBox>("KnobCycleEnabled").IsChecked == false,
                    id + ": preset menu includes all available presets with long press opt-in and no custom no-op");
                _state.SelectKey("encoder:ccw", false); Build();
                check(HasPresets(), id + ": selecting one rotation offers presets");
                _state.SelectKey("encoder:cw", true); await SetKnobModeAsync(true);
                var bothSummary = Find<TextBlock>("CurrentKeyBindingSummary").Text ?? "";
                check(bothSummary.Contains(L.T("Drehen"), StringComparison.Ordinal) && !bothSummary.Contains(L.T("Drücken"), StringComparison.Ordinal) &&
                    Find<Button>("KnobManualTargetLeft").Background?.ToString() == Find<Button>("KnobManualTargetRight").Background?.ToString() &&
                    Find<Button>("KnobManualTargetPress").Background?.ToString() != Find<Button>("KnobManualTargetLeft").Background?.ToString(),
                    id + ": selecting both rotations describes turning and highlights both directions, never the knob press");
                _state.SelectKey("encoder:ccw", false); await SetKnobModeAsync(false);
                _state.SelectKey(press, true); Build(); var before = EditorState.Fingerprint(_state.Project);
                await AssignKnobPresetAsync("brightness");
                check(!HasPresets() && EditorState.Fingerprint(_state.Project) == before && !_state.CanUndo,
                    id + ": mixed key and rotation selections never change both directions");
                _state.SelectKey("encoder:ccw", false); _state.SelectKey("encoder:cw", true); _state.LayerIndex = 2; Build();
                var expected = ProjectService.Clone(_state.Project);
                expected.Layers[2].Keys["encoder:ccw"].Binding = "RM_VALD";
                expected.Layers[2].Keys["encoder:cw"].Binding = "RM_VALU";
                before = EditorState.Fingerprint(_state.Project); await AssignKnobPresetAsync("brightness");
                check(EditorState.Fingerprint(_state.Project) == EditorState.Fingerprint(expected) && _state.CanUndo && _state.Dirty,
                    id + ": preset changes only both rotation bindings on the current layer, preserving colors, knob press and other settings");
                await AssignKnobPresetAsync("brightness"); _state.Undo();
                check(EditorState.Fingerprint(_state.Project) == before && !_state.CanUndo,
                    id + ": the complete preset undoes once, including after a repeated selection");
                _state.SelectKey("encoder:cw", false); Build();
                await SetKnobModeAsync(true);
                Find<ComboBox>("BindingCategory").SelectedIndex = 1; Find<TextBox>("BindingSearch").Text = "KC_VOL";
                await AssignKnobPresetAsync("brightness"); UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); UpdateLayout();
                var bindings = Find<ListBox>("BindingChoices");
                check((bindings.SelectedItem as BindingRow)?.Code == "RM_VALU" && Find<ComboBox>("BindingCategory").SelectedIndex == 0 &&
                    Find<TextBox>("BindingSearch").Text == "" && bindings.GetVisualDescendants().OfType<ScrollViewer>().Any(scroll => scroll.Offset.Y > 0),
                    id + ": preset reveals and scrolls to the assigned brightness function, clearing stale filters");
                Find<ComboBox>("BindingCategory").SelectedIndex = 2;
                check(bindings.Items.Cast<BindingRow>().Select(row => row.Code).ToHashSet().SetEquals(["RM_VALU", "RM_VALD"]),
                    id + ": lighting category offers exactly the supported brightness functions");
                Find<TextBox>("BindingSearch").Text = L.T("Beleuchtung heller");
                // Avalonia posts TextChanged; wait for the editor's real filter handler.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                check(bindings.Items.Count == 1 && ((BindingRow)bindings.Items[0]!).Code == "RM_VALU", id + ": translated brightness labels are searchable");
                Find<ComboBox>("BindingCategory").SelectedIndex = 0; Find<TextBox>("BindingSearch").Text = "RM_";
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                check(bindings.Items.Count == 2, id + ": unsupported effect bindings stay hidden");
                _state.Undo(); Build(); before = EditorState.Fingerprint(_state.Project);
                var pendingCalls = 0; _pendingApply = () => { pendingCalls++; return true; }; _offlinePromptChoice = 0;
                await AssignKnobPresetAsync("brightness");
                check(_pendingApply is not null && pendingCalls == 0 && EditorState.Fingerprint(_state.Project) == before && !_state.CanUndo,
                    id + ": canceling pending edits retains input without changing knob bindings");
                _pendingApply = null; _startupLoading = true; await AssignKnobPresetAsync("brightness"); _startupLoading = false;
                check(EditorState.Fingerprint(_state.Project) == before && !_state.CanUndo, id + ": startup loading blocks knob assignment");

                _state.SelectKey(press, false); _state.LayerIndex = 0; Build();
                await SetKnobModeAsync(false);
                foreach (var preset in KnobPresets.All)
                {
                    before = EditorState.Fingerprint(_state.Project);
                    expected = ProjectService.Clone(_state.Project); KnobPresets.Apply(expected.Layers[0], preset.Id);
                    await AssignKnobPresetAsync(preset.Id);
                    check(EditorState.Fingerprint(_state.Project) == EditorState.Fingerprint(expected) &&
                        Find<ComboBox>("KnobPresetChoice").SelectedIndex == KnobPresets.All.ToList().IndexOf(preset),
                        id + ": " + preset.Id + " preset applies from knob press and displays its selected label");
                    if (EditorState.Fingerprint(_state.Project) != before) _state.Undo();
                    check(EditorState.Fingerprint(_state.Project) == before, id + ": undo preserves the entire profile after " + preset.Id);
                    Build();
                }
                before = EditorState.Fingerprint(_state.Project);
                expected = ProjectService.Clone(_state.Project); expected.Layers[0].KnobCycleEnabled = true;
                expected.Layers[0].KnobCycleMacZoom = OsActionCatalog.CurrentSystem == "macos";
                await SetKnobCycleAsync(true);
                check(EditorState.Fingerprint(_state.Project) == EditorState.Fingerprint(expected) && Find<CheckBox>("KnobCycleEnabled").IsChecked == true &&
                    Find<TextBlock>("KnobCycleHint").IsVisible,
                    id + ": enabling long press changes only this layer's opt-in and shows the short-press hint");
                await SetKnobCycleAsync(true); _state.Undo();
                check(EditorState.Fingerprint(_state.Project) == before && !_state.CanUndo, id + ": repeated long-press enable still undoes once");
                _state.Layer.Keys["encoder:ccw"].Binding = "KC_LEFT";
                _state.Layer.Keys["encoder:cw"].Binding = "KC_RGHT";
                _knobModeState = null;
                Build(); before = EditorState.Fingerprint(_state.Project);
                check(!HasPresets() && Find<TextBox>("BindingSearch") is not null, id + ": a custom rotation pair initially opens manual mode");
                await SetKnobModeAsync(false);
                check(Find<ComboBox>("KnobPresetChoice").SelectedIndex == 0 && Find<ComboBox>("KnobPresetChoice").Items.Count == KnobPresets.All.Count + 1 &&
                    Find<CheckBox>("KnobCycleEnabled").IsEnabled,
                    id + ": custom directions remain visibly custom and can join the long-press cycle");
                await SetKnobCycleAsync(true);
                check(_state.Layer.Keys["encoder:ccw"].Binding == "KC_LEFT" && _state.Layer.Keys["encoder:cw"].Binding == "KC_RGHT",
                    id + ": enabling long press never overwrites custom directions");
                _state.Undo(); Build();
                check(EditorState.Fingerprint(_state.Project) == before, id + ": disabling via undo restores custom rotation settings exactly");
                _state.Layer.Keys[press].Binding = "MO(1)"; Build(); before = EditorState.Fingerprint(_state.Project);
                await SetKnobCycleAsync(true);
                check(!Find<CheckBox>("KnobCycleEnabled").IsEnabled && Find<TextBlock>("KnobCycleHint").IsVisible &&
                    EditorState.Fingerprint(_state.Project) == before,
                    id + ": Fn knob press blocks long-press cycling with an explanation and no mutation");
                _state.Layer.Keys[press].Binding = "KC_MUTE"; Build();
                await AssignKnobPresetAsync("zoom-mac"); await SetKnobCycleAsync(true);
                check(_state.Layer.KnobCycleMacZoom && KnobPresets.Find(_state.Layer)?.Id == "zoom-mac",
                    id + ": explicit macOS zoom selects command-based cycling regardless of host OS");
                await AssignKnobPresetAsync("scroll");
                check(_state.Layer.KnobCycleMacZoom && KnobPresets.Find(_state.Layer)?.Id == "scroll",
                    id + ": switching to scrolling retains the selected macOS zoom variant for long press");
                await AssignKnobPresetAsync("zoom");
                check(!_state.Layer.KnobCycleMacZoom && KnobPresets.Find(_state.Layer)?.Id == "zoom",
                    id + ": Windows/Linux zoom switches the long-press cycle to Control zoom");
                await AssignKnobPresetAsync("zoom-mac"); await SetKnobCycleAsync(false);
                check(!_state.Layer.KnobCycleEnabled && !_state.Layer.KnobCycleMacZoom,
                    id + ": disabling long press clears its platform flag while preserving normal assignments");
                before = EditorState.Fingerprint(_state.Project); expected = ProjectService.Clone(_state.Project);
                expected.Layers[0].KnobStep = 4;
                await SetKnobStepAsync(4); await SetKnobStepAsync(4);
                check(EditorState.Fingerprint(_state.Project) == EditorState.Fingerprint(expected) && Find<ComboBox>("KnobStep").SelectedIndex == 3,
                    id + ": step selector changes only this layer's step and displays the saved value");
                _state.Undo(); Build();
                check(EditorState.Fingerprint(_state.Project) == before, id + ": changing step is one undo even after repeated selection");
                check(!await SetKnobStepAsync(0) && !await SetKnobStepAsync(6) && EditorState.Fingerprint(_state.Project) == before,
                    id + ": invalid step sizes cannot alter the editor");
                _pendingApply = () => true; _offlinePromptChoice = 0;
                check(!await SetKnobStepAsync(2) && _pendingApply is not null && EditorState.Fingerprint(_state.Project) == before,
                    id + ": canceling pending edits also rejects the step change");
                _pendingApply = null;

                await AssignKnobPresetAsync("undo-mac"); await SetKnobCycleAsync(true);
                check(_state.Layer.KnobCycleMacZoom && Find<ComboBox>("KnobUndoLayout").Items.Count == 3,
                    id + ": macOS undo uses command shortcuts and offers all three undo keyboard layouts");
                before = EditorState.Fingerprint(_state.Project); await SetKnobUndoLayoutAsync(2);
                check(_state.Layer.KnobUndoLayout == 2 && Find<ComboBox>("KnobUndoLayout").SelectedIndex == 2,
                    id + ": explicit AZERTY undo layout is saved and reflected by the editor");
                if (EditorState.Fingerprint(_state.Project) != before) _state.Undo();
                Build();

                var dialog = CreateKnobCycleOptionsWindow(_state.Layer);
                var allFunctions = ProjectService.Clone(_state.Project).Layers[0]; allFunctions.KnobCycleMask = 255;
                var compact = CreateKnobCycleOptionsWindow(allFunctions); compact.Show(this);
                try
                {
                ConstrainKnobDialog(compact, 1280 / 1.5, 720 / 1.5);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); compact.UpdateLayout();
                var compactGrid = (Grid)compact.Content!;
                var compactScroll = compact.GetLogicalDescendants().OfType<ScrollViewer>().Single(control => control.Name == "KnobCycleScroll");
                var compactFooter = compact.GetLogicalDescendants().OfType<StackPanel>().Single(control => control.Name == "KnobCycleFooter");
                check(compact.MaxHeight <= 720 / 1.5 - 48 && compactScroll.Bounds.Height > 0 && compactScroll.Extent.Height > compactScroll.Viewport.Height &&
                    compactFooter.Bounds.Bottom <= compactGrid.Bounds.Height && compactFooter.Bounds.Top >= compactScroll.Bounds.Bottom &&
                    compact.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "KnobCycleUndoLayout").IsVisible,
                    id + $": all eight functions and undo layout scroll inside a compact high-DPI work area while footer remains outside and visible (max={compact.MaxHeight}, scroll={compactScroll.Bounds}, extent={compactScroll.Extent}, viewport={compactScroll.Viewport}, footer={compactFooter.Bounds}, grid={compactGrid.Bounds})");
                var orderedPanel = compact.GetLogicalDescendants().OfType<StackPanel>().Single(control => control.Name == "KnobCycleOrderedRows");
                var dragRows = orderedPanel.Children.OfType<Grid>().ToArray();
                var draggedId = (int)dragRows[0].Tag!;
                var handle = compact.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "KnobCycleDrag" + draggedId);
                var dragFrom = handle.TranslatePoint(new Point(12, 12), compact)!.Value;
                var dragTo = dragRows[1].TranslatePoint(new Point(24, dragRows[1].Bounds.Height / 2), compact)!.Value;
                var pointer = new Pointer(777, PointerType.Mouse, true);
                var held = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
                handle.RaiseEvent(new PointerPressedEventArgs(handle, pointer, compact, dragFrom, 1, held, KeyModifiers.None, 1));
                handle.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, handle, pointer, compact, dragTo, 2,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                check(orderedPanel.Children.OfType<Grid>().Select(row => (int)row.Tag!).SequenceEqual(dragRows.Select(row => (int)row.Tag!)) &&
                    dragRows[1].Background?.ToString() == "#ff4a2931",
                    id + ": pointer dragging previews its target without mutating the row order before release");
                handle.RaiseEvent(new PointerReleasedEventArgs(handle, pointer, compact, dragTo, 3,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                check((int)orderedPanel.Children.OfType<Grid>().ElementAt(1).Tag! == draggedId && pointer.Captured is null,
                    id + ": releasing a real pointer drag reorders the function and releases capture");
                compact.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "KnobCycleUp" + draggedId)
                    .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                check(orderedPanel.Children.OfType<Grid>().Select(row => (int)row.Tag!).SequenceEqual(dragRows.Select(row => (int)row.Tag!)),
                    id + ": accessible arrow buttons remain usable after pointer drag");
                }
                finally { compact.Close(); }
                var sparseLayer = ProjectService.Clone(_state.Project).Layers[0]; sparseLayer.KnobCycleMask = 1 | 8 | 64;
                var sparseSelected = KnobPresets.OrderedCycleOptions(sparseLayer).Where(option => (sparseLayer.KnobCycleMask & option.Bit) != 0).ToArray();
                check(CycleOrderForSelection(sparseSelected, sparseLayer).SequenceEqual(sparseLayer.KnobCycleOrder),
                    id + ": confirming unchanged functions preserves interspersed inactive order entries");
                Grid[] OrderedRows() => dialog.GetLogicalDescendants().OfType<Grid>().Where(row => row.Name?.StartsWith("KnobCycleRow", StringComparison.Ordinal) == true).ToArray();
                Button DialogButton(string name) => dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == name);
                var confirm = dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ConfirmKnobCycleSelection");
                var layoutChoice = dialog.GetLogicalDescendants().OfType<ComboBox>().Single(choice => choice.Name == "KnobCycleUndoLayout");
                check(OrderedRows().Select(row => (int)row.Tag!).SequenceEqual(KnobPresets.OrderedCycleOptions(_state.Layer).Where(option => (_state.Layer.KnobCycleMask & option.Bit) != 0).Select(option => option.Bit)),
                    id + ": cycle dialog displays active functions in their saved order");
                DialogButton("KnobCycleDown1").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                check((int)OrderedRows()[1].Tag! == 1, id + ": moving a cycle function down changes its displayed order");
                DialogButton("KnobCycleUp1").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                check((int)OrderedRows()[0].Tag! == 1, id + ": moving it up restores its order");
                foreach (var option in OrderedRows()) DialogButton("KnobCycleRemove" + option.Tag).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                check(!confirm.IsEnabled && !layoutChoice.IsVisible, id + ": empty cycle cannot be confirmed and unused undo layout stays hidden");
                var available = dialog.GetLogicalDescendants().OfType<ComboBox>().Single(choice => choice.Name == "KnobCycleAddChoice");
                available.SelectedItem = available.Items.Cast<KnobCycleOption>().Single(option => option.Bit == 128);
                DialogButton("AddKnobCycleFunction").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                check(confirm.IsEnabled && layoutChoice.IsVisible, id + ": undo selection enables confirmation and reveals its layout choice");
                before = EditorState.Fingerprint(_state.Project);
                check(!await EditKnobCycleOptionsCoreAsync(() => Task.FromResult<KnobCycleSelection?>(null)) && EditorState.Fingerprint(_state.Project) == before,
                    id + ": canceling the cycle dialog leaves the entire profile unchanged");
                check(!await EditKnobCycleOptionsCoreAsync(() => Task.FromResult<KnobCycleSelection?>(new(0, false, 0))) &&
                    !await EditKnobCycleOptionsCoreAsync(() => Task.FromResult<KnobCycleSelection?>(new(256, false, 0))) &&
                    !await EditKnobCycleOptionsCoreAsync(() => Task.FromResult<KnobCycleSelection?>(new(1, false, 3))) && EditorState.Fingerprint(_state.Project) == before,
                    id + ": invalid cycle masks and undo layouts are rejected without partial changes");
                check(!await EditKnobCycleOptionsCoreAsync(() => Task.FromResult<KnobCycleSelection?>(new(31, false, 0, [1, 2, 4, 8, 16, 32, 64, 64]))) &&
                    !await EditKnobCycleOptionsCoreAsync(() => Task.FromResult<KnobCycleSelection?>(new(31, false, 0, [1, 2]))) && EditorState.Fingerprint(_state.Project) == before,
                    id + ": malformed cycle orders leave the complete project untouched");
                expected = ProjectService.Clone(_state.Project); expected.Layers[0].KnobCycleMask = 2 | 32 | 128;
                expected.Layers[0].KnobCycleMacZoom = false; expected.Layers[0].KnobUndoLayout = 1;
                int[] cycleOrder = [128, 2, 32, 1, 4, 8, 16, 64]; expected.Layers[0].KnobCycleOrder = cycleOrder.ToList();
                check(await EditKnobCycleOptionsCoreAsync(() => Task.FromResult<KnobCycleSelection?>(new(2 | 32 | 128, false, 1, cycleOrder))) &&
                    EditorState.Fingerprint(_state.Project) == EditorState.Fingerprint(expected) &&
                    Find<TextBlock>("KnobCycleSummary").Text == KnobPresets.CycleSummary(_state.Layer),
                    id + ": cycle subset, order, operating system and undo layout apply together and update the summary");
                await EditKnobCycleOptionsCoreAsync(() => Task.FromResult<KnobCycleSelection?>(new(2 | 32 | 128, false, 1, cycleOrder)));
                _state.Undo(); Build();
                check(EditorState.Fingerprint(_state.Project) == before, id + ": all cycle-dialog settings undo together once");
                check(!await EditKnobCycleOptionsCoreAsync(() => { _state.LayerIndex = 1; return Task.FromResult<KnobCycleSelection?>(new(1, false, 0)); }) &&
                    EditorState.Fingerprint(_state.Project) == before,
                    id + ": a layer switch while the cycle dialog is open invalidates its result");
                _state.LayerIndex = 0; Build();
            }
        }
        finally
        {
            _state = savedState; _page = savedPage; _editingSlider = savedSlider; _busy = savedBusy; _startupLoading = savedLoading;
            _initialConnectionsPending = savedInitial; _navigating = savedNavigating; _offlinePromptChoice = savedPrompt;
            Build(); _pendingApply = savedPending;
        }
    }

    private async Task RunKnobStatusChecksAsync(Action<bool, string> check)
    {
        var savedState = _state; var savedPage = _page; var savedSlider = _editingSlider;
        var savedBusy = _busy; var savedLoading = _startupLoading; var savedInitial = _initialConnectionsPending;
        var savedPending = _pendingApply; var savedNavigating = _navigating;
        var savedConnections = _connectedDeviceIds; var savedFailed = _connectionsFailed;
        var savedFind = _findKnobDevices; var savedRead = _readKnobStatus; var savedStatusText = _status;
        try
        {
            _state = EditorState.FromDevice(ProjectService.Create("numpad")); _page = "Tastenbelegung";
            _state.SelectKey("encoder:ccw", false); _editingSlider = false;
            _busy = _startupLoading = _initialConnectionsPending = _navigating = _connectionsFailed = false; _pendingApply = null;
            _connectedDeviceIds = ["numpad"]; ResetKnobStatus(); Build(); UpdateLayout();
            var controls = _editor.Children.ToArray(); var bounds = controls.Select(control => control.Bounds).ToArray();
            bool StillEditor() { UpdateLayout(); return _editor.Children.SequenceEqual(controls) && controls.Select(control => control.Bounds).SequenceEqual(bounds); }
            var button = _keyButtons["encoder:ccw"]; var tooltip = (TextBlock)ToolTip.GetTip(button)!;
            var baseline = tooltip.Text!;
            string OpenTip() { button.RaiseEvent(new CancelRoutedEventArgs(ToolTip.ToolTipOpeningEvent)); return tooltip.Text!; }
            bool QuietStatus() => _knobStatus is null && OpenTip() == baseline;
            check(!_editor.GetLogicalDescendants().OfType<Control>().Any(control => control.Name is "KnobRuntimeStatus" or "KnobRuntimeCard"),
                "knob editor has no runtime status row or reserved status card");
            check(QuietStatus() && baseline.Contains(DisplayBinding(_state.Layer.Keys["encoder:ccw"].Binding), StringComparison.Ordinal),
                "initial tooltip retains the normal key label and assigned binding without unavailable text");
            var fixtureState = _state; var initial = EditorState.Fingerprint(_state.Project); var reads = 0;
            var device = new OnboardDevice("numpad", "Fixture", "offline-knob-fixture");
            _findKnobDevices = () => Task.FromResult<IReadOnlyList<OnboardDevice>>([device]);
            var snapshot = new OnboardKnobStatus("numpad", 2, 2, "brightness", true, false, 0x7806, 0x7805, 1, 4, 123, 2, 2, 1);
            _readKnobStatus = (_, _) => { reads++; return Task.FromResult<OnboardKnobStatus?>(snapshot); };
            Status("unchanged");
            await RefreshKnobStatusAsync();
            check(reads == 0, "runtime polling never accesses hardware from offline mode without an injected-fixture opt-in");
            await RefreshKnobStatusAsync(true);
            check(reads == 1 && tooltip.Text == baseline && StillEditor() && _status == "unchanged",
                "confirmed status reads do not mutate the tooltip, sidebar or footer");
            var firstTip = OpenTip();
            check(firstTip.StartsWith(baseline + "\n\n", StringComparison.Ordinal) && firstTip.Contains(L.F($"Auf der Tastatur aktiv: {snapshot.Label}"), StringComparison.Ordinal) &&
                firstTip.Contains(L.F($"Ebene {2}"), StringComparison.Ordinal) && _state.LayerIndex == 0,
                "opening the knob tooltip adds confirmed runtime function and layer without replacing the normal hint or using the edited layer");
            snapshot = snapshot with { ChangeCounter = 2, ActivePresetBit = 4, PresetId = "tracks" };
            await RefreshKnobStatusAsync(true);
            check(_status == L.F($"Drehregler: {snapshot.Label}") && tooltip.Text == firstTip && StillEditor(),
                "a real counter change shows a footer notice without rewriting the open tooltip or moving controls");
            check(OpenTip().Contains(L.F($"Auf der Tastatur aktiv: {snapshot.Label}"), StringComparison.Ordinal),
                "reopening the tooltip picks up the newest confirmed function");
            var firstNotice = _knobNoticeRevision;
            snapshot = snapshot with { ChangeCounter = 3, ActivePresetBit = 1, PresetId = "volume" };
            await RefreshKnobStatusAsync(true);
            ExpireKnobChangeNotice(firstNotice);
            check(_status == L.F($"Drehregler: {snapshot.Label}"), "an older notice timeout cannot clear a newer function change");
            ExpireKnobChangeNotice(_knobNoticeRevision);
            check(_status == "unchanged", "consecutive knob notices restore the original footer when the latest notice expires");
            snapshot = snapshot with { ChangeCounter = 4 }; await RefreshKnobStatusAsync(true);
            Status("newer operation result"); ExpireKnobChangeNotice(_knobNoticeRevision);
            check(_status == "newer operation result", "notice expiry never overwrites a newer operation result");
            Status("after change"); await RefreshKnobStatusAsync(true);
            check(_status == "after change", "unchanged hardware status does not repeat the change feedback");
            _knobStatusReadAt = DateTime.UtcNow.AddSeconds(-4);
            check(OpenTip() == baseline && StillEditor(), "expired snapshots fall back to the normal tooltip without unavailable messages or layout changes");
            await RefreshKnobStatusAsync(true);
            snapshot = snapshot with { Generation = 5, ChangeCounter = 0 };
            await RefreshKnobStatusAsync(true);
            check(_status == "after change", "a new configuration generation establishes a baseline without claiming a function change");
            device = device with { Path = "second-offline-knob-fixture" }; snapshot = snapshot with { ChangeCounter = 9 };
            await RefreshKnobStatusAsync(true);
            check(_status == "after change", "a replacement device path never generates a false mode-change message");
            var confirmedTip = OpenTip();
            _findKnobDevices = () => Task.FromResult<IReadOnlyList<OnboardDevice>>([device, device with { Path = "ambiguous" }]);
            var readBefore = reads; await RefreshKnobStatusAsync(true);
            check(reads == readBefore && tooltip.Text == confirmedTip && QuietStatus() && _knobStatusNextRead > DateTime.UtcNow && StillEditor() && _status == "after change",
                "ambiguous identity clears cached status silently; the open tip stays still and the next opening shows its normal hint");
            _findKnobDevices = () => Task.FromResult<IReadOnlyList<OnboardDevice>>([device]);
            _knobStatusNextRead = default;
            _readKnobStatus = (_, _) => Task.FromResult<OnboardKnobStatus?>(null);
            await RefreshKnobStatusAsync(true);
            check(QuietStatus() && _knobStatusNextRead > DateTime.UtcNow.AddSeconds(10) && StillEditor() && _status == "after change",
                "unsupported status uses a longer retry interval without adding UI content");
            _knobStatusNextRead = default;
            _readKnobStatus = (_, _) => { reads++; return Task.FromResult<OnboardKnobStatus?>(snapshot); };
            await RefreshKnobStatusAsync(true);
            check(_knobStatus == snapshot && _status == "after change" && OpenTip() != baseline,
                "confirmed recovery makes runtime data available on demand without a false announcement");
            _readKnobStatus = (_, _) => { reads++; throw new IOException("Injected status read failure."); };
            await RefreshKnobStatusAsync(true);
            check(QuietStatus() && StillEditor() && _knobStatusNextRead > DateTime.UtcNow && _status == "after change",
                "failed reads leave the sidebar and footer untouched and use the normal tooltip");
            readBefore = reads;
            for (var retry = 0; retry < 3; retry++) await RefreshKnobStatusAsync(true);
            check(reads == readBefore && QuietStatus(), "repeated polls during error backoff stay quiet and do not hammer the device");
            _knobStatusNextRead = default;
            _readKnobStatus = (_, _) => { reads++; return Task.FromResult<OnboardKnobStatus?>(snapshot); };
            await RefreshKnobStatusAsync(true);
            check(_knobStatus == snapshot && StillEditor() && _status == "after change", "a later successful read recovers without changing the editor layout");
            var savedRevision = _connectionRevision; _connectionRevision++;
            check(OpenTip() == baseline, "a connection revision change invalidates the previous tooltip snapshot immediately");
            _connectionRevision = savedRevision;
            readBefore = reads; _connectedDeviceIds = []; _knobStatusNextRead = DateTime.UtcNow.AddMinutes(1);
            await RefreshKnobStatusAsync(true);
            check(reads == readBefore && QuietStatus() && StillEditor(), "disconnect invalidates cached data even during backoff without showing status filler");
            _connectedDeviceIds = ["numpad"]; _knobStatusNextRead = default;
            var completion = new TaskCompletionSource<OnboardKnobStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _readKnobStatus = (_, _) => { reads++; return completion.Task; };
            var pendingRead = RefreshKnobStatusAsync(true); _busy = true; completion.SetResult(snapshot); await pendingRead;
            check(QuietStatus(), "starting a device operation while status is pending discards the previous response");
            var busyReads = reads; await RefreshKnobStatusAsync(true);
            check(reads == busyReads, "busy operations prevent background knob status polls");
            _busy = false;
            _findKnobDevices = () => new TaskCompletionSource<IReadOnlyList<OnboardDevice>>().Task;
            await RefreshKnobStatusAsync(true);
            check(!_knobStatusReading && QuietStatus() && _knobStatusNextRead > DateTime.UtcNow && _status == "after change",
                "stalled discovery is bounded by the whole-call deadline and permits a later retry");
            _findKnobDevices = () => Task.FromResult<IReadOnlyList<OnboardDevice>>([device]); _knobStatusNextRead = default;
            _readKnobStatus = (_, _) => { _state = EditorState.FromDevice(ProjectService.Create("pro-iso")); return Task.FromResult<OnboardKnobStatus?>(snapshot); };
            await RefreshKnobStatusAsync(true);
            check(_knobStatus is null && OpenTip() == baseline, "switching devices during a read discards the response and prevents its use by an old control");
            check(EditorState.Fingerprint(fixtureState.Project) == initial && !fixtureState.Dirty && !fixtureState.CanUndo,
                "runtime feedback never mutates the editor profile, dirty flag or undo history");
        }
        finally
        {
            _knobNoticeTimer?.Dispose(); _knobNoticeTimer = null; _knobNoticePrevious = null;
            _state = savedState; _page = savedPage; _editingSlider = savedSlider; _busy = savedBusy; _startupLoading = savedLoading;
            _initialConnectionsPending = savedInitial; _pendingApply = null; _navigating = savedNavigating;
            _connectedDeviceIds = savedConnections; _connectionsFailed = savedFailed; _findKnobDevices = savedFind; _readKnobStatus = savedRead;
            ResetKnobStatus(); Build(); _pendingApply = savedPending; Status(savedStatusText);
        }
    }
}
