using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace InGlorious.Core;

public partial class MainWindow
{
    private void RunNewFeaturesChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Offline checks only.");
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            ResetWorkflowFixture(id); SetLightingEditor(true); SetEffectScope(false);
            var before = Fingerprint(_project);
            EffectChoice.SelectedValue = 2; SetEffectColors(["#FF203D", "#22040B", "#FFFFFF"]);
            EffectColorsChanged();
            check(HasPendingEffect && Fingerprint(_project) == before, id + ": custom color sequence previews without changing profile");
            ApplyEffect_Click(this, new RoutedEventArgs());
            check(!HasPendingEdits && Layer.LightingEffect.Colors.SequenceEqual(new[] { "#FF203D", "#22040B", "#FFFFFF" }), id + ": applying custom sequence preserves order");
            check(OnboardSerializer.Prepare(_project).RequiresColorSequences, id + ": custom color sequence requires new device capability");
            Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == before, id + ": sequence is one undoable change");
            Redo_Click(this, new RoutedEventArgs());
            _selection.Add(Device.Keys.First(k => k.LedIndex.HasValue).Id); SetEffectScope(true);
            SetEffectColors(["#0055FF", "#FFD447"]); ApplyEffect_Click(this, new RoutedEventArgs());
            check(Layer.LedEffects.Values.Single().Colors.SequenceEqual(new[] { "#0055FF", "#FFD447" }) && Layer.LightingEffect.Colors.Count == 3, id + ": per-key sequence is independent of global sequence");
            before = Fingerprint(_project); var undo = _undo.Count;
            SetEffectColors(["#12FF34", "#EF0987"]); EffectSpeed.Value = 43;
            var input = EffectInputFingerprint();
            foreach (var language in L.LanguageChoices)
            {
                ApplyUiLanguage(language.Code);
                check(EffectInputFingerprint() == input && HasPendingEffect && Fingerprint(_project) == before && _undo.Count == undo,
                    id + ": " + language.Code + " switches labels without losing pending effect or changing history");
                check(Equals(SettingsButton.ToolTip, L.T("Einstellungen")) && ((LightingEffectOption)EffectChoice.Items[2]).Label == L.T("Farbwechsel"), id + ": translated static and dynamic labels update together");
            }
            ApplyUiLanguage("de"); DiscardPendingEdits();
            SetTab(3); UpdateLayout(); check(!LayerOverview.IsVisible, id + ": redundant large layer cards stay removed");
            SetTab(5); ProfileSearch.Text = "Numpad"; check(ProfileList.Items.Count == 1, id + ": profile library search matches device name"); ProfileSearch.Text = "";
            before = Fingerprint(_project); var bindings = JsonSerializer.Serialize(Layer.Keys.ToDictionary(p => p.Key, p => p.Value.Binding));
            SetLightingEditor(false);
            var preset = LightingPresetStore.CreateInGlorious(id);
            ApplyLightingPreset(preset); UpdateLayout();
            check(JsonSerializer.Serialize(Layer.Keys.ToDictionary(p => p.Key, p => p.Value.Binding)) == bindings && Layer.SynchronizeSides,
                id + ": INGLORIOUS template changes lighting while preserving bindings");
            check(_activeTab == 0 && _showLightingEffects && !_showLightingStandby && !_effectForSelection && ColorPanel.IsVisible && !HasPendingEdits,
                id + ": applying an animated template opens its whole-layer effect editor from colors-only mode");
            var applied = Fingerprint(_project); RenderLightingPreviewFrame(0); var firstFrame = _previewFrame.ToArray();
            var animated = false;
            for (uint time = 137; time <= 2740; time += 137)
            { RenderLightingPreviewFrame(time); animated |= !firstFrame.SequenceEqual(_previewFrame); }
            check(_previewScene?.Animated == true && animated && Fingerprint(_project) == applied,
                id + ": template preview actually changes LED frames without modifying the applied profile");
            var afterPresetHistory = _undo.Count;
            SetLightingEditor(false); ApplyLightingPreset(preset);
            check(_showLightingEffects && _undo.Count == afterPresetHistory && Fingerprint(_project) == applied,
                id + ": reapplying the same template restores its effect preview without another undo entry");
            Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == before, id + ": lighting template applies in one undo step");
            var staticPreset = LightingPresetStore.Capture(ProjectService.Create(id), 0, "Static preview fixture");
            ApplyLightingPreset(staticPreset); RenderLightingPreviewFrame(0);
            check(!_showLightingEffects && !_showLightingStandby && _previewScene?.Animated == false,
                id + ": a colors-only template still opens the color editor without invented animation");
        }
        ResetWorkflowFixture("numpad"); SetTab(0); UpdateLayout();
        var sliderButton = KeyboardCanvas.Children.OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == L.T("Schieberegler anpassen"));
        sliderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
        check(_activeTab == 1 && BindingPanel.IsVisible && SliderPanel.IsVisible && !KeyBindingPanel.IsVisible && !DeviceSettingsPanel.IsVisible && SharedBrightnessInfo.Text == L.T(SharedBrightnessService.Notice), "Clicking the pictured slider opens its binding editor and required background-service info");
        SliderModeChoice.SelectedIndex = 3;
        check(HasPendingTiming, "Changed slider function is included in pending device settings");
        _pendingEditDecisionOverride = () => PendingEditChoice.Cancel; SetTab(0);
        check(_activeTab == 1 && HasPendingTiming && SliderModeChoice.SelectedIndex == 3, "Cancel preserves pending slider mode in key bindings");
        _pendingEditDecisionOverride = () => PendingEditChoice.Apply; SetTab(0);
        check(_activeTab == 0 && _project.Slider.Mode == 3 && !HasPendingEdits, "Apply commits slider function before navigation");
        _pendingEditDecisionOverride = null;
        SetTab(1); ShowBindingTarget(true); UpdateLayout();
        var boundKey = Device.Keys.First(k => k.LedIndex.HasValue).Id;
        SelectKey(boundKey); UpdateLayout();
        check(KeyBindingPanel.IsVisible && !SliderPanel.IsVisible && !_editingSlider && _selection.Contains(boundKey)
            && CurrentLayerChoice.IsVisible && SelectionBar.IsVisible, "Clicking a Numpad key restores key assignment and selection tools");
        SetTab(4); UpdateLayout();
        check(!SliderPanel.IsVisible && DeviceSettingsPanel.IsVisible, "Device settings no longer contain the slider editor");
        SwitchDevice("pro-iso"); SetTab(1); UpdateLayout();
        check(KeyBindingPanel.IsVisible && !SliderPanel.IsVisible && !BindingTargetButtons.IsVisible, "PRO binding editor exposes keys only");
        check(!_sharedBrightness.Running, "Offline UI checks never start shared brightness hardware service");
        // The close flow must protect both device drafts even when a later save is canceled.
        ResetWorkflowFixture("pro-iso"); _project.Name = "Close PRO"; RefreshAll();
        SwitchDevice("numpad"); _project.Name = "Close Numpad"; RefreshAll();
        var closeBefore = Fingerprint(_project); var asked = 0; var saved = 0;
        check(!ConfirmCloseProjects(names => { asked = names.Length; return UnsavedChangesChoice.Cancel; }, () => { saved++; return true; }) && asked == 2 && saved == 0,
            "Cancel close preserves both dirty profiles without saving");
        check(ConfirmCloseProjects(_ => UnsavedChangesChoice.Discard, () => { saved++; return true; }) && saved == 0 && Fingerprint(_project) == closeBefore,
            "Explicit discard permits closing without changing the saved files or draft");
        check(!ConfirmCloseProjects(_ => UnsavedChangesChoice.Save, () => { saved++; return false; }) && saved == 1 && _project.DeviceId == "numpad" && Fingerprint(_project) == closeBefore,
            "A canceled or failed save prevents closing and restores the original device");
        foreach (var choice in new[] { UnsavedChangesChoice.Cancel, UnsavedChangesChoice.Discard, UnsavedChangesChoice.Save })
        {
            var dialog = new UnsavedChangesWindow(L.T("Ungespeicherte Profile"), L.T("Diese geänderten Profile vor dem Beenden speichern?\n\n"), ["Close PRO", "Close Numpad"]) { Owner = this };
            check(dialog.Choice == UnsavedChangesChoice.Cancel && dialog.CancelButton.IsCancel && dialog.CancelButton.IsDefault, "Unsaved dialog starts with safe cancel choice");
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (choice == UnsavedChangesChoice.Cancel) dialog.Close();
                else (choice == UnsavedChangesChoice.Save ? dialog.SaveButton : dialog.DiscardButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            dialog.ShowDialog();
            check(dialog.Choice == choice, "Unsaved dialog returns " + choice + " only after the corresponding action");
        }
        RunSliderPanelChecks(check);
        RunKeyBindingTransferChecks(check);
        ResetWorkflowFixture("pro-iso");
    }

    private void RunKeyBindingTransferChecks(Action<bool, string> check)
    {
        var reading = _readingDevice; var guide = _checkingFirmwareGuide; var scanning = _scanning;
        var loading = _startupLoading; var finishing = _startupFinishing; var closed = _startupClosed;
        var enabled = IsEnabled; var editorEnabled = EditorSurface.IsEnabled;
        var pendingDecision = _pendingEditDecisionOverride;
        try
        {
            _readingDevice = _checkingFirmwareGuide = _scanning = _startupLoading = _startupFinishing = _startupClosed = false;
            IsEnabled = EditorSurface.IsEnabled = true;
            ResetWorkflowFixture("numpad"); SetTab(1);
            var key = Device.Keys.First(k => !k.IsEncoder).Id;
            _projectPath = Path.Combine(Path.GetTempPath(), "inglorious-bindings-check-profile.json");
            _project.Name = "Local profile"; Layer.Keys[key].Color = "#123456";
            _project.DeviceSettings.DebounceMs = 9; _project.Standby.Enabled = true;
            RefreshAll(); _savedFingerprint = Fingerprint(_project);
            var context = CaptureKeyBindingContext();
            var originalBinding = _project.Layers[0].Keys[key].Binding;
            var source = ProjectService.Create("numpad");
            source.Layers[0].Keys[key].Binding = "KC_A"; source.Layers[1].Name = "Work"; source.Slider.Mode = 1;
            source.Macros.Add(new() { Id = "transfer_macro", Name = "Imported macro", Steps = [new() { Kind = "down", Key = "KC_B" }, new() { Kind = "up", Key = "KC_B" }] });
            source.Layers[2].Keys[key].Binding = "MACRO:transfer_macro";
            var document = KeyBindingTransfer.Capture(source);
            foreach (var slider in new[] { false, true })
            {
                ShowBindingTarget(slider); UpdateLayout();
                check(ImportKeyBindingsButton.IsVisible && ExportKeyBindingsButton.IsVisible,
                    "Binding file actions remain available for both keys and slider");
            }
            check(!ImportKeyBindingsCore(context, document, () => false) && Fingerprint(_project) == context.Fingerprint && _undo.Count == 0,
                "Canceling binding import preserves the complete draft and undo history");
            check(ImportKeyBindingsCore(context, document, () => true) && _undo.Count == 1 && _redo.Count == 0,
                "Confirmed binding import creates one undo step");
            var imported = Fingerprint(_project);
            check(_project.Layers[0].Keys[key].Binding == "KC_A" && _project.Layers[1].Name == "Work" && _project.Slider.Mode == 1 &&
                _project.Layers[2].Keys[key].Binding == "MACRO:transfer_macro" && _project.Macros.Single().Id == "transfer_macro",
                "Binding import includes layer names, assignments, slider and assigned macros");
            check(_project.Layers[0].Keys[key].Color == "#123456" && _project.DeviceSettings.DebounceMs == 9 && _project.Standby.Enabled &&
                _project.Name == "Local profile" && _projectPath == context.Path && _savedFingerprint == context.Fingerprint && IsDirty,
                "Binding import preserves lighting, timing, standby and profile save identity");
            check(ReferenceEquals(_sessions["numpad"].Project, _project) && _sessions["numpad"].Undo.Length == 1,
                "The active device session retains the imported draft and undo history");
            Undo_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == context.Fingerprint && _undo.Count == 0 && !IsDirty,
                "One undo restores the complete profile before binding import");
            Redo_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == imported && _undo.Count == 1, "Redo restores imported bindings and macros");
            Undo_Click(this, new RoutedEventArgs()); context = CaptureKeyBindingContext();
            var confirmations = 0; var rejected = false;
            try { ImportKeyBindingsCore(context, KeyBindingTransfer.Capture(ProjectService.Create("pro-iso")), () => { confirmations++; return true; }); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && confirmations == 0 && Fingerprint(_project) == context.Fingerprint && _undo.Count == 0,
                "Wrong-model bindings are rejected before confirmation without changing history");
            check(!ImportKeyBindingsCore(context, document, () => { _project.Name = "Changed during confirmation"; return true; }) &&
                _project.Name == "Changed during confirmation" && _project.Layers[0].Keys[key].Binding == originalBinding && _undo.Count == 0,
                "A confirmed import cannot overwrite intervening project changes");
            RefreshAll(); context = CaptureKeyBindingContext();
            check(!ImportKeyBindingsCore(context, document, () => { _projectPath = context.Path + ".renamed"; return true; }) && _undo.Count == 0,
                "Saving under a different profile path invalidates a pending binding import");
            context = CaptureKeyBindingContext();
            check(!ImportKeyBindingsCore(context, document, () => { ProjectName.Text = "Pending profile name"; return true; }) &&
                ProjectName.Text == "Pending profile name" && _undo.Count == 0,
                "A confirmed import preserves intervening uncommitted name input");
            RefreshAll(); context = CaptureKeyBindingContext();
            check(!ImportKeyBindingsCore(context, document, () => { SwitchDevice("pro-iso"); return true; }) && _project.DeviceId == "pro-iso" &&
                Fingerprint(_sessions["numpad"].Project) == context.Fingerprint,
                "Changing the active device invalidates pending binding import without changing its saved session");
            check(IsKeyBindingExportProfilePath(context.Path!) && IsKeyBindingExportProfilePath(context.Path!.ToUpperInvariant()) &&
                !IsKeyBindingExportProfilePath(Path.Combine(Path.GetTempPath(), "inglorious-bindings-export.bindings.json")),
                "Binding export protects other open profile paths including Windows case aliases");
            SwitchDevice("numpad"); context = CaptureKeyBindingContext();
            check(IsKeyBindingExportProfilePath(_projectPath!), "Binding export protects the current profile file");
            foreach (var (name, set) in new (string, Action<bool>)[]
            {
                ("device read", value => _readingDevice = value), ("firmware operation", value => _checkingFirmwareGuide = value),
                ("connection scan", value => _scanning = value), ("startup read", value => _startupLoading = value),
                ("startup completion", value => _startupFinishing = value), ("closed window", value => _startupClosed = value)
            })
            {
                set(true);
                check(!ImportKeyBindingsCore(context, document, () => { confirmations++; return true; }) && confirmations == 0,
                    "Binding import refuses " + name + " before confirmation");
                set(false);
            }
            DebounceInput.Text = "13";
            check(!ImportKeyBindingsCore(context, document, () => { confirmations++; return true; }) && confirmations == 0 && HasPendingTiming,
                "Binding import never overwrites pending settings input");
            var pendingCalls = 0; _pendingEditDecisionOverride = () => { pendingCalls++; return PendingEditChoice.Apply; };
            ImportKeyBindings_Click(this, new RoutedEventArgs()); ExportKeyBindings_Click(this, new RoutedEventArgs());
            check(pendingCalls == 0 && HasPendingTiming && Fingerprint(_project) == context.Fingerprint,
                "Offline file actions never open pickers or apply pending user input");
        }
        finally
        {
            _readingDevice = reading; _checkingFirmwareGuide = guide; _scanning = scanning;
            _startupLoading = loading; _startupFinishing = finishing; _startupClosed = closed;
            IsEnabled = enabled; EditorSurface.IsEnabled = editorEnabled; _pendingEditDecisionOverride = pendingDecision;
            DiscardPendingEdits(); ResetWorkflowFixture("pro-iso");
        }
    }

    private void RunSliderPanelChecks(Action<bool, string> check)
    {
        var language = L.CurrentLanguage; var width = Width; var height = Height;
        try
        {
            ResetWorkflowFixture("numpad");
            _project.Slider = new() { Mode = 3, Minimum = 23, Maximum = 999, MidiChannel = 6, MidiController = 74 };
            RefreshAll(); SetTab(1); ShowBindingTarget(true);
            var before = Fingerprint(_project);
            var values = new[] { SliderMinimum.Text, SliderMaximum.Text, MidiChannel.Text, MidiController.Text };
            SliderCalibration.IsExpanded = false;
            check(!CurrentLayerChoice.IsVisible && !SelectionBar.IsVisible, "Slider: device-wide controls omit layer and key selection tools");
            foreach (var mode in new[] { 0, 1, 2, 3 })
            {
                SliderModeChoice.SelectedIndex = mode; UpdateLayout();
                check(MidiFields.IsVisible == (mode == 1) && SliderSharedBrightnessPanel.IsVisible == (mode == 3)
                    && SliderCalibration.Visibility == (mode > 0 ? Visibility.Visible : Visibility.Collapsed),
                    "Slider: mode " + mode + " exposes only its MIDI, shared dimming and optional calibration sections");
                check(values.SequenceEqual(new[] { SliderMinimum.Text, SliderMaximum.Text, MidiChannel.Text, MidiController.Text })
                    && Fingerprint(_project) == before && _undo.Count == 0 && HasPendingTiming == (mode != 3),
                    "Slider: mode " + mode + " preserves hidden values and changes only pending input");
            }
            SliderCalibration.IsExpanded = true; SliderCalibration.IsExpanded = false;
            check(!HasPendingTiming && Fingerprint(_project) == before,
                "Slider: expanding calibration never creates a configuration change");
            Width = 1150; Height = 760; UpdateLayout();
            check(SharedBrightnessToggle.Content is TextBlock { TextWrapping: TextWrapping.Wrap } wrapped
                && wrapped.ActualWidth > 0 && wrapped.ActualWidth <= SliderSharedBrightnessPanel.ActualWidth,
                "Slider: shared dimming label wraps inside the narrow inspector");
            foreach (var (field, label) in new[] { (SliderMinimum, "Minimum"), (SliderMaximum, "Maximum"), (MidiChannel, "Kanal"), (MidiController, "Controller") })
                check(field.Parent is StackPanel group && group.Children.OfType<TextBlock>().Any(t => t.Text == L.T(label))
                    && System.Windows.Automation.AutomationProperties.GetName(field) == L.T(label),
                    "Slider: " + label + " has a visible label and an accessible name");
            check(Equals(ApplySliderButton.Style, FindResource("PrimaryButton")) && SliderModeChoice.Items.Count == 4,
                "Slider: all four functions have one clearly styled apply action");

            foreach (var mode in new[] { 0, 2 })
            {
                ResetWorkflowFixture("numpad"); _project.Slider.Mode = mode;
                RefreshAll(); SetTab(1); ShowBindingTarget(true); SliderCalibration.IsExpanded = false;
                before = Fingerprint(_project); SliderMaximum.Text = "invalid";
                ApplySlider_Click(this, new RoutedEventArgs()); UpdateLayout();
                check(SliderCalibration.Visibility == Visibility.Visible && SliderCalibration.IsExpanded && SliderMaximum.IsVisible
                    && PendingEditsHint.Visibility == Visibility.Visible && HasPendingTiming && Fingerprint(_project) == before && _undo.Count == 0,
                    "Slider: invalid hidden calibration in mode " + mode + " opens for correction without changing the profile");
                ApplyUiLanguage("en"); UpdateLayout();
                check(SliderCalibration.IsVisible && SliderCalibration.IsExpanded && SliderMaximum.IsVisible && SliderMaximum.Text == "invalid"
                    && HasPendingTiming && _pendingInputError.Length > 0 && Fingerprint(_project) == before,
                    "Slider: language refresh keeps mode " + mode + " calibration errors and their editable fields visible");
                ApplyUiLanguage(language);
                SliderMinimum.Text = "23"; SliderMaximum.Text = "999";
                MidiChannel.Text = "invalid"; MidiController.Text = "240";
                ApplySlider_Click(this, new RoutedEventArgs()); UpdateLayout();
                check(MidiFields.IsVisible && MidiChannel.IsVisible && MidiController.IsVisible && HasPendingTiming
                    && Fingerprint(_project) == before && _undo.Count == 0,
                    "Slider: inactive invalid MIDI fields in mode " + mode + " become visible before apply can succeed");
                ApplyUiLanguage("en"); UpdateLayout();
                check(MidiFields.IsVisible && MidiChannel.Text == "invalid" && MidiController.Text == "240" && _pendingInputError.Length > 0,
                    "Slider: language refresh does not hide inactive MIDI validation errors in mode " + mode);
                ApplyUiLanguage(language);
                MidiChannel.Text = "11"; MidiController.Text = "105"; ApplySlider_Click(this, new RoutedEventArgs());
                check(_project.Slider is { Minimum: 23, Maximum: 999, MidiChannel: 10, MidiController: 105 } && _project.Slider.Mode == mode
                    && !HasPendingTiming && _undo.Count == 1,
                    "Slider: correcting hidden fields commits the complete settings once in mode " + mode);
                Undo_Click(this, new RoutedEventArgs());
                check(Fingerprint(_project) == before && !HasPendingTiming,
                    "Slider: undo restores all original values after correction in mode " + mode);
            }
        }
        finally
        {
            ApplyUiLanguage(language); Width = width; Height = height;
            DiscardPendingEdits(); ResetWorkflowFixture("pro-iso");
        }
    }
}
