using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private readonly SharedBrightnessService _sharedBrightness = new();
    private bool _syncControlRefreshing;
    internal async Task<bool> PauseSharedBrightnessForLightingTestAsync()
    {
        var resume = _sharedBrightness.Running;
        await _sharedBrightness.StopAsync();
        return resume;
    }

    internal void ResumeSharedBrightnessAfterLightingTest(bool resume)
    {
        if (resume && IsLoaded) StartSharedBrightnessIfReady();
    }
    private void InitializeSharedBrightness()
    {
        _sharedBrightness.StatusChanged += message => Dispatcher.BeginInvoke(() =>
        {
            SharedBrightnessStatus.Text = message;
            SharedBrightnessStatus.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        });
        RefreshSharedBrightness();
    }
    private void RefreshSharedBrightness()
    {
        _syncControlRefreshing = true;
        SharedBrightnessToggle.IsChecked = _preferences.SharedBrightness;
        SharedBrightnessInfo.Text = L.T(SharedBrightnessService.Notice);
        _syncControlRefreshing = false;
    }
    private async void SharedBrightness_Click(object sender, RoutedEventArgs e)
    {
        if (_syncControlRefreshing || _screenshotMode || _startupLoading) { RefreshSharedBrightness(); return; }
        try
        {
            var edit = AppPreferencesStore.BeginEdit();
            var changed = edit.Preferences with { SharedBrightness = SharedBrightnessToggle.IsChecked == true };
            AppPreferencesStore.Save(edit, changed); _preferences = changed;
            await _sharedBrightness.StopAsync();
            StartSharedBrightnessIfReady();
        }
        catch (Exception ex) { ShowError(L.T("Gemeinsames Dimmen konnte nicht geändert werden."), ex); }
        RefreshSharedBrightness();
    }
    private void ShowBackgroundNotice()
    {
        if (_screenshotMode || _preferences.SharedBrightnessNoticeDismissed) return;
        UnsavedChangesWindow.ShowInformation(this, L.T("Info zur Beleuchtung"), L.T(SharedBrightnessService.Notice));
        try
        {
            var edit = AppPreferencesStore.BeginEdit();
            var updated = edit.Preferences with { SharedBrightnessNoticeDismissed = true };
            AppPreferencesStore.Save(edit, updated); _preferences = updated;
        }
        catch (Exception ex) { Status(ex.Message); }
    }
}
