using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace InGlorious.Core;

public partial class MainWindow
{
    // Invoked only by --ui-smoke. Uses local draft handlers; no modal dialog, input hook or device API.
    private void RunExperimentalModelChecks(Action<bool, string> check)
    {
        var devicesBefore = _devices;
        try
        {
        foreach (var model in DeviceCatalog.All.Where(d => d.IsExperimentalFirmware))
        {
            SetOfflineConnectionFixture([]);
            ResetWorkflowFixture(model.Id); SetTab(0); UpdateLayout();
            check(ExperimentalDeviceLabel.IsVisible && ExperimentalDeviceLabel.Text == L.T("Experimentell") && DeviceButtons.Children.Count == 0, model.Id + ": disconnected profile keeps its experimental label without inventing a connected device");
            check(_keyButtons.Keys.Count(id => id.StartsWith("encoder:", StringComparison.Ordinal)) == (model.HasEncoder ? 2 : 0), model.Id + ": rotation controls exist only for physical encoders");
            if (Device.BadgeGeometry is { } geometry)
            {
                const double unit = 51, inset = 32;
                var topInset = Device.PreviewTopInset * unit;
                var badgeVisual = KeyboardCanvas.Children.OfType<FrameworkElement>().Single(element => element.Name == (geometry.LedIndex is int led ? "BadgeLed" + led : "PassiveBadge"));
                var badgeBounds = new Rect(Canvas.GetLeft(badgeVisual), Canvas.GetTop(badgeVisual), badgeVisual.Width, badgeVisual.Height);
                check(Math.Abs(badgeBounds.X - (inset + geometry.X * unit)) < .001 && Math.Abs(badgeBounds.Y - (topInset + geometry.Y * unit)) < .001 && Math.Abs(badgeBounds.Width - (geometry.W * unit - 4)) < .001 && Math.Abs(badgeBounds.Height - (geometry.H * unit - 4)) < .001, model.Id + ": physical badge uses its reviewed position and proportions");
                check(badgeBounds.Top >= 0 && badgeBounds.Bottom <= KeyboardCanvas.Height && badgeBounds.Right <= KeyboardCanvas.Width, model.Id + ": badge including negative source Y is inside the preview canvas");
                check(Device.Keys.Where(key => !key.IsEncoder).All(key => !badgeBounds.IntersectsWith(new Rect(Canvas.GetLeft(_keyButtons[key.Id]), Canvas.GetTop(_keyButtons[key.Id]), _keyButtons[key.Id].Width, _keyButtons[key.Id].Height))), model.Id + ": physical badge does not cover any key or rotary knob");
                check(Device.Keys.All(key => Math.Abs(Canvas.GetTop(_keyButtons[key.Id]) - (topInset + key.Y * unit)) < .001), model.Id + ": preview top inset moves every key consistently");
                var sides = KeyboardCanvas.Children.OfType<UniformGrid>().ToArray();
                check(sides.Length == 2 && sides.All(side => Math.Abs(Canvas.GetTop(side) - (topInset + 4)) < .001) && Math.Abs(Canvas.GetLeft(sides[1]) - (inset + Device.PreviewWidth * unit + 12)) < .001 && Canvas.GetLeft(sides[1]) > badgeBounds.Right, model.Id + ": side strips share the top inset and stay outside the physical badge");
                check(geometry.LedIndex.HasValue ? badgeVisual is Button && _previewVisuals.ContainsKey(geometry.LedIndex.Value) : badgeVisual is Border && !badgeVisual.Focusable && !KeyboardCanvas.Children.OfType<Button>().Any(button => button.Name.StartsWith("BadgeLed", StringComparison.Ordinal)) && _previewVisuals.Count == Device.LedCount, model.Id + ": only an addressable badge is interactive and registered as a preview LED");
            }
            check(!ReadDeviceButton.IsEnabled && !TransferButton.IsEnabled && DeviceSaveState.Text == L.T("Gerät nicht verbunden"), model.Id + ": disconnected profiles cannot read or transfer");
            SetOfflineConnectionFixture([model.Id]);
            var beforeLabels = Fingerprint(_project); var undoBeforeLabels = _undo.Count;
            BuildDeviceButtons(); UpdateSourceHint(); UpdateLayout();
            var deviceButton = DeviceButtons.Children.OfType<Button>().Single();
            var deviceLabels = (StackPanel)((Grid)deviceButton.Content).Children[1];
            check((string)deviceButton.Tag == model.Id && ((TextBlock)deviceLabels.Children[2]).IsVisible && ((TextBlock)deviceLabels.Children[2]).Text == L.T("Experimentell") && System.Windows.Automation.AutomationProperties.GetName(deviceButton).Contains(L.T("Experimentell"), StringComparison.Ordinal), model.Id + ": connected device button identifies experimental support visually and accessibly");
            check(Fingerprint(_project) == beforeLabels && _undo.Count == undoBeforeLabels, model.Id + ": showing experimental support never edits a profile");
            check(ReadDeviceButton.IsEnabled && TransferButton.IsEnabled, model.Id + ": exact connected runtime identity permits configuration actions");
            var before = Fingerprint(_project);
            var key = Device.Keys.First(k => k.LedIndex.HasValue);
            _selection.Add(key.Id); ApplyColor("#A01337");
            check(Layer.Keys[key.Id].Color == "#A01337", model.Id + ": individual key colors can be edited");
            Undo_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == before, model.Id + ": undo restores the complete candidate profile");
            ApplyLightingPreset(LightingPresetStore.CreateInGlorious(model.Id));
            check(_showLightingEffects && Layer.LightingEffect.Colors.Count > 0, model.Id + ": animated preset is available without firmware");
            OnboardSerializer.ValidateEditorProject(_project);
            check(OnboardSerializer.Prepare(_project).Payload[6] == BoardDescriptorRegistry.Get(model.Id).BoardId, model.Id + ": configuration payload uses its own board identity");
            if (Device.BadgeLedIndices.Count > 0)
            {
                ResetWorkflowFixture(model.Id); SetTab(1); _selection.Add(key.Id); RenderKeyboard();
                if (model.Id.StartsWith("gmmk3-100-", StringComparison.Ordinal))
                    check(((Grid)_keyButtons[key.Id].Content).Children.OfType<TextBlock>().First().FontSize >= 19, model.Id + ": full-size keyboard legend remains readable after preview scaling");
                var initialBadge = Fingerprint(_project); var history = _undo.Count;
                var badge = Device.BadgeLedIndices.Single();
                var badgeButton = KeyboardCanvas.Children.OfType<Button>().Single(button => button.Name == "BadgeLed" + badge);
                badgeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
                check(_activeTab == 0 && _lightingTarget == LightingTarget.Badge && _selection.Count == 0 && SelectedEffectLeds().SequenceEqual([badge]), model.Id + ": clicking logo opens lighting without a phantom binding key");
                check(LightingSurfaceButtons is WrapPanel && Fingerprint(_project) == initialBadge && _undo.Count == history && BadgeLightButton.IsVisible && !SideChoicePanel.IsVisible && !SideSyncToggle.IsVisible, model.Id + ": badge selection is pure and exposes no side synchronization controls");
                var beforeSides = Device.SideLedIndices.ToDictionary(led => led, SideLedColor);
                ApplyLightingColor("#14C8E6");
                check(SideLedColor(badge) == "#14C8E6" && beforeSides.All(pair => SideLedColor(pair.Key) == pair.Value) && Layer.Keys[key.Id].Binding == key.DefaultBinding, model.Id + ": logo color changes only the independent badge");
                Undo_Click(this, new RoutedEventArgs());
                check(Fingerprint(_project) == initialBadge, model.Id + ": logo color undo restores entire profile");
                ApplyLightingColor("#14C8E6"); SetLightingEditor(true); SetEffectScope(true);
                EffectChoice.SelectedValue = 2; EffectSpeed.Value = 77; EffectBrightness.Value = 219; SetEffectColors(["#0033FF", "#FFD400"]);
                ApplyEffect_Click(this, new RoutedEventArgs());
                check(Layer.LedEffects.Count == 1 && Layer.LedEffects[badge] is { Mode: 2, Speed: 77, Brightness: 219 }, model.Id + ": logo supports its own effect and palette");
                var logoEffect = JsonSerializer.Serialize(Layer.LedEffects[badge]);
                SetSideSynchronization(true); ChooseLightingSurface(LightingTarget.Both); ApplyLightingColor("#B32244");
                check(SelectedEffectLeds().Length == 20 && Device.SideLedIndices.All(led => SideLedColor(led) == "#B32244") && SideLedColor(badge) == "#14C8E6" && JsonSerializer.Serialize(Layer.LedEffects[badge]) == logoEffect, model.Id + ": synchronized sides preserve independent logo color and effect");
                var payload = OnboardSerializer.Prepare(_project).Payload;
                check(OnboardSerializer.Prepare(OnboardSerializer.Decode(payload)).Payload.SequenceEqual(payload), model.Id + ": edited logo round-trips with all colors and bindings");
            }
            else check(BadgeLightButton.Visibility == Visibility.Collapsed, model.Id + ": layouts without badge expose no logo target");
            SetTab(4); UpdateLayout();
            check(DeviceFeedbackHint.IsVisible && DeviceFeedbackHint.Text == DeviceFeedbackContext.Invitation(model) && DeviceFeedbackContact.IsVisible,
                model.Id + ": experimental feedback invitation stays beside the optional contact button");
            check(ExperimentalDeviceLabel.IsVisible && ExperimentalDeviceNotice.IsVisible && ExperimentalDeviceNotice.Text == L.T("Experimentelle Unterstützung: noch nicht vollständig geprüft."), model.Id + ": device overview explains the experimental test status without an opt-in gate");
            check(DeviceHardwareOverview.Visibility == Visibility.Visible && OfflineDeviceOverview.Visibility == Visibility.Collapsed && DeviceRecoveryOverview.Visibility == Visibility.Visible && FirmwareInstallationNotice.Visibility == Visibility.Collapsed && ExperimentalSetupActions.Visibility == Visibility.Collapsed, model.Id + ": runtime status, generic update and restore share one device overview");
            check(ManageFirmwareButton.IsEnabled && RestoreOriginalFirmwareButton.IsEnabled, model.Id + ": connected models can open generic firmware checks with independent write protection");
            DiscardPendingEdits();
        }
        ResetWorkflowFixture("gmmk3-pro-100-iso"); SetOfflineConnectionFixture(["gmmk3-pro-100-iso"]);
        BuildDeviceButtons(); UpdateSourceHint(); SetTab(4); UpdateLayout();
        check(!Device.IsOfflineOnly && ReadDeviceButton.IsEnabled && TransferButton.IsEnabled && ManageFirmwareButton.IsEnabled && RestoreOriginalFirmwareButton.IsEnabled,
            "The separately identified PRO exposes normal configuration and guarded setup actions");
        check(!ExperimentalDeviceLabel.IsVisible && !ExperimentalDeviceNotice.IsVisible && !OfflineDeviceOverview.IsVisible && DeviceHardwareOverview.IsVisible && DeviceRecoveryOverview.IsVisible,
            "The hardware-tested GMMK 3 PRO 100% ISO has no experimental badge and retains guarded firmware actions");
        check(DeviceFeedbackHint.Text == L.T("Deine Rückmeldung hilft uns, INGLORIOUS Advanced zu verbessern."),
            "The tested PRO keeps the general invitation after switching from an experimental model");
        var engineeringBefore = Fingerprint(_project); var engineeringKey = Device.Keys.First(key => key.LedIndex.HasValue);
        SetTab(0); _selection.Add(engineeringKey.Id); ApplyColor("#A01337");
        check(Layer.Keys[engineeringKey.Id].Color == "#A01337" && _project.DeviceId == "gmmk3-pro-100-iso", "Engineering profile stays separately editable");
        Undo_Click(this, new RoutedEventArgs());
        check(Fingerprint(_project) == engineeringBefore, "Engineering profile edit can be undone without device access");
        }
        finally { _devices = devicesBefore; ResetWorkflowFixture("pro-iso"); }
    }

    private void RunWorkflowChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Workflow checks require offline UI-smoke mode.");
        var preferencesBeforeFeedback = JsonSerializer.Serialize(_preferences);
        var modelForFeedback = DeviceCatalog.All.First(model => model.IsExperimentalFirmware).Id;
        var settingsForFeedback = new AppSettingsWindow(_preferences, offlinePreview: true, connectedDevices: []);
        OfferFeedbackAfterSetup(modelForFeedback, new(modelForFeedback, new(0, 11, 2), 1), settingsForFeedback);
        check(_pendingSetupFeedback is { } queued && queued.DeviceId == modelForFeedback && queued.Version?.BuildId == 1 &&
            !_feedbackInvitationOpen && JsonSerializer.Serialize(_preferences) == preferencesBeforeFeedback,
            "setup feedback remains queued while settings owns an edit session, without changing preferences or opening a prompt");
        settingsForFeedback.Close();
        OfferPendingSetupFeedback();
        check(_pendingSetupFeedback is null && !_feedbackInvitationOpen && JsonSerializer.Serialize(_preferences) == preferencesBeforeFeedback,
            "draining setup feedback clears its queue and offline checks cannot persist markers or open a prompt");
        OfferPendingSetupFeedback();
        check(_pendingSetupFeedback is null && JsonSerializer.Serialize(_preferences) == preferencesBeforeFeedback,
            "an empty setup feedback queue has no side effects");
        RunExperimentalModelChecks(check);
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            ResetWorkflowFixture(id);
            check(ExperimentalDeviceLabel.Visibility == Visibility.Collapsed && ExperimentalDeviceNotice.Visibility == Visibility.Collapsed && DeviceButtons.Children.OfType<Button>().Where(button => (string)button.Tag == id).All(button => ((StackPanel)((Grid)button.Content).Children[1]).Children[2].Visibility == Visibility.Collapsed), id + ": stable devices have no experimental label or notice");
            check(_selection.Count == 0 && _selectedSideLeds.Count == 0, id + ": opening a local project does not preselect a key or side LED");
            var left = Device.LeftSideLedIndices;
            var right = Device.RightSideLedIndices;
            var initial = Fingerprint(_project);
            var history = _undo.Count;
            SelectSideLed(left[1], toggle: false);
            SetWholeSideSelection(true);
            check(_lightingTarget == LightingTarget.Left && SideIndices(_lightingTarget).SequenceEqual(left), id + ": whole-side option expands an individual selection to its side");
            check(Fingerprint(_project) == initial && _undo.Count == history && !IsDirty, id + ": whole-side selection alone changes no project data or undo history");
            var keysBefore = JsonSerializer.Serialize(Layer.Keys);
            var otherLayer = JsonSerializer.Serialize(_project.Layers[1]);
            var rightBefore = right.ToDictionary(i => i, SideLedColor);
            ApplyLightingColor("#A13457");
            check(left.All(i => SideLedColor(i) == "#A13457") && rightBefore.All(pair => SideLedColor(pair.Key) == pair.Value), id + ": whole-side paint changes every selected side LED and preserves opposite side");
            check(JsonSerializer.Serialize(Layer.Keys) == keysBefore && JsonSerializer.Serialize(_project.Layers[1]) == otherLayer, id + ": whole-side paint preserves key colors and other layers");
            Undo_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == initial, id + ": whole-side painting is one undoable operation");
            Redo_Click(this, new RoutedEventArgs());
            SetLightingEditor(true);
            SelectSideLed(left[1], toggle: false);
            EffectChoice.SelectedValue = 16; EffectSpeed.Value = 91; EffectBrightness.Value = 167; EffectReverse.IsChecked = true;
            ApplyEffect_Click(this, new RoutedEventArgs());
            check(_effectForSelection && left.All(i => Layer.LedEffects[i] is { Mode: 16, Speed: 91, Brightness: 167, Reverse: true }) && right.All(i => !Layer.LedEffects.ContainsKey(i)), id + ": effect applied to whole selected side remains independent of opposite side");
            SetSideSynchronization(true);
            check(left.Select(i => JsonSerializer.Serialize(Layer.LedEffects[i])).SequenceEqual(right.Select(i => JsonSerializer.Serialize(Layer.LedEffects[i]))), id + ": side synchronization copies each corresponding effect");
            check(!ReferenceEquals(Layer.LedEffects[left[0]], Layer.LedEffects[right[0]]), id + ": synchronized LED effects are independent settings objects");
            SelectSideLed(right[2], toggle: false);
            check(SideIndices(_lightingTarget).Count == left.Count + right.Count, id + ": synchronized whole-side selection includes both sides");
            ApplyLightingColor("#123B56");
            check(Device.NonKeyLedIndices.All(i => SideLedColor(i) == "#123B56"), id + ": whole-side synchronized paint reaches both complete strips");
            EffectChoice.SelectedValue = 20; EffectSpeed.Value = 143; EffectBrightness.Value = 199;
            ApplyEffect_Click(this, new RoutedEventArgs());
            check(Device.NonKeyLedIndices.All(i => Layer.LedEffects[i] is { Mode: 20, Speed: 143, Brightness: 199, Reverse: false }), id + ": synchronized whole-side effects preserve parameters on every side LED");
            var beforeSelectionMode = Fingerprint(_project); history = _undo.Count;
            SetWholeSideSelection(false);
            check(Fingerprint(_project) == beforeSelectionMode && _undo.Count == history, id + ": returning to individual LED selection does not alter configuration");
            SetSideSynchronization(false);
            SelectSideLed(left[1], toggle: false);
            var untouched = Device.NonKeyLedIndices.Where(i => i != left[1]).ToDictionary(i => i, i => (Color: SideLedColor(i), Effect: JsonSerializer.Serialize(Layer.LedEffects[i])));
            ApplyLightingColor("#ADCE19");
            EffectChoice.SelectedValue = 15; EffectSpeed.Value = 77; EffectBrightness.Value = 89;
            ApplyEffect_Click(this, new RoutedEventArgs());
            check(SideIndices(_lightingTarget).SequenceEqual(new[] { left[1] }) && SideLedColor(left[1]) == "#ADCE19" && Layer.LedEffects[left[1]].Mode == 15, id + ": individual LED color and effect are editable again");
            check(untouched.All(pair => SideLedColor(pair.Key) == pair.Value.Color && JsonSerializer.Serialize(Layer.LedEffects[pair.Key]) == pair.Value.Effect), id + ": individual edit preserves every unselected side LED");
            SelectSideLed(right[1], toggle: true);
            beforeSelectionMode = Fingerprint(_project); SetWholeSideSelection(true);
            check(_lightingTarget == LightingTarget.Both && Fingerprint(_project) == beforeSelectionMode, id + ": selection spanning both sides expands without changing data");

            RenderKeyboard();
            var strips = KeyboardCanvas.Children.OfType<UniformGrid>().OrderBy(Canvas.GetLeft).ToArray();
            var keyLeft = _keyButtons.Values.Min(Canvas.GetLeft);
            var keyRight = _keyButtons.Values.Max(button => Canvas.GetLeft(button) + button.Width);
            check(strips.Length == 2 && strips[0].Children.Count == left.Count && strips[1].Children.Count == right.Count, id + ": physical preview renders one distinct segment per side LED");
            check(keyLeft - (Canvas.GetLeft(strips[0]) + strips[0].Width) >= 12 && Canvas.GetLeft(strips[1]) - keyRight >= 12, id + ": visible side strips keep a separate gutter from key hit targets");
            check(Canvas.GetLeft(strips[0]) >= 0 && Canvas.GetLeft(strips[1]) + strips[1].Width <= KeyboardCanvas.Width, id + ": side strip targets remain inside the keyboard canvas");

            RunLayerCopyWorkflowChecks(id, check);
            RunMacroWorkflowChecks(id, check);
            RunDeviceSaveStatusChecks(id, check);
            TransferWindow.RunOfflineStatusChecks(id, check);
        }
        RunBindingWorkflowChecks(check);
        RunKnobBrightnessChecks(check);
        RunKnobPresetChecks(check);
        RunLayerGuidanceChecks(check);
        ResetWorkflowFixture("pro-iso");
    }

    private void ResetWorkflowFixture(string deviceId)
    {
        _deviceReadRequired.Clear(); _deviceReadCrc.Clear(); _automaticFallbackProjects.Clear();
        _sessions.Clear(); ApplyOpening(OpenDefaultProject(deviceId, allowDefault: false));
        _wholeSideSelection = false; _showLightingEffects = false; _effectForSelection = false; _macroAssigning = false;
        _lightingTarget = LightingTarget.Keys; _selectedSideLeds.Clear(); _selection.Clear(); _activeTab = 0;
        BindingSearch.Text = ""; BindingCategory.SelectedIndex = 0;
        RefreshAll();
    }

    private void RunLayerCopyWorkflowChecks(string id, Action<bool, string> check)
    {
        ResetWorkflowFixture(id);
        var key = Device.Keys.First(k => k.LedIndex is not null);
        var side = Device.LeftSideLedIndices[0];
        Layer.Keys[key.Id].Color = "#D12191"; Layer.Keys[key.Id].Binding = "KC_P";
        Layer.ExtraLedColors[side] = "#018ACD"; Layer.SynchronizeSides = true;
        Layer.LightingEffect = new() { Mode = 16, Speed = 202, Brightness = 143, Target = 2, Reverse = true };
        Layer.LedEffects[key.LedIndex!.Value] = new() { Mode = 19, Speed = 88, Brightness = 134 };
        _project.Layers[2].Name = "Zielebene"; RefreshAll(); SetTab(3);
        var before = Fingerprint(_project);
        CopyTargetLayer.SelectedIndex = 2; CopyLayer_Click(this, new RoutedEventArgs());
        var copy = _project.Layers[2];
        check(copy.Name == "Zielebene" && copy.Keys[key.Id].Binding == "KC_P" && copy.Keys[key.Id].Color == "#D12191" && copy.ExtraLedColors[side] == "#018ACD" && copy.SynchronizeSides, id + ": layer copy preserves destination name while copying bindings, colors and side preference");
        check(copy.LightingEffect is { Mode: 16, Speed: 202, Brightness: 143, Target: 2, Reverse: true } && copy.LedEffects[key.LedIndex.Value].Mode == 19, id + ": layer copy includes global and per-LED effects");
        check(!ReferenceEquals(copy.Keys, Layer.Keys) && !ReferenceEquals(copy.Keys[key.Id], Layer.Keys[key.Id]) && !ReferenceEquals(copy.ExtraLedColors, Layer.ExtraLedColors) && !ReferenceEquals(copy.LightingEffect, Layer.LightingEffect) && !ReferenceEquals(copy.LedEffects, Layer.LedEffects) && !ReferenceEquals(copy.LedEffects[key.LedIndex.Value], Layer.LedEffects[key.LedIndex.Value]), id + ": copied layer owns all mutable settings deeply");
        var copied = Fingerprint(_project);
        Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == before, id + ": layer copy has a complete undo snapshot");
        Redo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == copied, id + ": layer copy redo restores every copied field");
        _project.Layers[2].Keys[key.Id].Color = "#123456"; _project.Layers[2].LightingEffect.Mode = 1; _project.Layers[2].LedEffects[key.LedIndex.Value].Speed = 3;
        check(Layer.Keys[key.Id].Color == "#D12191" && Layer.LightingEffect.Mode == 16 && Layer.LedEffects[key.LedIndex.Value].Speed == 88, id + ": editing copied layer does not mutate source layer");
        ResetWorkflowFixture(id); _layerIndex = 1; RefreshAll(); SetTab(3);
        var baseBefore = Fingerprint(_project); var history = _undo.Count;
        CopyTargetLayer.SelectedIndex = 0; CopyLayer_Click(this, new RoutedEventArgs());
        check(Fingerprint(_project) == baseBefore && _undo.Count == history, id + ": copy to base refuses transparent bindings without changing data or undo");
        foreach (var assignment in Layer.Keys.Values) assignment.Binding = "KC_NO";
        RefreshAll(); CopyTargetLayer.SelectedIndex = 0; CopyLayer_Click(this, new RoutedEventArgs());
        check(_project.Layers[0].Keys.Values.All(assignment => assignment.Binding == "KC_NO") && ProjectService.Validate(_project).Count == 0, id + ": explicit bindings may be copied to base without introducing transparency");
    }

    private void RunMacroWorkflowChecks(string id, Action<bool, string> check)
    {
        ResetWorkflowFixture(id);
        var key = Device.Keys.First(k => !k.IsEncoder).Id;
        var original = new MacroDefinition { Id = "workflow-original", Name = "Vorlage", Steps = [new() { Kind = "down", Key = "KC_P" }, new() { Kind = "delay", DelayMs = 25 }, new() { Kind = "up", Key = "KC_P" }] };
        _project.Macros.Add(original); _macroId = original.Id; Layer.Keys[key].Binding = "MACRO:" + original.Id;
        RefreshAll(); SetTab(2);
        var before = Fingerprint(_project);
        DuplicateMacro_Click(this, new RoutedEventArgs());
        var copy = CurrentMacro!;
        check(copy.Id != original.Id && copy.Steps.Count == original.Steps.Count && copy.Steps.Zip(original.Steps).All(pair => !ReferenceEquals(pair.First, pair.Second)), id + ": duplicate macro gets a new ID and independent step objects");
        check(_project.Layers.All(layer => layer.Keys.Values.All(assignment => assignment.Binding != "MACRO:" + copy.Id)) && Layer.Keys[key].Binding == "MACRO:" + original.Id, id + ": duplicating macro preserves original assignments and leaves copy unassigned");
        copy.Steps[0].Key = "KC_B"; copy.Steps[2].Key = "KC_B"; copy.Steps[1].DelayMs = 333;
        check(original.Steps[0].Key == "KC_P" && original.Steps[1].DelayMs == 25, id + ": editing duplicated steps leaves original macro unchanged");
        var copied = Fingerprint(_project);
        Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == before, id + ": macro duplication can be undone without losing original assignment");
        Redo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == copied, id + ": macro duplication and edited steps return on redo");

        var recorded = new List<MacroStep> { new() { Kind = "down", Key = "KC_Z" }, new() { Kind = "delay", DelayMs = 22 }, new() { Kind = "up", Key = "KC_Z" } };
        before = Fingerprint(_project); var macroCount = _project.Macros.Count;
        _macroAssigning = true; AdoptRecordedMacro(recorded);
        var adopted = CurrentMacro!;
        check(_project.Macros.Count == macroCount + 1 && !_macroAssigning && adopted.Steps.Zip(recorded).All(pair => !ReferenceEquals(pair.First, pair.Second)), id + ": recorded import creates an independent macro and exits assignment mode");
        recorded[0].Key = "KC_X"; recorded[1].DelayMs = 800;
        check(adopted.Steps[0].Key == "KC_Z" && adopted.Steps[1].DelayMs == 22, id + ": imported recording is isolated from later source-buffer edits");
        var afterImport = Fingerprint(_project);
        Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == before, id + ": recording import can be undone completely");
        Redo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == afterImport, id + ": recording import redo restores all recorded steps");
        _macroId = _project.Macros.Last().Id; RefreshAll(); SetTab(2);
        _selection.Add(key); RefreshInspector();
        before = Fingerprint(_project);
        AssignMacro_Click(this, new RoutedEventArgs());
        check(_macroAssigning && _selection.Count == 0 && Fingerprint(_project) == before && MacroWorkspace.Visibility == Visibility.Collapsed, id + ": first macro-assignment step only opens key selection");
        _selection.Add(key); RenderKeyboard(); RefreshInspector();
        var assignmentMacro = CurrentMacro!.Id;
        AssignMacro_Click(this, new RoutedEventArgs());
        check(Layer.Keys[key].Binding == "MACRO:" + assignmentMacro, id + ": second macro-assignment step applies selected macro to the chosen key");
        Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == before, id + ": macro assignment can be undone independently of recording");
        _macroAssigning = true;
        SwitchDevice(id == "pro-iso" ? "numpad" : "pro-iso");
        check(!_macroAssigning && MacroWorkspace.Visibility == Visibility.Visible, id + ": device switch resets macro assignment mode");
        SwitchDevice(id);
        check(!_macroAssigning && Fingerprint(_project) == before, id + ": returning to device preserves macros and assignments without resuming stale assignment mode");
        check(ProjectService.Validate(_project).Count == 0, id + ": macro workflows leave a valid balanced project");
    }

    private void RunBindingWorkflowChecks(Action<bool, string> check)
    {
        ResetWorkflowFixture("pro-iso"); SetTab(1);
        check(BindingCategoryId("KC_P") == 1 && new[] { "KC_P0", "KC_P7", "KC_PENT", "KC_PPLS", "KC_PSLS", "KC_NUM" }.All(code => BindingCategoryId(code) == 4), "Letter P and numpad bindings belong to distinct categories");
        check(new[] { "KC_PSCR", "KC_PGUP", "KC_PGDN", "KC_PAUS" }.All(code => BindingCategoryId(code) == 2), "Navigation keys starting with P do not become numpad bindings");
        var before = Fingerprint(_project); var history = _undo.Count;
        BindingCategory.SelectedIndex = 1; BindingSearch.Text = "P"; RefreshBindingChoices();
        check(BindingChoice.Items.Cast<BindingOption>().Any(option => option.Code == "KC_P") && BindingChoice.Items.Cast<BindingOption>().All(option => BindingCategoryId(option.Code) == 1), "Letter category search retains P without numpad choices");
        BindingCategory.SelectedIndex = 4; BindingSearch.Text = ""; RefreshBindingChoices();
        check(BindingChoice.Items.Cast<BindingOption>().Any(option => option.Code == "KC_P7") && BindingChoice.Items.Cast<BindingOption>().All(option => option.Code != "KC_P"), "Numpad category excludes ordinary P");
        BindingCategory.SelectedIndex = 0; BindingSearch.Text = "kc_p7"; RefreshBindingChoices();
        check(BindingChoice.Items.Cast<BindingOption>().Select(option => option.Code).SequenceEqual(new[] { "KC_P7" }), "Binding code search is case-insensitive and specific");
        BindingSearch.Text = "Strg"; RefreshBindingChoices();
        check(BindingChoice.Items.Cast<BindingOption>().Select(option => option.Code).ToHashSet().SetEquals(new[] { "KC_LCTL", "KC_RCTL" }), "Binding search finds translated visible modifier names");
        BindingCategory.SelectedIndex = 7; BindingSearch.Text = "Ebene 2"; RefreshBindingChoices();
        check(BindingChoice.Items.Cast<BindingOption>().Select(option => option.Code).ToHashSet().SetEquals(new[] { "MO(2)", "TG(2)", "TO(2)" }), "Layer category and text search expose hold, toggle and direct switch");
        BindingSearch.Text = "not-a-binding"; RefreshBindingChoices();
        check(BindingChoice.Items.Count == 0 && BindingChoice.SelectedIndex == -1 && Fingerprint(_project) == before && _undo.Count == history, "Empty search has no stale binding selection or draft mutation");
        var encoder = Device.Keys.First(key => key.IsEncoder);
        SelectKey(encoder.Id); SetKnobEditorMode(true); UpdateLayout();
        check(BindingSearch.Text == "" && BindingCategory.SelectedIndex == 0 &&
            (BindingChoice.SelectedItem as BindingOption)?.Code == Assignment(encoder.Id).Binding,
            "Selecting a preview key reveals its existing assignment even when the previous search hid it");
        var scroller = NavigationDescendants(BindingChoice).OfType<ScrollViewer>().FirstOrDefault();
        check(scroller is not null && scroller.VerticalOffset > 0,
            "Selecting an encoder scrolls its lower-list media binding into view");
        BindingCategory.SelectedIndex = 5; BindingSearch.Text = "KC_VOL"; RefreshBindingChoices();
        SelectKey(encoder.Id);
        check(BindingCategory.SelectedIndex == 5 && BindingSearch.Text == "KC_VOL",
            "Selecting a key retains a search and category that already include its assignment");
        _layerIndex = 1; SelectKey(encoder.Id);
        check((BindingChoice.SelectedItem as BindingOption)?.Code == "KC_TRNS" && BindingCategory.SelectedIndex == 0,
            "The revealed assignment follows the edited layer rather than the base binding");
        check(Fingerprint(_project) == before && _undo.Count == history,
            "Revealing an existing assignment never assigns a function or changes undo history");
        _layerIndex = 0;
        SetTab(4);
        UpdateLayout();
        check(!KeyboardWorkspace.IsVisible && DeviceOverview.IsVisible, "Device settings show the device overview without editable key targets or a hidden ancestor");
    }

    private void RunKnobBrightnessChecks(Action<bool, string> check)
    {
        var decision = _pendingEditDecisionOverride;
        try
        {
            foreach (var id in new[] { "pro-iso", "numpad", "gmmk3-pro-100-iso" })
            {
                ResetWorkflowFixture(id); _editingSlider = false; SetTab(1);
                var press = Device.Keys.Single(key => key.Label == "Knob").Id;
                SelectKey(press); SetKnobEditorMode(false);
                check(KnobPresetPanel.Visibility == Visibility.Visible, id + ": knob press offers the same preset controls");
                SelectKey("encoder:ccw");
                check(KnobPresetPanel.Visibility == Visibility.Visible, id + ": selecting one rotation offers brightness for both directions");
                _selection.Add(press); RefreshInspector();
                var before = Fingerprint(_project); ApplyKnobPreset("brightness");
                check(KnobPresetPanel.Visibility == Visibility.Collapsed && Fingerprint(_project) == before,
                    id + ": a mixed key and rotation selection cannot change both knob directions");
                _selection.Remove(press); _selection.Add("encoder:cw"); _layerIndex = 2; RefreshInspector();
                var expected = ProjectService.Clone(_project);
                expected.Layers[2].Keys["encoder:ccw"].Binding = "RM_VALD";
                expected.Layers[2].Keys["encoder:cw"].Binding = "RM_VALU";
                before = Fingerprint(_project); ApplyKnobPreset("brightness");
                check(Fingerprint(_project) == Fingerprint(expected) && _undo.Count == 1 && IsDirty,
                    id + ": shortcut changes only both rotation bindings on the edited layer, preserving colors, knob press and all other settings");
                ApplyKnobPreset("brightness");
                check(_undo.Count == 1, id + ": repeating the shortcut creates no redundant undo step");
                Undo_Click(this, new RoutedEventArgs());
                check(Fingerprint(_project) == before && _undo.Count == 0, id + ": one undo fully restores both directions");
                SelectKey("encoder:cw"); BindingCategory.SelectedIndex = 5; BindingSearch.Text = "KC_VOL";
                ApplyKnobPreset("brightness");
                check(BindingSearch.Text.Length == 0 && BindingCategory.SelectedIndex == 0 &&
                    (BindingChoice.SelectedItem as BindingOption)?.Code == "RM_VALU", id + ": shortcut reveals the new binding despite a previous media filter");
                BindingCategory.SelectedIndex = 9; BindingSearch.Text = ""; RefreshBindingChoices();
                check(BindingChoice.Items.Cast<BindingOption>().Select(option => option.Code).ToHashSet().SetEquals(["RM_VALU", "RM_VALD"]),
                    id + ": lighting category contains only supported brightness functions");
                BindingSearch.Text = L.T("Beleuchtung heller"); RefreshBindingChoices();
                check(BindingChoice.Items.Count == 1 && ((BindingOption)BindingChoice.Items[0]).Code == "RM_VALU", id + ": translated brightness name is searchable");
                BindingCategory.SelectedIndex = 0; BindingSearch.Text = "RM_"; RefreshBindingChoices();
                check(BindingChoice.Items.Count == 2, id + ": unsupported RGB effect actions stay hidden");
                Undo_Click(this, new RoutedEventArgs());
                DebounceInput.Text = "13"; _pendingEditDecisionOverride = () => PendingEditChoice.Cancel;
                before = Fingerprint(_project); ApplyKnobPreset("brightness");
                check(HasPendingTiming && Fingerprint(_project) == before && _undo.Count == 0, id + ": canceling pending input prevents a knob change and preserves the draft");
                DiscardPendingEdits(); _pendingEditDecisionOverride = null;
            }
        }
        finally { _pendingEditDecisionOverride = decision; DiscardPendingEdits(); ResetWorkflowFixture("pro-iso"); }
    }

    private void RunLayerGuidanceChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Layer guidance checks require offline mode.");
        var decision = _pendingEditDecisionOverride;
        try
        {
            foreach (var id in new[] { "pro-iso", "numpad" })
            {
                ResetWorkflowFixture(id); SetTab(1);
                var key = Device.Keys.First(key => !key.IsEncoder && key.DefaultBinding != "MO(1)").Id;
                _selection.Add(key); RefreshInspector();
                var baseline = ProjectService.Clone(_project);
                var before = Fingerprint(_project);
                SetTab(3); UpdateLayout();
                check(_activeTab == 3 && _selection.SetEquals([key]) && Fingerprint(_project) == before && _undo.Count == 0,
                    id + ": opening guided layer assignment preserves the selected key and complete draft");
                check(LayerEditingHelp.IsVisible && LayerEditingHint.Text == LayerGuidance.EditingHint &&
                    LayerSummaryHint.Text == LayerGuidance.LayerSummary(0) && LayerAssignmentSource.Text.Contains(Device.Keys.First(k => k.Id == key).AccessibleLabel),
                    id + ": layer guidance separates editor selection from runtime and identifies the assignment key");
                check(TargetLayer.SelectedIndex == 1 && LayerSwitchMode.SelectedIndex == 0,
                    id + ": an ordinary base-layer key starts with a hold action to layer one");
                TargetLayer.SelectedIndex = 3; LayerSwitchMode.SelectedIndex = 1; RefreshInspector();
                check(TargetLayer.SelectedIndex == 3 && LayerSwitchMode.SelectedIndex == 1 && Fingerprint(_project) == before,
                    id + ": refreshing the same selection preserves the user's unassigned target and behavior");
                foreach (var (mode, target, code) in new[] { (0, 3, "MO(3)"), (1, 2, "TG(2)"), (2, 1, "TO(1)") })
                {
                    Layer.Keys[key].Binding = code; RefreshInspector();
                    check(TargetLayer.SelectedIndex == target && LayerSwitchMode.SelectedIndex == mode,
                        id + ": an existing " + code + " is selected in the guided editor");
                }
                var peer = Device.Keys.First(k => !k.IsEncoder && k.Id != key).Id;
                Layer.Keys[key].Binding = "TO(3)"; Layer.Keys[peer].Binding = "TG(2)"; _selection.Add(peer); RefreshInspector();
                check(TargetLayer.SelectedIndex == 1 && LayerSwitchMode.SelectedIndex == 0,
                    id + ": mixed selected functions do not present one key's function as their shared assignment");
                Layer.Keys[peer].Binding = "TO(3)"; RefreshInspector();
                check(TargetLayer.SelectedIndex == 3 && LayerSwitchMode.SelectedIndex == 2,
                    id + ": matching functions on multiple selected keys are recognized together");
                Layer.Keys[key].Binding = baseline.Layers[0].Keys[key].Binding;
                Layer.Keys[peer].Binding = baseline.Layers[0].Keys[peer].Binding;
                _selection.Remove(peer); RefreshInspector();
                TargetLayer.SelectedIndex = 3; LayerSwitchMode.SelectedIndex = 1;
                _project = ProjectService.Clone(_project); RefreshInspector();
                check(TargetLayer.SelectedIndex == 1 && LayerSwitchMode.SelectedIndex == 0 && Fingerprint(_project) == before && _undo.Count == 0,
                    id + ": replacing the profile refreshes the suggested action without changing profile data");
                foreach (var (mode, code) in new[] { (0, "MO(2)"), (1, "TG(2)"), (2, "TO(2)") })
                {
                    TargetLayer.SelectedIndex = 2; LayerSwitchMode.SelectedIndex = mode;
                    check(LayerSwitchHint.Text == LayerGuidance.ActionDescription(mode, 0, 2) && AssignLayerSwitchButton.IsEnabled,
                        id + ": mode " + mode + " displays its explanation before assignment");
                    AssignLayerSwitch_Click(this, new RoutedEventArgs());
                    var expected = ProjectService.Clone(baseline); expected.Layers[0].Keys[key].Binding = code;
                    check(Fingerprint(_project) == Fingerprint(expected) && _undo.Count == 1,
                        id + ": " + code + " changes only the selected key on the edited layer in one undo step");
                    Undo_Click(this, new RoutedEventArgs());
                    check(Fingerprint(_project) == before && _undo.Count == 0,
                        id + ": undo fully restores the draft after " + code);
                }
                SelectLayer(1);
                var upperBefore = Fingerprint(_project);
                check(TargetLayer.SelectedIndex == 0 && LayerSwitchMode.SelectedIndex == 2 &&
                    LayerSwitchHint.Text == LayerGuidance.ActionDescription(2, 1, 0),
                    id + ": an unassigned higher-layer key suggests returning directly to the base");
                TargetLayer.SelectedIndex = 3; LayerSwitchMode.SelectedIndex = 0;
                check(LayerSwitchHint.Text == LayerGuidance.ActionDescription(0, 1, 3),
                    id + ": changing the target updates guidance for the actual edited and target layers");
                RefreshInspector();
                check(TargetLayer.SelectedIndex == 3 && LayerSwitchMode.SelectedIndex == 0,
                    id + ": a manual higher-layer action remains selected during inspector refresh");
                TargetLayer.SelectedIndex = 0; LayerSwitchMode.SelectedIndex = 2; AssignLayerSwitch_Click(this, new RoutedEventArgs());
                check(_project.Layers[1].Keys[key].Binding == "TO(0)" && _project.Layers[0].Keys[key].Binding == Device.Keys.First(k => k.Id == key).DefaultBinding,
                    id + ": a return-to-base key changes only the current higher layer");
                Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == upperBefore, id + ": return key has complete undo");
                check(AssignTransparentButton.IsEnabled && TransparentBindingHint.Text == LayerGuidance.BindingDescription("KC_TRNS"),
                    id + ": higher layers explain and allow inherited bindings");
                SelectLayer(0); check(!AssignTransparentButton.IsEnabled, id + ": base cannot inherit a nonexistent lower layer");
                _selection.Clear(); RefreshInspector();
                check(!AssignLayerSwitchButton.IsEnabled, id + ": assigning a layer switch requires a selected key");
                if (Device.Keys.FirstOrDefault(k => k.IsEncoder) is { } encoder)
                {
                    _selection.Add(encoder.Id); RefreshInspector(); TargetLayer.SelectedIndex = 2; LayerSwitchMode.SelectedIndex = 0;
                    var encoderBefore = Fingerprint(_project); AssignLayerSwitch_Click(this, new RoutedEventArgs());
                    check(Fingerprint(_project) == encoderBefore && _undo.Count == 0, id + ": encoder rotation cannot receive a hold action");
                }
                ResetWorkflowFixture(id); SetTab(1); _selection.Add(key); RefreshInspector();
                DebounceInput.Text = "13"; _pendingEditDecisionOverride = () => PendingEditChoice.Cancel;
                before = Fingerprint(_project); SetTab(3);
                check(_activeTab == 1 && HasPendingTiming && Fingerprint(_project) == before,
                    id + ": canceling pending-input resolution keeps the current binding editor");
                _pendingEditDecisionOverride = () => PendingEditChoice.Discard;
                SetTab(3);
                DebounceInput.Text = "13"; _pendingEditDecisionOverride = () => PendingEditChoice.Cancel;
                TargetLayer.SelectedIndex = 2; LayerSwitchMode.SelectedIndex = 2; AssignLayerSwitch_Click(this, new RoutedEventArgs());
                check(Fingerprint(_project) == before && _undo.Count == 0 && HasPendingTiming,
                    id + ": canceled pending input blocks guided assignment without losing the input");
                DiscardPendingEdits(); _pendingEditDecisionOverride = null;
                SetTab(1); BindingCategory.SelectedIndex = 0; BindingSearch.Text = "";
                foreach (var code in new[] { "KC_LCTL", "KC_TRNS", "MO(1)", "TG(2)", "TO(3)" })
                {
                    BindingChoice.SelectedValue = code;
                    check(BindingFunctionHint.Text == LayerGuidance.BindingDescription(code) && BindingFunctionHint.Visibility == Visibility.Visible,
                        id + ": selected function explains " + code + " without changing bindings");
                }
                check(BindingChoice.Items.Cast<BindingOption>().Select(option => option.Code).Distinct().Count() == BindingChoice.Items.Count,
                    id + ": the function list has no duplicated default Fn entry");
            }
        }
        finally { _pendingEditDecisionOverride = decision; DiscardPendingEdits(); ResetWorkflowFixture("pro-iso"); }
    }

    private void RunDeviceSaveStatusChecks(string id, Action<bool, string> check)
    {
        ResetWorkflowFixture(id);
        _confirmedDeviceSaves.Remove(id); UpdateSourceHint();
        check(DeviceSaveState.Text == "Noch nicht übertragen" && !SafetyBanner.IsVisible, id + ": local profile alone never implies a verified transfer or shows a provenance banner");
        _confirmedDeviceSaves[id] = (Fingerprint(_project), new DateTime(2026, 10, 10, 12, 34, 0));
        try
        {
            UpdateSourceHint();
            check(DeviceSaveState.Text.StartsWith("✓ Auf Tastatur bestätigt") && !SafetyBanner.IsVisible, id + ": matching verified profile is shown in the footer without a provenance banner");
            var key = Device.Keys.First(k => k.LedIndex.HasValue).Id;
            _selection.Add(key); ApplyColor("#194C75");
            check(DeviceSaveState.Text == "Änderungen noch nicht übertragen" && !SafetyBanner.IsVisible, id + ": editing a verified profile removes current-device confirmation without adding a banner");
            _savedFingerprint = Fingerprint(_project); UpdateDirtyState();
            check(!IsDirty && DeviceSaveState.Text == "Änderungen noch nicht übertragen", id + ": saving an edited local profile cannot confirm device storage");
            Undo_Click(this, new RoutedEventArgs());
            check(IsDirty && DeviceSaveState.Text.StartsWith("✓ Auf Tastatur bestätigt"), id + ": undo restores the confirmed device state even when it differs from the saved file");
            _savedFingerprint = "different local file"; UpdateDirtyState();
            check(DeviceSaveState.Text.StartsWith("✓ Auf Tastatur bestätigt"), id + ": local-file fingerprint changes do not invalidate a matching device confirmation");
            var external = ProjectService.Clone(_project);
            external.Layers[0].Keys[key].Color = "#FEA012";
            RecordDeviceRead(external);
            check(DeviceSaveState.Text == "Änderungen noch nicht übertragen" && _project.Layers[0].Keys[key].Color != "#FEA012", id + ": reading a different device state invalidates an old confirmation before editor adoption");
            AdoptDeviceProject(external, 9);
            check(DeviceSaveState.Text.StartsWith("✓ Auf Tastatur bestätigt") && IsDirty, id + ": adopting a verified device read confirms it independently of local file storage");
            InvalidateDeviceConfirmation(id);
            check(DeviceSaveState.Text == "Noch nicht übertragen", id + ": beginning another read clears stale device confirmation even if reading later fails or is canceled");
        }
        finally { _confirmedDeviceSaves.Remove(id); UpdateSourceHint(); }
    }
}

