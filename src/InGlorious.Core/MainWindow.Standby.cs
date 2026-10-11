using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _showLightingStandby, _applyingPendingStandby, _refreshingStandby;
    private string _standbyInputBaseline = "";
    private string StandbyInputFingerprint() => JsonSerializer.Serialize(new
    {
        Enabled = StandbyEnabled.IsChecked == true,
        Seconds = int.TryParse(StandbySeconds.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds.ToString(CultureInfo.InvariantCulture) : StandbySeconds.Text,
        Mode = StandbyMode.SelectedIndex,
        Brightness = int.TryParse(StandbyBrightness.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var brightness) ? brightness.ToString(CultureInfo.InvariantCulture) : StandbyBrightness.Text
    });
    private bool HasPendingStandby => _pendingEditsReady && StandbyInputFingerprint() != _standbyInputBaseline;
    private void InitializeStandbyInputs()
    {
        _standbyInputBaseline = StandbyInputFingerprint();
        StandbyEnabled.Checked += StandbyInputChanged; StandbyEnabled.Unchecked += StandbyInputChanged;
        StandbySeconds.TextChanged += StandbyInputChanged;
        StandbyMode.SelectionChanged += StandbyInputChanged;
        StandbyBrightness.TextChanged += StandbyInputChanged;
    }
    private void StandbyInputChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshingStandby) return;
        if (ReferenceEquals(sender, StandbySeconds)) UpdateStandbyTimeChoice();
        UpdateStandbyAvailability(); PendingInputChanged(sender, e);
    }
    private void StandbyTime_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingStandby || StandbySeconds is null || _refreshingPendingInputs) return;
        if (int.TryParse(StandbyTimeChoice.SelectedValue?.ToString(), out var seconds)) StandbySeconds.Text = seconds.ToString(CultureInfo.InvariantCulture);
    }
    private void UpdateStandbyTimeChoice()
    {
        var prior = _refreshingStandby; _refreshingStandby = true;
        try
        {
            var text = int.TryParse(StandbySeconds.Text, out var value) ? value.ToString(CultureInfo.InvariantCulture) : "custom";
            StandbyTimeChoice.SelectedValue = StandbyTimeChoice.Items.Cast<ComboBoxItem>().Any(item => (string)item.Tag == text) ? text : "custom";
        }
        finally { _refreshingStandby = prior; }
    }
    private void UpdateStandbyAvailability()
    {
        StandbyOptions.IsEnabled = StandbyEnabled.IsChecked == true;
        StandbyBrightnessPanel.Visibility = StandbyMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        StandbyScreensaverHint.Visibility = StandbyMode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshStandbyControls()
    {
        if (!_refreshing && !_applyingPendingStandby && HasPendingStandby) { UpdatePendingEditsHint(); return; }
        var prior = _refreshingPendingInputs; _refreshingPendingInputs = true; _refreshingStandby = true;
        try
        {
            var settings = _project.Standby;
            StandbyEnabled.IsChecked = settings.Enabled;
            StandbySeconds.Text = settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            StandbyMode.SelectedIndex = settings.Mode;
            StandbyBrightness.Text = settings.DimBrightnessPercent.ToString(CultureInfo.InvariantCulture);
            UpdateStandbyTimeChoice(); UpdateStandbyAvailability(); _standbyInputBaseline = StandbyInputFingerprint();
        }
        finally { _refreshingStandby = false; _refreshingPendingInputs = prior; }
        UpdatePendingEditsHint();
    }
    private void RefreshStandbySurface()
    {
        StandbyPanel.Visibility = _showLightingStandby ? Visibility.Visible : Visibility.Collapsed;
        ApplyStandbyHost.Visibility = _activeTab == 0 && _showLightingStandby ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, active) in new[] { (LightColorsTab, !_showLightingEffects && !_showLightingStandby), (LightEffectsTab, _showLightingEffects && !_showLightingStandby), (LightStandbyTab, _showLightingStandby) })
        {
            button.Background = Brush(active ? "#3A252B" : "#252A2F"); button.BorderBrush = Brush(active ? "#A74B54" : "#353A40");
        }
        if (!_showLightingStandby) return;
        StaticColorsPanel.Visibility = EffectsPanel.Visibility = EffectScopeButtons.Visibility = LightingSelectionPanel.Visibility = Visibility.Collapsed;
        ApplyEffectHost.Visibility = Visibility.Collapsed;
        if (_activeTab == 0) { InspectorTitle.Text = L.T("Standby"); InspectorSubtitle.Text = Device.Name + L.T(" · alle Ebenen"); }
    }
    private void LightStandby_Click(object sender, RoutedEventArgs e) => SetStandbyEditor();
    private void SetStandbyEditor()
    {
        if (!_showLightingStandby && !ResolvePendingEdits()) return;
        _showLightingStandby = true; _showLightingEffects = false;
        RenderKeyboard(); RefreshInspector();
    }
    private bool ValidateStandbyInput()
    {
        if (!int.TryParse(StandbySeconds.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 10 or > 3600)
            return PendingInputError(L.T("Die Wartezeit muss zwischen 10 und 3600 Sekunden liegen."), StandbySeconds);
        if (StandbyMode.SelectedIndex is < 0 or > 2) return PendingInputError(L.T("Ungültige Standby-Einstellungen."), StandbyMode);
        if (!int.TryParse(StandbyBrightness.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var brightness) || brightness is < 1 or > 100)
            return PendingInputError(L.T("Die Helligkeit muss zwischen 1 und 100 Prozent liegen."), StandbyBrightness);
        return true;
    }
    private void ApplyStandby_Click(object sender, RoutedEventArgs e) => ApplyStandbyInputs();
    private bool ApplyStandbyInputs()
    {
        if (!ValidateStandbyInput()) return false;
        _pendingInputError = ""; _applyingPendingStandby = true;
        try
        {
            var value = new LightingStandbySettings { Enabled = StandbyEnabled.IsChecked == true, TimeoutSeconds = int.Parse(StandbySeconds.Text, CultureInfo.InvariantCulture), Mode = StandbyMode.SelectedIndex, DimBrightnessPercent = int.Parse(StandbyBrightness.Text, CultureInfo.InvariantCulture) };
            if (JsonSerializer.Serialize(value) != JsonSerializer.Serialize(_project.Standby))
            {
                BeginChange(); _project.Standby = value; FinishChange(L.T("Standby-Einstellungen übernommen."));
            }
            RefreshStandbyControls(); return true;
        }
        finally { _applyingPendingStandby = false; UpdatePendingEditsHint(); }
    }
    private void RunStandbyChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Offline checks only.");
        try
        {
            foreach (var id in new[] { "pro-iso", "numpad" })
            {
                ResetWorkflowFixture(id); _showLightingStandby = false; RefreshAll(); SetStandbyEditor();
                var before = Fingerprint(_project); var layers = JsonSerializer.Serialize(_project.Layers); var history = _undo.Count;
                check(StandbyPanel.Visibility == Visibility.Visible && ApplyStandbyHost.Visibility == Visibility.Visible && StaticColorsPanel.Visibility == Visibility.Collapsed && SelectionBar.Visibility == Visibility.Collapsed && CurrentLayerChoice.Visibility == Visibility.Collapsed, id + ": standby is a flat device-wide lighting view with a visible apply button");
                StandbyEnabled.IsChecked = true; StandbyTimeChoice.SelectedValue = "60"; StandbyMode.SelectedIndex = 1; StandbyBrightness.Text = "15";
                check(HasPendingStandby && Fingerprint(_project) == before && StandbySeconds.Text == "60", id + ": standby edits and preset time stay pending without changing the profile");
                _pendingEditDecisionOverride = () => PendingEditChoice.Cancel; SetLightingEditor(false);
                check(_showLightingStandby && HasPendingStandby, id + ": cancel retains standby view and pending input");
                _pendingEditDecisionOverride = () => PendingEditChoice.Apply; StandbySeconds.Text = "9"; SetTab(5);
                check(_activeTab == 0 && HasPendingStandby && Fingerprint(_project) == before, id + ": invalid standby input blocks navigation without partial changes");
                check(!SaveProject(false) && Fingerprint(_project) == before, id + ": invalid standby input blocks file-save before any dialog or write");
                StandbySeconds.Text = "70"; ApplyUiLanguage("en");
                check(HasPendingStandby && StandbySeconds.Text == "70" && StandbyBrightness.Text == "15", id + ": language refresh preserves pending standby values and baseline");
                ApplyUiLanguage("de"); _pendingEditDecisionOverride = () => PendingEditChoice.Discard; SetLightingEditor(false);
                check(!_showLightingStandby && !HasPendingStandby && Fingerprint(_project) == before, id + ": discard restores saved standby values and opens colors");
                SetStandbyEditor(); StandbyEnabled.IsChecked = true; StandbyTimeChoice.SelectedValue = "60"; StandbyMode.SelectedIndex = 1; StandbyBrightness.Text = "15";
                _pendingEditDecisionOverride = () => PendingEditChoice.Apply; SetLightingEditor(true);
                check(!_showLightingStandby && _showLightingEffects && !HasPendingStandby && _project.Standby.Enabled && _project.Standby.TimeoutSeconds == 60 && _project.Standby.Mode == 1 && _project.Standby.DimBrightnessPercent == 15 && _undo.Count == history + 1, id + ": apply on leaving standby commits one undo entry");
                check(JsonSerializer.Serialize(_project.Layers) == layers, id + ": device-wide standby preserves every layer color, binding, effect and name");
                Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == before, id + ": undo restores complete pre-standby profile");
                Redo_Click(this, new RoutedEventArgs()); check(_project.Standby.Enabled && StandbySeconds.Text == "60" && !HasPendingStandby, id + ": redo restores standby and resets the pending baseline");
                SetStandbyEditor(); StandbyEnabled.IsChecked = false; check(ApplyStandbyInputs() && !_project.Standby.Enabled && _project.Standby.Mode == 1 && _project.Standby.TimeoutSeconds == 60, id + ": disabling standby preserves the chosen time and action");
                StandbyEnabled.IsChecked = true; StandbySeconds.Text = "3600"; StandbyBrightness.Text = "100";
                check(ApplyStandbyInputs(), id + ": maximum time and dim brightness are accepted");
                StandbySeconds.Text = "10"; StandbyBrightness.Text = "1"; check(ApplyStandbyInputs(), id + ": minimum time and dim brightness are accepted");
                StandbyBrightness.Text = "0"; check(!ApplyStandbyInputs() && HasPendingStandby, id + ": invalid dim brightness cannot be silently applied");
                DiscardPendingEdits();
                var beforeScreensaver = Fingerprint(_project); var beforeScreensaverHistory = _undo.Count;
                StandbyMode.SelectedIndex = 2;
                check(StandbyMode.Items.Count == 3 && HasPendingStandby && Fingerprint(_project) == beforeScreensaver && StandbyBrightnessPanel.Visibility == Visibility.Collapsed && StandbyScreensaverHint.Visibility == Visibility.Visible, id + ": selecting screensaver stays pending, hides dimming and explains the repeating sequence");
                check(ApplyStandbyInputs() && _project.Standby is { Enabled: true, Mode: 2, TimeoutSeconds: 10, DimBrightnessPercent: 1 } && _undo.Count == beforeScreensaverHistory + 1, id + ": screensaver commits once and preserves timeout and stored dim brightness");
                var screensaverProfile = ProjectService.Clone(_project);
                check(screensaverProfile.Standby.Mode == 2 && Fingerprint(screensaverProfile) == Fingerprint(_project), id + ": screensaver survives the profile JSON roundtrip");
                var screensaverImage = OnboardSerializer.Prepare(_project);
                check(screensaverImage.SchemaVersion == 4 && OnboardSerializer.Decode(screensaverImage.Payload).Standby.Mode == 2, id + ": screensaver is preserved in the onboard configuration");
                check(JsonSerializer.Serialize(_project.Layers) == layers, id + ": screensaver leaves all layer settings unchanged");
                Undo_Click(this, new RoutedEventArgs());
                check(Fingerprint(_project) == beforeScreensaver && StandbyMode.SelectedIndex == 1 && StandbyBrightnessPanel.Visibility == Visibility.Visible && StandbyScreensaverHint.Visibility == Visibility.Collapsed, id + ": undo restores dimming and its controls exactly");
                Redo_Click(this, new RoutedEventArgs());
                check(_project.Standby.Mode == 2 && StandbyMode.SelectedIndex == 2 && !HasPendingStandby && StandbyScreensaverHint.Visibility == Visibility.Visible, id + ": redo restores screensaver without pending edits");
                StandbyEnabled.IsChecked = false;
                check(ApplyStandbyInputs() && !_project.Standby.Enabled && _project.Standby.Mode == 2 && !StandbyOptions.IsEnabled, id + ": disabling standby keeps the selected screensaver action");
                SetLightingEditor(false);
            }
        }
        finally { _pendingEditDecisionOverride = null; _showLightingStandby = false; ResetWorkflowFixture("pro-iso"); RefreshAll(); }
    }
}
