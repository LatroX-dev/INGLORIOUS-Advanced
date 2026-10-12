using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    // Called only by --ui-smoke; choices are injected, so no dialog or device operation can run.
    private void RunPendingEditsChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Pending-edit checks require offline UI-smoke mode.");
        try
        {
            foreach (var id in new[] { "pro-iso", "numpad" })
            {
                ApplyOpening(OpenDefaultProject(id, allowDefault: false)); _layerIndex = 0;
                _lightingTarget = LightingTarget.Keys; _showLightingEffects = true; _effectForSelection = false; _activeTab = 0;
                _selection.Clear(); _selectedSideLeds.Clear(); RefreshAll();
                var baseline = Fingerprint(_project); var decisions = 0;
                _pendingEditDecisionOverride = () => { ++decisions; return PendingEditChoice.Cancel; };
                check(!HasPendingEdits, id + ": initialization has no pending input");
                SetEffectScope(true); SetEffectScope(false);
                check(decisions == 0 && !HasPendingEdits && EffectTargetPanel.Visibility == Visibility.Visible, id + ": scope changes do not create pending values");
                GlobalEffectEnabled.IsChecked = true; EffectChoice.SelectedValue = 3; EffectSpeed.Value = 177;
                check(HasPendingEffect && PendingEditsHint.Visibility == Visibility.Visible, id + ": unapplied effect is visible");
                RefreshInspector();
                check((int)EffectChoice.SelectedValue == 3 && EffectSpeed.Value == 177 && HasPendingEffect, id + ": caption refresh preserves unapplied effect");
                SetTab(4); check(_activeTab == 0 && decisions == 1 && Fingerprint(_project) == baseline, id + ": cancel preserves editor and model");
                var key = Device.Keys.First(k => k.LedIndex.HasValue);
                SelectKey(key.Id); check(_selection.Count == 0 && decisions == 2, id + ": cancel also blocks a selection change");
                _pendingEditDecisionOverride = () => PendingEditChoice.Discard;
                SelectKey(key.Id);
                check(_selection.SetEquals(new[] { key.Id }) && _effectForSelection && !HasPendingEdits && Fingerprint(_project) == baseline, id + ": discard changes context without applying values");
                check(EffectTargetPanel.Visibility == Visibility.Collapsed, id + ": selected effect context refreshes after discard");
                EffectChoice.SelectedValue = 5; EffectBrightness.Value = 101;
                _pendingEditDecisionOverride = () => PendingEditChoice.Apply;
                CurrentLayerChoice.SelectedIndex = 1;
                check(_layerIndex == 1 && CurrentLayerChoice.SelectedIndex == 1 && _project.Layers[0].LedEffects[key.LedIndex!.Value] is { Mode: 5, Brightness: 101 } && !HasPendingEdits, id + ": layer combo applies pending effect to the old selection before navigation");
                Undo_Click(this, new RoutedEventArgs());
                check(!_project.Layers[0].LedEffects.ContainsKey(key.LedIndex!.Value), id + ": automatic apply remains undoable");

                SetTab(4); var oldInterval = _project.DeviceSettings.UsbPollingIntervalMs;
                DebounceInput.Text = "41"; var beforeInvalid = Fingerprint(_project);
                SetTab(0);
                check(_activeTab == 4 && Fingerprint(_project) == beforeInvalid && HasPendingTiming, id + ": invalid timing blocks apply and navigation");
                check(PendingEditsHint.Text.Contains("0 bis 40"), id + ": validation detail survives the navigation gate");
                check(!SaveProject(false) && Fingerprint(_project) == beforeInvalid, id + ": invalid input blocks file save before any file dialog");
                DebounceInput.Text = "09"; PollingChoice.SelectedValue = oldInterval == 4 ? "2" : "4";
                check(!PendingEditsHint.Text.Contains("0 bis 40"), id + ": editing clears the previous validation message");
                SetTab(0);
                check(_activeTab == 0 && _project.DeviceSettings.DebounceMs == 9 && !HasPendingEdits, id + ": valid timing applies before navigation");
                DebounceInput.Text = "009";
                check(!HasPendingTiming, id + ": equivalent numeric text does not require confirmation");
                SetEffectScope(false); GlobalEffectEnabled.IsChecked = true; EffectChoice.SelectedValue = 6; DebounceInput.Text = "bad";
                var beforePair = Fingerprint(_project);
                check(!ResolvePendingEdits() && Fingerprint(_project) == beforePair && HasPendingEffect && HasPendingTiming, id + ": mixed pending set validates atomically before either mutation");
                _pendingEditDecisionOverride = null;
                check(ResolvePendingEdits() && !HasPendingEdits && Fingerprint(_project) == beforePair, id + ": offline smoke defaults to discard without a modal dialog");
            }
        }
        finally
        {
            _pendingEditDecisionOverride = null; _pendingInputError = "";
            _sessions.Clear(); ApplyOpening(OpenDefaultProject("pro-iso", allowDefault: false));
            _layerIndex = 0; _lightingTarget = LightingTarget.Keys; _selection.Clear(); _selectedSideLeds.Clear();
            _activeTab = 0; _showLightingEffects = false; _effectForSelection = false; RefreshAll();
        }
    }
}
