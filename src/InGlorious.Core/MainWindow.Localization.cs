using System.Windows.Controls;

namespace InGlorious.Core;

public partial class MainWindow
{
    private void ApplyUiLanguage(string language)
    {
        // A language choice changes presentation, including labels on pending edits, never their values.
        var fields = new[] { ProjectName, LayerName, MacroName, ColorHex, DebounceInput, SliderMinimum, SliderMaximum, MidiChannel, MidiController, StandbySeconds, StandbyBrightness };
        var texts = fields.Select(box => box.Text).ToArray();
        var effect = (EffectChoice.SelectedValue, EffectSpeed.Value, EffectBrightness.Value, EffectTarget.SelectedIndex, EffectReverse.IsChecked, GlobalEffectEnabled.IsChecked);
        var colors = _effectColors.ToList();
        var slider = SliderModeChoice.SelectedIndex;
        var polling = PollingChoice.SelectedValue;
        var standby = (StandbyEnabled.IsChecked, StandbyMode.SelectedIndex, StandbyTimeChoice.SelectedValue);
        var baseline = (_effectInputBaseline, _timingInputBaseline, _standbyInputBaseline);
        var pending = _refreshingPendingInputs;
        _refreshingPendingInputs = true;
        try
        {
            L.SetLanguage(language);
            RefreshSharedBrightness();
            RefreshAll();
            EffectChoice.Items.Refresh(); BindingChoice.Items.Refresh(); StepKeyChoice.Items.Refresh();
            for (var i = 0; i < fields.Length; ++i) fields[i].Text = texts[i];
            SetEffectColors(colors);
            EffectChoice.SelectedValue = effect.Item1; EffectSpeed.Value = effect.Item2; EffectBrightness.Value = effect.Item3;
            EffectTarget.SelectedIndex = effect.Item4; EffectReverse.IsChecked = effect.Item5; GlobalEffectEnabled.IsChecked = effect.Item6;
            PollingChoice.SelectedValue = polling; SliderModeChoice.SelectedIndex = slider;
            StandbyEnabled.IsChecked = standby.Item1; StandbyMode.SelectedIndex = standby.Item2; UpdateStandbyTimeChoice();
            if (standby.Item3?.ToString() == "custom") StandbyTimeChoice.SelectedValue = "custom";
            UpdateStandbyAvailability(); RefreshGlobalEffectSurface();
            UpdateEffectDescription(); UpdateSliderHint();
            (_effectInputBaseline, _timingInputBaseline, _standbyInputBaseline) = baseline;
        }
        finally { _refreshingPendingInputs = pending; }
        UpdatePendingEditsHint();
        RefreshLightingPreview();
    }
}
