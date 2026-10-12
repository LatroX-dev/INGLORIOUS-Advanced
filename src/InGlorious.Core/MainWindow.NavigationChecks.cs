using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace InGlorious.Core;

public partial class MainWindow
{
    // Offline UI regression: no file dialogs, preferences writes, transport or input synthesis.
    private void RunNavigationChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Navigation checks require offline UI-smoke mode.");
        var oldWidth = Width; var oldHeight = Height;
        try
        {
            foreach (var id in new[] { "pro-iso", "numpad" })
            {
                ResetWorkflowFixture(id);
                check(CurrentLayerChoice.Items.Count == 4 && CurrentLayerChoice.SelectedIndex == 0, id + ": layer chooser starts with four layers and the current selection");
                var panels = new FrameworkElement[] { ColorPanel, BindingPanel, MacroPanel, LayerPanel, DeviceSettingsPanel };
                for (var area = 0; area < 6; ++area)
                {
                    SetTab(area); UpdateLayout();
                    var showsKeys = area is 0 or 1 or 3;
                    check(_activeTab == area && KeyboardWorkspace.IsVisible == showsKeys && SelectionBar.IsVisible == showsKeys,
                        id + ": area " + area + " shows key selection only where it can be edited");
                    check(LightingPresetsButton.IsVisible == (area == 0), id + ": lighting templates are available only in the lighting editor");
                    check(ProfilesWorkspace.IsVisible == (area == 5) && InspectorHost.IsVisible == (area < 4) && MacroWorkspace.IsVisible == (area == 2) && DeviceOverview.IsVisible == (area == 4),
                        id + ": area " + area + " has its intended workspace and inspector visibility");
                    check(panels.Count(panel => panel.IsVisible) == (area == 5 ? 0 : 1) && (area == 5 || panels[area].IsVisible),
                        id + ": area " + area + " exposes no controls from another editor");
                    check(TransferButton.IsVisible && ReadDeviceButton.IsVisible && Equals(TransferButton.Content, "Auf Tastatur übertragen"),
                        id + ": area " + area + " retains both keyboard actions with the requested transfer label");
                }

                ResetWorkflowFixture(id); SetLightingEditor(true); SetEffectScope(false);
                var before = Fingerprint(_project); var history = _undo.Count;
                GlobalEffectEnabled.IsChecked = true; EffectChoice.SelectedValue = 3; EffectSpeed.Value = 174;
                _pendingEditDecisionOverride = () => PendingEditChoice.Cancel;
                CurrentLayerChoice.SelectedIndex = 2;
                check(_layerIndex == 0 && CurrentLayerChoice.SelectedIndex == 0 && HasPendingEffect && Fingerprint(_project) == before && _undo.Count == history,
                    id + ": cancelling layer-combo navigation restores its displayed selection and leaves pending input intact");
                SetTab(5);
                check(_activeTab == 0 && !ProfilesWorkspace.IsVisible && HasPendingEffect,
                    id + ": cancelling pending input blocks navigation to Profiles");
                _pendingEditDecisionOverride = () => PendingEditChoice.Apply;
                EffectTarget.SelectedIndex = -1;
                CurrentLayerChoice.SelectedIndex = 2;
                check(_layerIndex == 0 && CurrentLayerChoice.SelectedIndex == 0 && HasPendingEffect && Fingerprint(_project) == before,
                    id + ": invalid apply restores layer-combo selection without partial model changes");
                SetTab(5);
                check(_activeTab == 0 && !ProfilesWorkspace.IsVisible && Fingerprint(_project) == before,
                    id + ": invalid pending input cannot be hidden by opening Profiles");
                EffectTarget.SelectedIndex = 0;
                CurrentLayerChoice.SelectedIndex = 2;
                check(_layerIndex == 2 && CurrentLayerChoice.SelectedIndex == 2 && _project.Layers[0].LightingEffect is { Mode: 3, Speed: 174, Target: 1 } && _project.Layers[2].LightingEffect.Mode == 0 && !HasPendingEdits,
                    id + ": valid apply commits to the old layer before selecting the requested new layer");
                GlobalEffectEnabled.IsChecked = true; EffectChoice.SelectedValue = 6; EffectSpeed.Value = 88;
                before = Fingerprint(_project); _pendingEditDecisionOverride = () => PendingEditChoice.Discard;
                SetTab(5); UpdateLayout();
                check(_activeTab == 5 && ProfilesWorkspace.IsVisible && !HasPendingEdits && Fingerprint(_project) == before,
                    id + ": discard opens Profiles while preserving the saved model values");
                SetTab(0); GlobalEffectEnabled.IsChecked = true; EffectChoice.SelectedValue = 5; EffectSpeed.Value = 143;
                _pendingEditDecisionOverride = () => PendingEditChoice.Apply;
                SetTab(5); UpdateLayout();
                check(_activeTab == 5 && ProfilesWorkspace.IsVisible && Layer.LightingEffect is { Mode: 5, Speed: 143 } && !HasPendingEdits,
                    id + ": apply commits pending effect before opening Profiles");
                _pendingEditDecisionOverride = null;
                _defaultLoadError = "Defektes Testprofil"; UpdateSourceHint(); UpdateLayout();
                check(SafetyBanner.IsVisible && ProjectSourceHint.Text.Contains("Defektes Testprofil", StringComparison.Ordinal),
                    id + ": a profile load error remains visible after removing routine provenance text");
                _defaultLoadError = null; UpdateSourceHint(); UpdateLayout();
                check(!SafetyBanner.IsVisible && !ProjectSourceHint.IsVisible && !ProjectSourceText.IsVisible,
                    id + ": successful profile state shows no provenance banner");
                RunLightingSurfaceNavigationChecks(id, check);
            }

            Width = MinWidth; Height = MinHeight; SetTab(5); UpdateLayout();
            check(TransferButton.Parent is Grid footer && ReferenceEquals(footer, ReadDeviceButton.Parent) && footer.Parent is Border footerHost && ReferenceEquals(footerHost.Parent, EditorSurface) && Grid.GetRow(footerHost) == 2,
                "Both keyboard actions share the fixed footer in root row two");
            var upload = NavigationBounds(TransferButton); var read = NavigationBounds(ReadDeviceButton);
            check(upload.Width > 0 && read.Width > 0 && read.Right <= upload.Left && upload.Right <= EditorSurface.ActualWidth && upload.Bottom <= EditorSurface.ActualHeight && read.Bottom <= EditorSurface.ActualHeight,
                "Both footer buttons remain visible and separate at minimum window size");
            var settings = SettingsButton;
            var settingsBounds = NavigationBounds(settings);
            check(settings.IsVisible && Equals(settings.Content, "\uE713") && Equals(settings.ToolTip, "Einstellungen") && System.Windows.Automation.AutomationProperties.GetName(settings) == "Einstellungen" && settingsBounds.Top >= 0 && settingsBounds.Bottom <= 56 && settingsBounds.Right <= EditorSurface.ActualWidth,
                "App settings remain directly reachable in the top toolbar at minimum size");
            check(TryFindResource("HelpVisibility") is Visibility help && help == Visibility.Visible,
                "Optional operating help is visible by default in offline mode");
            SetTab(0); UpdateLayout();
            var presetsBounds = NavigationBounds(LightingPresetsButton); var inspectorBounds = NavigationBounds(InspectorHost);
            check(LightingPresetsButton.IsVisible && presetsBounds.Width >= 32 && presetsBounds.Left >= NavigationBounds(InspectorTitle).Right && presetsBounds.Right <= inspectorBounds.Right && presetsBounds.Bottom <= NavigationBounds(LightingEditorTabs).Top &&
                Equals(LightingPresetsButton.ToolTip, "Beleuchtungsvorlagen") && System.Windows.Automation.AutomationProperties.GetName(LightingPresetsButton) == "Beleuchtungsvorlagen",
                "Lighting templates remain labeled and reachable beside the inspector heading at minimum width");
            var previewPreferences = new AppPreferences { StartDevice = "numpad", ShowHelp = false };
            var settingsPreview = new AppSettingsWindow(previewPreferences, offlinePreview: true);
            try
            {
                check(settingsPreview.Preferences == previewPreferences && !((Button)settingsPreview.FindName("SaveButton")).IsEnabled && !((Button)settingsPreview.FindName("OpenFolderButton")).IsEnabled,
                    "App settings preview preserves preferences and disables persistence and folder actions");
            }
            finally { settingsPreview.Close(); }
        }
        finally
        {
            _pendingEditDecisionOverride = null; _pendingInputError = ""; _defaultLoadError = null;
            Width = oldWidth; Height = oldHeight;
            ResetWorkflowFixture("pro-iso"); UpdateLayout();
        }
    }

    private void RunLightingSurfaceNavigationChecks(string id, Action<bool, string> check)
    {
        ResetWorkflowFixture(id); _lastSideTarget = LightingTarget.Left;
        var before = Fingerprint(_project); var history = _undo.Count;
        var left = Device.LeftSideLedIndices.ToArray(); var right = Device.RightSideLedIndices.ToArray();
        void Click(ButtonBase button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); UpdateLayout(); }
        void Whole(bool enabled) { WholeSideToggle.IsChecked = enabled; Click(WholeSideToggle); }
        bool Selected(params int[] leds) => SelectedEffectLeds().ToHashSet().SetEquals(leds);
        bool Unchanged() => Fingerprint(_project) == before && _undo.Count == history && !IsDirty;

        UpdateLayout();
        check(LightingEditorTabs.Children.Count == 2 && ReferenceEquals(LightColorsTab.Parent, LightingEditorTabs) && ReferenceEquals(LightEffectsTab.Parent, LightingEditorTabs) &&
            ReferenceEquals(EffectScopeButtons.Parent, LightingEditorContent) && ReferenceEquals(StaticColorsPanel.Parent, LightingEditorContent) && ReferenceEquals(EffectsPanel.Parent, LightingEditorContent) && !StandbyPanel.IsVisible,
            id + ": Colors and Effects share one tab body while standby belongs to Device");
        check(KeysLightButton.IsVisible && SidesLightButton.IsVisible && LightingSelectionPanel.IsVisible &&
            !SideChoicePanel.IsVisible && !WholeSideToggle.IsVisible && !SideLedSelectionPanel.IsVisible && !EffectScopeButtons.IsVisible,
            id + ": color editing starts with two lighting surfaces and no irrelevant side or effect controls");
        Click(SidesLightButton);
        check(_lightingTarget == LightingTarget.Left && _wholeSideSelection && WholeSideToggle.IsChecked == true && Selected(left) &&
            SideChoicePanel.IsVisible && WholeSideToggle.IsVisible && !SideLedSelectionPanel.IsVisible,
            id + ": first click on Seitenleisten selects the complete left strip and exposes side choices");
        check(Unchanged(), id + ": entering side lighting changes neither configuration nor history");

        Click(RightLightButton);
        check(_lightingTarget == LightingTarget.Right && _wholeSideSelection && Selected(right), id + ": right-side button selects the entire right strip");
        Click(BothLightButton);
        check(_lightingTarget == LightingTarget.Both && _wholeSideSelection && Selected(left.Concat(right).ToArray()), id + ": both-side button selects both complete strips");
        Click(LeftLightButton);
        check(_lightingTarget == LightingTarget.Left && _wholeSideSelection && Selected(left), id + ": left-side button restores the entire left strip");
        Click(RightLightButton); Click(KeysLightButton);
        check(_lightingTarget == LightingTarget.Keys && !SideChoicePanel.IsVisible && !WholeSideToggle.IsVisible && !SideLedSelectionPanel.IsVisible,
            id + ": returning to keys hides all side-specific selection controls");
        Click(SidesLightButton);
        check(_lightingTarget == LightingTarget.Right && _wholeSideSelection && Selected(right), id + ": returning to side lighting remembers its last side choice");

        Click(LightEffectsTab);
        check(_showLightingEffects && _effectForSelection && Selected(right) && LightingSelectionPanel.IsVisible && EffectScopeButtons.IsVisible,
            id + ": colors-to-effects preserves the entire selected side and uses selection scope");
        Click(GlobalEffectButton);
        check(!_effectForSelection && !LightingSelectionPanel.IsVisible && EffectScopeButtons.IsVisible && Selected(right),
            id + ": global effects hide target selection without discarding the remembered LEDs");
        Click(SelectedEffectButton); Click(LightColorsTab); Whole(false);
        check(!_wholeSideSelection && _lightingTarget == LightingTarget.SelectedLeds && Selected(right) &&
            _selectedSideLeds.SetEquals(right) && SideLedSelectionPanel.IsVisible,
            id + ": disabling whole-strip selection preserves all selected LEDs and exposes numbered controls");
        check(NavigationDescendants(SideLedRows).OfType<Button>().Count() == left.Length + right.Length,
            id + ": individual side selector exposes one button for every side LED");
        // Explicit modifier state keeps this offline check independent of keys the user may be holding.
        SelectSideLed(right[1], toggle: false); UpdateLayout();
        check(Selected(right[1]) && _selectedSideLeds.SetEquals(new[] { right[1] }), id + ": selecting an individual LED replaces the prior complete-strip selection");
        Click(SidesLightButton);
        check(!_wholeSideSelection && Selected(right[1]), id + ": clicking the active side surface does not expand an individual selection");
        Click(LightEffectsTab);
        check(_effectForSelection && Selected(right[1]) && LightingSelectionPanel.IsVisible && SideLedSelectionPanel.IsVisible,
            id + ": colors-to-effects preserves the exact individual LED selection");
        Click(LightColorsTab); Whole(true);
        check(_wholeSideSelection && _lightingTarget == LightingTarget.Right && Selected(right) && !SideLedSelectionPanel.IsVisible,
            id + ": whole-strip toggle expands an individual LED only to its containing side");
        check(Unchanged(), id + ": lighting surface, scope and granularity navigation never edits the profile");

        Whole(false); SelectSideLed(right[1], toggle: false);
        ApplyLightingColor("#4B93D1");
        var colored = Device.NonKeyLedIndices.Where(led => SideLedColor(led) == "#4B93D1").ToHashSet();
        Click(LightEffectsTab); EffectChoice.SelectedValue = 16; EffectSpeed.Value = 163; EffectBrightness.Value = 117;
        Click(ApplyEffectButton);
        check(colored.SetEquals(new[] { right[1] }) && Layer.LedEffects.Keys.ToHashSet().SetEquals(colored) &&
            Layer.LedEffects[right[1]] is { Mode: 16, Speed: 163, Brightness: 117 },
            id + ": color and effect actions modify the same exact individual LED set");

        ResetWorkflowFixture(id); _lastSideTarget = LightingTarget.Left;
        _selection.Add(Device.Keys.First(key => key.LedIndex.HasValue).Id); RefreshInspector();
        Click(LightEffectsTab); EffectChoice.SelectedValue = 3; EffectSpeed.Value = 179;
        before = Fingerprint(_project); history = _undo.Count;
        var selectedKeys = SelectedEffectLeds();
        _pendingEditDecisionOverride = () => PendingEditChoice.Cancel;
        Click(SidesLightButton);
        check(_lightingTarget == LightingTarget.Keys && !_wholeSideSelection && _lastSideTarget == LightingTarget.Left &&
            Selected(selectedKeys) && HasPendingEffect && Equals(EffectChoice.SelectedValue, 3) && EffectSpeed.Value == 179 && !SideChoicePanel.IsVisible && Unchanged(),
            id + ": cancelling pending edits blocks side entry before changing any selection or edited values");
        _pendingEditDecisionOverride = () => PendingEditChoice.Discard;
        Click(SidesLightButton);
        check(_lightingTarget == LightingTarget.Left && _wholeSideSelection && Selected(left) && !HasPendingEdits && Unchanged(),
            id + ": discarding pending edits enters the chosen full side without applying edits to either target");
        EffectChoice.SelectedValue = 6; EffectSpeed.Value = 181;
        _pendingEditDecisionOverride = () => PendingEditChoice.Cancel;
        Whole(false);
        check(_wholeSideSelection && WholeSideToggle.IsChecked == true && _lightingTarget == LightingTarget.Left && Selected(left) && HasPendingEffect && Unchanged(),
            id + ": cancelling a granularity change restores the whole-strip toggle and retains pending values");
        _pendingEditDecisionOverride = () => PendingEditChoice.Discard;
        Whole(false);
        check(!_wholeSideSelection && Selected(left) && !HasPendingEdits && Unchanged(),
            id + ": discarded pending edits allow individual mode without changing its selected LEDs");
        _pendingEditDecisionOverride = null;
    }

    private Rect NavigationBounds(FrameworkElement element) => element.TransformToAncestor(EditorSurface).TransformBounds(new Rect(new Point(), element.RenderSize));
    private static IEnumerable<DependencyObject> NavigationDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); ++index)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in NavigationDescendants(child)) yield return descendant;
        }
    }
}
