using System.Text.Json;
using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private LightingEffectSettings ReadEffectInput() => new()
    {
        Mode = (EffectChoice.SelectedItem as LightingEffectOption)?.Id ?? 0,
        Speed = (int)Math.Round(EffectSpeed.Value), Brightness = (int)Math.Round(EffectBrightness.Value),
        Target = EffectTarget.SelectedIndex + 1, Reverse = EffectReverse.IsChecked == true, Colors = _effectColors.ToList()
    };

    private LightingEffectSettings EffectiveEffectInput()
    {
        var effect = ReadEffectInput();
        if (!_effectForSelection && GlobalEffectEnabled.IsChecked != true) return GlobalEffectSwitch.Disable(effect);
        if (effect.Mode == 0) effect.Speed = 128;
        effect.Reverse &= (EffectChoice.SelectedItem as LightingEffectOption)?.HasDirection == true;
        return effect;
    }

    private void GlobalEffectEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_pendingEditsReady || _refreshingEffects || _refreshingPendingInputs) return;
        if (!_effectForSelection && GlobalEffectEnabled.IsChecked == true)
        {
            // Keep the current draft while switching off and on. An applied static setting
            // has no hidden animation to restore, so enabling it starts with breathing.
            var value = GlobalEffectSwitch.Enable(ReadEffectInput());
            _refreshingEffects = true;
            try { EffectChoice.SelectedValue = value.Mode; EffectBrightness.Value = value.Brightness; }
            finally { _refreshingEffects = false; }
            UpdateEffectDescription();
        }
        RefreshGlobalEffectSurface();
        PendingInputChanged(sender, e);
        RefreshLightingPreview();
    }

    private void RefreshGlobalEffectSurface()
    {
        GlobalEffectEnabled.Visibility = _effectForSelection ? Visibility.Collapsed : Visibility.Visible;
        var enabled = _effectForSelection || GlobalEffectEnabled.IsChecked == true;
        EffectSettingsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (_effectForSelection) return;
        EffectScopeHint.Text = enabled
            ? L.F($"Ebene {_layerIndex} · Gilt für LEDs ohne eigenen Effekt. {Layer.LedEffects.Count} LEDs haben eigene Einstellungen.")
            : L.T("LEDs ohne eigenen Effekt zeigen ihre festen Farben.");
        if (_activeTab == 0 && _showLightingEffects)
            InspectorSubtitle.Text = enabled ? (EffectChoice.SelectedItem as LightingEffectOption)?.Label ?? "" : L.T("Aus");
    }

    private void RunGlobalEffectSwitchChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Offline checks only.");
        try
        {
            foreach (var id in new[] { "pro-iso", "numpad" })
            {
                ResetWorkflowFixture(id);
                var keys = Device.Keys.Where(key => key.LedIndex.HasValue).Take(3).ToArray();
                Layer.Keys[keys[0].Id].Color = "#C62432";
                Layer.Keys[keys[1].Id].Color = "#000000";
                Layer.LightingEffect = new() { Mode = 16, Speed = 181, Brightness = 83, Target = 3, Reverse = true, Colors = ["#D72031", "#351022"] };
                Layer.LedEffects[keys[2].LedIndex!.Value] = new() { Mode = 2, Speed = 173, Brightness = 177, Colors = ["#19ABCD", "#F0D42D"] };
                RefreshAll(); SetLightingEditor(true); SetEffectScope(false);
                var before = Fingerprint(_project); var history = _undo.Count;
                var own = JsonSerializer.Serialize(Layer.LedEffects); var draft = JsonSerializer.Serialize(ReadEffectInput());
                check(GlobalEffectEnabled.IsChecked == true && EffectSettingsPanel.Visibility == Visibility.Visible && !HasPendingEffect, id + ": an installed animation exposes an enabled global switch");
                GlobalEffectEnabled.IsChecked = false;
                check(HasPendingEffect && EffectSettingsPanel.Visibility == Visibility.Collapsed && EffectScopeButtons.Visibility == Visibility.Visible && ApplyEffectButton.IsEnabled && Fingerprint(_project) == before && _undo.Count == history,
                    id + ": turning global off remains pending and keeps the scope buttons available");
                ApplyUiLanguage("en"); ApplyUiLanguage("de");
                check(GlobalEffectEnabled.IsChecked == false && HasPendingEffect && Fingerprint(_project) == before,
                    id + ": language changes preserve the uncommitted global switch and hidden effect values");
                RenderLightingPreviewFrame(1234);
                var expectedLayer = ProjectService.Clone(_project).Layers[_layerIndex];
                expectedLayer.LightingEffect = GlobalEffectSwitch.Disable(expectedLayer.LightingEffect);
                var expected = LightingPreviewEngine.CreateScene(Device, expectedLayer); var frame = new PreviewRgb[expected.LedCount];
                expected.Render(1234, [], frame);
                check(_previewFrame.SequenceEqual(frame) && frame[keys[1].LedIndex!.Value] == new PreviewRgb(0, 0, 0) && JsonSerializer.Serialize(Layer.LedEffects) == own,
                    id + ": off previews fixed colors and black without disabling individual effects");
                GlobalEffectEnabled.IsChecked = true;
                check(JsonSerializer.Serialize(ReadEffectInput()) == draft && !HasPendingEffect && Fingerprint(_project) == before && _undo.Count == history,
                    id + ": switching the unapplied draft off and on retains mode, palette, target, speed and brightness");
                GlobalEffectEnabled.IsChecked = false;
                _pendingEditDecisionOverride = () => PendingEditChoice.Cancel; SetEffectScope(true);
                check(!_effectForSelection && GlobalEffectEnabled.IsChecked == false && HasPendingEffect && Fingerprint(_project) == before,
                    id + ": cancelling scope navigation retains the uncommitted off state");
                _pendingEditDecisionOverride = () => PendingEditChoice.Discard; SetEffectScope(true);
                check(_effectForSelection && GlobalEffectEnabled.Visibility == Visibility.Collapsed && EffectSettingsPanel.Visibility == Visibility.Visible && !HasPendingEffect && Fingerprint(_project) == before,
                    id + ": selection editing remains available and discard restores the saved global state");
                SetEffectScope(false); GlobalEffectEnabled.IsChecked = false;
                check(ApplyEffectInputs() && Layer.LightingEffect is { Mode: 0, Brightness: 255, Speed: 181, Target: 3, Reverse: true } && Layer.LightingEffect.Colors.SequenceEqual(new[] { "#D72031", "#351022" }) && JsonSerializer.Serialize(Layer.LedEffects) == own && _undo.Count == history + 1 && !HasPendingEffect,
                    id + ": applying off records one undo step and preserves individual effects and the other global fields");
                Undo_Click(this, new RoutedEventArgs());
                check(Fingerprint(_project) == before && GlobalEffectEnabled.IsChecked == true, id + ": undo restores the full animation and switch state");
                Redo_Click(this, new RoutedEventArgs());
                check(GlobalEffectEnabled.IsChecked == false && EffectSettingsPanel.Visibility == Visibility.Collapsed && JsonSerializer.Serialize(Layer.LedEffects) == own,
                    id + ": redo restores static global lighting without touching individual effects");
                GlobalEffectEnabled.IsChecked = true;
                check(HasPendingEffect && Equals(EffectChoice.SelectedValue, 1) && EffectBrightness.Value == 255 && !GlobalEffectSwitch.IsEnabled(Layer.LightingEffect),
                    id + ": enabling an applied static setting starts a pending breathing effect without changing the saved profile");
                DiscardPendingEdits();
                Layer.LightingEffect.Brightness = 91; RefreshAll();
                check(GlobalEffectEnabled.IsChecked == true && Equals(EffectChoice.SelectedValue, 0) && EffectBrightness.Value == 91,
                    id + ": a static dimming setting remains enabled with its brightness intact");
            }
        }
        finally { _pendingEditDecisionOverride = null; DiscardPendingEdits(); ResetWorkflowFixture("pro-iso"); }
    }
}
