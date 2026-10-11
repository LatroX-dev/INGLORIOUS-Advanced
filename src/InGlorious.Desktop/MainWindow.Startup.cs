using InGlorious.Core;
using System.Diagnostics;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private bool _startupLoading, _startupStarted, _startupWindowClosed;
    private CancellationTokenSource? _startupCancellation;
    private readonly Dictionary<string, string> _startupNotices = [];
    private readonly Dictionary<string, string> _startupReadFingerprints = [];
    private readonly Dictionary<string, string> _startupFallbackNotices = [];
    private readonly HashSet<string> _startupPlaceholders = [];

    private async Task LoadStartupDevicesAsync()
    {
        // Screenshots and smoke checks return before discovery or any device read.
        if (Program.Offline || _startupStarted || _startupWindowClosed || _allowClose) return;
        _startupStarted = _startupLoading = _busy = true;
        using var cancellation = _startupCancellation = new CancellationTokenSource();
        _root.IsEnabled = false;
        Status(L.T("Konfigurationen werden von den Geräten gelesen …"));
        try
        {
            var results = await StartupDeviceLoader.LoadAsync(cancellation.Token);
            ApplyStartupResults(results);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            Trace.TraceError("Startup configuration read failed: {0}", error);
            ApplyStartupResults(DeviceCatalog.All.Select(device =>
                new StartupDeviceResult(device.Id, device.IsOfflineOnly ? StartupDeviceState.OfflineOnly : StartupDeviceState.ReadFailed)).ToArray());
        }
        finally
        {
            _startupLoading = _busy = false;
            _startupCancellation = null;
            if (!_startupWindowClosed && !_allowClose) { Build(); ShowStartupNotice(); }
        }
    }

    private void ApplyStartupResults(IReadOnlyList<StartupDeviceResult> results)
    {
        if (_startupWindowClosed || _allowClose) return;
        var preferred = _preferences.ResolveStartDevice();
        results = DeviceCatalog.All.Select(device =>
        {
            var matches = results.Where(result => result.DeviceId == device.Id).ToArray();
            var result = device.IsOfflineOnly ? new StartupDeviceResult(device.Id, StartupDeviceState.OfflineOnly) : matches.Length == 1 ? matches[0] : new StartupDeviceResult(device.Id, StartupDeviceState.ReadFailed);
            return result.State == StartupDeviceState.Loaded && (!result.Loaded ||
                result.Project!.DeviceId != device.Id || result.Info!.DeviceId != device.Id)
                ? new StartupDeviceResult(device.Id, device.IsOfflineOnly ? StartupDeviceState.OfflineOnly : StartupDeviceState.ReadFailed) : result;
        }).ToArray();
        var loaded = new Dictionary<string, EditorState>();
        foreach (var result in results)
        {
            if (!DeviceCatalog.All.Any(device => device.Id == result.DeviceId)) continue;
            if (!result.Loaded) continue;
            loaded.Add(result.DeviceId, EditorState.FromDevice(result.Project!));
        }
        foreach (var result in results)
        {
            if (!DeviceCatalog.All.Any(device => device.Id == result.DeviceId)) continue;
            _startupNotices[result.DeviceId] = result.Message;
            _startupReadFingerprints.Remove(result.DeviceId);
            _confirmed.Remove(result.DeviceId);
            _startupFallbackNotices.Remove(result.DeviceId);
            if (!loaded.TryGetValue(result.DeviceId, out var session))
            {
                if (result.State != StartupDeviceState.OfflineOnly) _startupFallbackNotices[result.DeviceId] = result.Message;
                // Only the untouched startup placeholder is discarded. Existing local sessions remain available.
                if (_startupPlaceholders.Remove(result.DeviceId)) _sessions.Remove(result.DeviceId);
                continue;
            }
            _startupPlaceholders.Remove(result.DeviceId);
            _sessions[result.DeviceId] = session;
            _startupReadFingerprints[result.DeviceId] = EditorState.Fingerprint(session.Project);
            if (result.Info!.Persistent) _confirmed[result.DeviceId] = EditorState.Fingerprint(session.Project);
            else _startupFallbackNotices[result.DeviceId] = L.T("Das Gerät meldet keinen verfügbaren dauerhaften Speicher.");
        }
        _state = OpenSession(StartupDeviceLoader.SelectDevice(preferred, results));
        _pendingApply = null;
        _macroId = null;
    }

    private void ShowStartupNotice()
    {
        ClearChangedStartupNotice();
        if (_startupNotices.TryGetValue(_state.Device.Id, out var notice)) Status(notice);
    }

    private void ClearChangedStartupNotice()
    {
        var id = _state.Device.Id;
        if (!_startupReadFingerprints.TryGetValue(id, out var read) || read == EditorState.Fingerprint(_state.Project)) return;
        _startupReadFingerprints.Remove(id);
        if (_startupNotices.Remove(id, out var notice) && _status == notice) Status(L.T("Bereit"));
    }
}
