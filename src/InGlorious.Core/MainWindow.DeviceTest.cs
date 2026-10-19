using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private async void DeviceTest_Click(object sender, RoutedEventArgs e)
    {
        if (_readingDevice || _checkingFirmwareGuide || _startupLoading || _startupClosed) return;
        _checkingFirmwareGuide = true;
        try
        {
            using var pausedKnob = await PauseKnobStatusAsync();
            await _sharedBrightness.StopAsync();
            var devices = _screenshotMode ? [] : await Task.Run(OnboardClient.FindDevices);
            var matches = devices.Where(item => item.DeviceId == Device.Id).ToArray();
            if (!_screenshotMode && matches.Length != 1) { Status(L.T("Verbinde genau eine Tastatur dieses Modells für den Gerätetest.")); return; }
            new DeviceTestWindow(Device.Id, matches.SingleOrDefault(), _screenshotMode) { Owner = this }.ShowDialog();
        }
        catch (Exception) { Status(L.T("Der Gerätetest konnte nicht gestartet werden. Prüfe die USB-Verbindung.")); }
        finally { _checkingFirmwareGuide = false; if (!_startupClosed) { RequestConnectionRefresh(); StartSharedBrightnessIfReady(); } }
    }
}
