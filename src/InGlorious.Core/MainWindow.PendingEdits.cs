using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _pendingEditsReady, _refreshingPendingInputs, _applyingPendingEffect, _applyingPendingTiming, _resolvingPendingEdits;
    private string _effectInputBaseline = "", _timingInputBaseline = "", _pendingInputError = "";
    private Func<PendingEditChoice>? _pendingEditDecisionOverride;

    private void InitializePendingEdits()
    {
        _effectInputBaseline = EffectInputFingerprint(); _timingInputBaseline = TimingInputFingerprint();
        InitializeStandbyInputs();
        _pendingEditsReady = true;
        EffectChoice.SelectionChanged += PendingInputChanged;
        EffectTarget.SelectionChanged += PendingInputChanged;
        EffectSpeed.ValueChanged += PendingInputChanged;
        EffectBrightness.ValueChanged += PendingInputChanged;
        EffectReverse.Checked += PendingInputChanged; EffectReverse.Unchecked += PendingInputChanged;
        PollingChoice.SelectionChanged += PendingInputChanged;
        DebounceInput.TextChanged += PendingInputChanged;
        SliderModeChoice.SelectionChanged += PendingInputChanged;
        foreach (var box in new[] { SliderMinimum, SliderMaximum, MidiChannel, MidiController }) box.TextChanged += PendingInputChanged;
        UpdatePendingEditsHint();
    }
    private void PendingInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_refreshingPendingInputs) _pendingInputError = "";
        UpdatePendingEditsHint();
    }
    private string EffectInputFingerprint()
    {
        var option = EffectChoice.SelectedItem as LightingEffectOption;
        return JsonSerializer.Serialize(new
        {
            Enabled = GlobalEffectEnabled.IsChecked == true,
            Mode = option?.Id, Speed = option?.Id is 0 or 13 ? 128 : (int)Math.Round(EffectSpeed.Value),
            // Context changes are guarded before they happen; context itself is not an edited value.
            Brightness = (int)Math.Round(EffectBrightness.Value), Target = EffectTarget.SelectedIndex + 1,
            Reverse = option?.HasDirection == true && EffectReverse.IsChecked == true, Colors = _effectColors
        });
    }
    private string TimingInputFingerprint() => JsonSerializer.Serialize(new { Timing = PendingEditDecisionPolicy.TimingFingerprint(PollingChoice.SelectedValue?.ToString(), DebounceInput.Text),
        Slider = Device.Id == "numpad" ? new[] { SliderModeChoice.SelectedIndex.ToString(), SliderMinimum.Text, SliderMaximum.Text, MidiChannel.Text, MidiController.Text } : [] });
    private bool HasPendingEffect => _pendingEditsReady && EffectInputFingerprint() != _effectInputBaseline;
    private bool HasPendingTiming => _pendingEditsReady && TimingInputFingerprint() != _timingInputBaseline;
    private bool HasPendingEdits => HasPendingEffect || HasPendingTiming || HasPendingStandby;
    private void UpdatePendingEditsHint()
    {
        if (!_pendingEditsReady || _refreshingPendingInputs) return;
        if (FindName("PendingEditsHint") is not TextBlock hint) return;
        if (_pendingInputError.Length > 0) { hint.Text = _pendingInputError; hint.Visibility = Visibility.Visible; return; }
        var fields = new List<string>();
        if (HasPendingEffect) fields.Add(L.T("Effekt"));
        if (HasPendingTiming) fields.Add(L.T("Geräteeinstellungen"));
        if (HasPendingStandby) fields.Add(L.T("Standby"));
        hint.Text = fields.Count == 0 ? "" : L.F($"{string.Join(L.T(" und "), fields)}: noch nicht übernommen.");
        hint.Visibility = fields.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Refreshing a caption or selection highlight must not erase a field being edited.
    private void RefreshEffectControls()
    {
        if (!_refreshing && !_applyingPendingEffect && HasPendingEffect) { UpdatePendingEditsHint(); return; }
        var prior = _refreshingPendingInputs; _refreshingPendingInputs = true;
        try { RefreshEffectControlsCore(); _effectInputBaseline = EffectInputFingerprint(); }
        finally { _refreshingPendingInputs = prior; }
        UpdatePendingEditsHint();
    }
    private void RefreshDeviceSettings()
    {
        if (!_refreshing && !_applyingPendingTiming && HasPendingTiming) { UpdatePendingEditsHint(); return; }
        var prior = _refreshingPendingInputs; _refreshingPendingInputs = true;
        try { RefreshDeviceSettingsCore(); _timingInputBaseline = TimingInputFingerprint(); }
        finally { _refreshingPendingInputs = prior; }
        UpdatePendingEditsHint();
    }

    private bool ResolvePendingEdits()
    {
        if (!_pendingEditsReady || _refreshing || _resolvingPendingEdits || !HasPendingEdits) return true;
        _resolvingPendingEdits = true;
        try
        {
            // Screenshots and the existing programmatic smoke setup must never open a modal dialog.
            var choice = _pendingEditDecisionOverride?.Invoke() ?? (_screenshotMode ? PendingEditChoice.Discard : AskPendingEdits());
            return PendingEditDecisionPolicy.Resolve(true, choice, ApplyPendingEdits, DiscardPendingEdits);
        }
        finally { _resolvingPendingEdits = false; UpdatePendingEditsHint(); }
    }
    private bool ValidateEffectInput()
    {
        if (EffectChoice.SelectedItem is not LightingEffectOption option || !LightingEffects.All.Any(o => o.Id == option.Id))
            return PendingInputError(L.T("Bitte einen Effekt auswählen."), EffectChoice);
        if (_effectForSelection && SelectedEffectLeds().Length == 0)
            return PendingInputError(L.T("Für diesen Effekt zuerst eine Taste oder Seiten-LED auswählen, oder die offenen Änderungen verwerfen."), EffectChoice);
        if (!_effectForSelection && EffectTarget.SelectedIndex is < 0 or > 2)
            return PendingInputError(L.T("Bitte den Bereich für den Effekt auswählen."), EffectTarget);
        return true;
    }
    private bool ValidateTimingInput()
    {
        if (PendingEditDecisionPolicy.TryTiming(PollingChoice.SelectedValue?.ToString(), DebounceInput.Text, out _, out _)) return TryGetSliderInput(out _);
        return PendingInputError(L.T("Bitte eine USB-Abfragerate und eine Entprellzeit von 0 bis 40 ms eingeben."), DebounceInput);
    }
    private bool PendingInputError(string message, Control control)
    {
        _pendingInputError = message;
        Status(message);
        if (FindName("PendingEditsHint") is TextBlock hint) { hint.Text = message; hint.Visibility = Visibility.Visible; }
        control.Focus();
        if (control is TextBox text) text.SelectAll();
        return false;
    }
    private bool ApplyPendingEdits()
    {
        var effect = HasPendingEffect; var timing = HasPendingTiming; var standby = HasPendingStandby;
        // Validate the entire pending set before changing either part of the project.
        if (effect && !ValidateEffectInput() || timing && !ValidateTimingInput() || standby && !ValidateStandbyInput()) return false;
        if (effect && !ApplyEffectInputs()) return false;
        if (timing && !ApplyTimingInputs()) return false;
        if (standby && !ApplyStandbyInputs()) return false;
        return !HasPendingEdits;
    }
    private void DiscardPendingEdits()
    {
        _pendingInputError = "";
        var effect = _applyingPendingEffect; var timing = _applyingPendingTiming; var standby = _applyingPendingStandby;
        _applyingPendingEffect = _applyingPendingTiming = _applyingPendingStandby = true;
        try { RefreshEffectControls(); RefreshDeviceSettings(); RefreshStandbyControls(); }
        finally { _applyingPendingEffect = effect; _applyingPendingTiming = timing; _applyingPendingStandby = standby; }
    }
    private void ApplyEffect_Click(object sender, RoutedEventArgs e) => ApplyEffectInputs();
    private bool ApplyEffectInputs()
    {
        if (!ValidateEffectInput()) return false;
        _pendingInputError = "";
        _applyingPendingEffect = true;
        try
        {
            if (!TryApplyEffect()) return false;
            RefreshEffectControls(); return true;
        }
        finally { _applyingPendingEffect = false; UpdatePendingEditsHint(); }
    }
    private void ApplyDeviceSettings_Click(object sender, RoutedEventArgs e) => ApplyTimingInputs();
    private bool ApplyTimingInputs()
    {
        if (!ValidateTimingInput()) return false;
        _pendingInputError = "";
        _applyingPendingTiming = true;
        try
        {
            if (!TryApplyDeviceSettings()) return false;
            RefreshDeviceSettings(); return true;
        }
        finally { _applyingPendingTiming = false; UpdatePendingEditsHint(); }
    }
    private PendingEditChoice AskPendingEdits()
    {
        var fields = new List<string>();
        if (HasPendingEffect) fields.Add(L.T("Effekteinstellungen"));
        if (HasPendingTiming) fields.Add(L.T("Geräteeinstellungen"));
        if (HasPendingStandby) fields.Add(L.T("Standby"));
        var names = string.Join(L.T(" und "), fields);
        return UnsavedChangesWindow.Ask(this, L.T("Änderungen übernehmen?"), L.F($"{names} wurden geändert, aber noch nicht ins Profil übernommen."), saveCaption: L.T("Übernehmen")) switch
        {
            UnsavedChangesChoice.Save => PendingEditChoice.Apply,
            UnsavedChangesChoice.Discard => PendingEditChoice.Discard,
            _ => PendingEditChoice.Cancel
        };
    }
}
