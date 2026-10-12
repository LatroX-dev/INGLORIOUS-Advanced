using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _applyingPendingStandby, _refreshingStandby;
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
        if (ReferenceEquals(sender, StandbySeconds) && StandbyTimeChoice.SelectedValue?.ToString() != "custom") UpdateStandbyTimeChoice();
        UpdateStandbyAvailability(); PendingInputChanged(sender, e);
    }
    private void StandbyTime_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingStandby || StandbySeconds is null || _refreshingPendingInputs) return;
        if (int.TryParse(StandbyTimeChoice.SelectedValue?.ToString(), out var seconds)) StandbySeconds.Text = seconds.ToString(CultureInfo.InvariantCulture);
        UpdateStandbyAvailability();
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
        StandbyCustomTimePanel.Visibility = StandbyTimeChoice.SelectedValue?.ToString() == "custom" ? Visibility.Visible : Visibility.Collapsed;
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
    private void RefreshLightingTabs()
    {
        foreach (var (button, active) in new[] { (LightColorsTab, !_showLightingEffects), (LightEffectsTab, _showLightingEffects) })
        {
            button.Background = Brush(active ? "#252A2F" : "#1C1F22");
            button.BorderBrush = Brush(active ? "#EF646B" : "#353A40");
            button.Foreground = Brush(active ? "#E4E7EB" : "#A0A7B0");
        }
    }
    private void SetStandbyEditor()
    {
        SetTab(4);
        if (_activeTab == 4) WorkspaceScroll.ScrollToTop();
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
        var oldWidth = Width; var oldHeight = Height;
        try
        {
            Width = 1360; Height = 920;
            foreach (var id in new[] { "pro-iso", "numpad" })
            {
                ResetWorkflowFixture(id); RefreshAll(); SetStandbyEditor();
                var before = Fingerprint(_project); var layers = JsonSerializer.Serialize(_project.Layers); var history = _undo.Count;
                UpdateLayout();
                check(_activeTab == 4 && StandbyPanel.IsVisible && ApplyStandbyHost.IsVisible && DeviceOverview.IsVisible && !InspectorHost.IsVisible && !ColorPanel.IsVisible && SelectionBar.Visibility == Visibility.Collapsed && CurrentLayerChoice.Visibility == Visibility.Collapsed, id + ": standby is in the main Device workspace with its apply button and no layer or key selection");
                StandbyEnabled.IsChecked = true; StandbyTimeChoice.SelectedValue = "60"; StandbyMode.SelectedIndex = 1; StandbyBrightness.Text = "15";
                UpdateLayout();
                check(HasPendingStandby && Fingerprint(_project) == before && StandbySeconds.Text == "60" && !StandbyCustomTimePanel.IsVisible && DevicePendingEditsHint.IsVisible, id + ": standby edits show pending feedback in the main workspace while preset times hide duplicate seconds");
                check(WorkspaceScroll.ScrollableHeight <= 1 && WorkspaceScroll.VerticalOffset == 0 && ApplyTimingButton.IsVisible && ManageFirmwareButton.IsVisible && RestoreOriginalFirmwareButton.IsVisible,
                    id + ": timing, standby and firmware actions fit the normal Device window without scrolling");
                StandbyTimeChoice.SelectedValue = "custom"; StandbySeconds.Text = "300";
                check(StandbyCustomTimePanel.Visibility == Visibility.Visible && StandbyTimeChoice.SelectedValue?.ToString() == "custom", id + ": entering a preset-equivalent custom time keeps the field visible while typing");
                StandbyTimeChoice.SelectedValue = "60";
                _pendingEditDecisionOverride = () => PendingEditChoice.Cancel; SetLightingEditor(false);
                check(_activeTab == 4 && HasPendingStandby, id + ": cancel retains standby view and pending input");
                _pendingEditDecisionOverride = () => PendingEditChoice.Apply; StandbySeconds.Text = "9"; SetTab(5);
                check(_activeTab == 4 && HasPendingStandby && Fingerprint(_project) == before, id + ": invalid standby input blocks navigation without partial changes");
                check(!SaveProject(false) && Fingerprint(_project) == before, id + ": invalid standby input blocks file-save before any dialog or write");
                StandbySeconds.Text = "70"; ApplyUiLanguage("en");
                check(HasPendingStandby && StandbySeconds.Text == "70" && StandbyBrightness.Text == "15", id + ": language refresh preserves pending standby values and baseline");
                ApplyUiLanguage("de"); _pendingEditDecisionOverride = () => PendingEditChoice.Discard; SetLightingEditor(false);
                check(_activeTab == 0 && !HasPendingStandby && Fingerprint(_project) == before, id + ": discard restores saved standby values and opens colors");
                SetStandbyEditor(); StandbyEnabled.IsChecked = true; StandbyTimeChoice.SelectedValue = "60"; StandbyMode.SelectedIndex = 1; StandbyBrightness.Text = "15";
                _pendingEditDecisionOverride = () => PendingEditChoice.Apply; SetLightingEditor(true);
                check(_activeTab == 0 && _showLightingEffects && !HasPendingStandby && _project.Standby.Enabled && _project.Standby.TimeoutSeconds == 60 && _project.Standby.Mode == 1 && _project.Standby.DimBrightnessPercent == 15 && _undo.Count == history + 1, id + ": apply on leaving standby commits one undo entry");
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

                ResetWorkflowFixture(id); SetStandbyEditor();
                StandbyEnabled.IsChecked = true; StandbySeconds.Text = "75";
                PollingChoice.SelectedValue = "4"; DebounceInput.Text = "9";
                ApplyDeviceSettings_Click(this, new RoutedEventArgs());
                check(!HasPendingTiming && HasPendingStandby && StandbySeconds.Text == "75" && !_project.Standby.Enabled && _project.DeviceSettings is { UsbPollingIntervalMs: 4, DebounceMs: 9 }, id + ": applying timing retains the uncommitted standby inputs in the same Device page");
                PollingChoice.SelectedValue = "2"; DebounceInput.Text = "8";
                ApplyStandby_Click(this, new RoutedEventArgs());
                check(!HasPendingStandby && HasPendingTiming && DebounceInput.Text == "8" && _project.Standby is { Enabled: true, TimeoutSeconds: 75 } && _project.DeviceSettings.UsbPollingIntervalMs == 4, id + ": applying standby retains the uncommitted timing inputs");
                before = Fingerprint(_project); StandbySeconds.Text = "9";
                check(!ApplyPendingEdits() && HasPendingTiming && HasPendingStandby && Fingerprint(_project) == before, id + ": Device navigation validates both sections before committing either one");
                StandbySeconds.Text = "90";
                check(ApplyPendingEdits() && !HasPendingEdits && _project.DeviceSettings is { UsbPollingIntervalMs: 2, DebounceMs: 8 } && _project.Standby.TimeoutSeconds == 90, id + ": the combined pending set applies both Device sections without losing values");
            }
        }
        finally { _pendingEditDecisionOverride = null; Width = oldWidth; Height = oldHeight; ResetWorkflowFixture("pro-iso"); RefreshAll(); }
    }
}
