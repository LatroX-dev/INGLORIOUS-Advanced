using System.Diagnostics;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private async Task OpenDeviceTestAsync(string? deviceId = null)
    {
        if (Program.Offline || _firmwareFlow || _busy || _startupLoading || _startupWindowClosed || _allowClose) return;
        var id = deviceId ?? _state.Device.Id;
        if (DeviceCatalog.Get(id).IsOfflineOnly || !await GuardNavigation()) return;
        if (_firmwareFlow || _busy || _startupWindowClosed || _allowClose) return;
        var priorBusy = _busy;
        _firmwareFlow = _busy = true; _root.IsEnabled = false;
        try
        {
            await _sharedBrightness.StopAsync();
            if (DeviceSetupService.IsGloriousCoreRunning() is not false)
            { Status(L.T("Schließe Glorious CORE, bevor du fortfährst.")); return; }
            var matches = (await PlatformDeviceService.FindAsync()).Where(device => device.DeviceId == id).ToArray();
            if (_startupWindowClosed || _allowClose) return;
            if (matches.Length != 1)
            {
                Status(L.T(matches.Length == 0 ? "Verbinde das passende Gerät mit INGLORIOUS-Firmware."
                    : "Schließe für den Test nur ein Gerät dieses Modells an."));
                return;
            }
            await new DeviceTestWindow(id, matches[0]).ShowDialog(this);
        }
        catch (Exception error)
        { Trace.TraceError("Device test failed: {0}", error); Status(DeviceSetupService.ForUser(error)); }
        finally
        {
            _firmwareFlow = false; _busy = priorBusy;
            if (!_startupWindowClosed && !_allowClose)
            { _root.IsEnabled = !_busy; UpdateFooter(); await UpdateBrightnessService(); await RefreshConnectionsAsync(); }
        }
    }
}
