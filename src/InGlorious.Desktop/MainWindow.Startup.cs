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
    private readonly Dictionary<string, uint> _deviceReadCrc = [];
    private readonly Dictionary<string, string> _deviceReadRequired = [];
    private readonly HashSet<string> _configurationRefreshPending = [];
    private readonly Dictionary<string, (EditorState Session, string Fingerprint, string? Path)> _automaticFallbackSessions = [];
    private bool DeviceConfigurationNeedsRead => _deviceReadRequired.ContainsKey(_state.Device.Id);
    private string DeviceConfigurationReadNotice => _deviceReadRequired.GetValueOrDefault(_state.Device.Id)
        ?? L.T("Die aktuelle Konfiguration wurde noch nicht gelesen. Wähle „Von Tastatur laden“, bevor du Änderungen überträgst.");

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
        _automaticFallbackSessions.Clear(); _deviceReadCrc.Clear(); _deviceReadRequired.Clear();
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
                if (result.State != StartupDeviceState.OfflineOnly) RequireDeviceConfigurationRead(result.DeviceId, result.Message);
                if (result.State != StartupDeviceState.OfflineOnly) _startupFallbackNotices[result.DeviceId] = result.Message;
                // Only the untouched startup placeholder is discarded. Existing local sessions remain available.
                if (_startupPlaceholders.Remove(result.DeviceId)) _sessions.Remove(result.DeviceId);
                continue;
            }
            _startupPlaceholders.Remove(result.DeviceId);
            MarkDeviceConfigurationRead(result.Project!, result.Info!);
            _sessions[result.DeviceId] = session;
            _startupReadFingerprints[result.DeviceId] = EditorState.Fingerprint(session.Project);
            if (result.Info!.Persistent) _confirmed[result.DeviceId] = EditorState.Fingerprint(session.Project);
            else _startupFallbackNotices[result.DeviceId] = L.T("Das Gerät meldet keinen verfügbaren dauerhaften Speicher.");
        }
        _state = OpenSession(StartupDeviceLoader.SelectDevice(preferred, results));
        foreach (var result in results.Where(result => !result.Loaded))
            if (_sessions.TryGetValue(result.DeviceId, out var fallback)) RegisterAutomaticFallback(fallback);
        _pendingApply = null;
        _macroId = null;
    }

    private void RequireDeviceConfigurationRead(string deviceId, string? message = null)
    {
        var notice = message ?? L.T("Die aktuelle Konfiguration wurde noch nicht gelesen. Wähle „Von Tastatur laden“, bevor du Änderungen überträgst.");
        _deviceReadRequired[deviceId] = notice;
        _startupFallbackNotices[deviceId] = notice;
        _startupNotices[deviceId] = notice;
        _confirmed.Remove(deviceId);
    }

    private void RegisterAutomaticFallback(EditorState session) => _automaticFallbackSessions[session.Device.Id] =
        (session, EditorState.Fingerprint(session.Project), session.Path);

    private void MarkDeviceConfigurationRead(AppProject project, OnboardInfo? info = null)
    {
        _deviceReadCrc[project.DeviceId] = info?.ActiveCrc ?? OnboardSerializer.Prepare(project).Crc32;
        _deviceReadRequired.Remove(project.DeviceId);
        _automaticFallbackSessions.Remove(project.DeviceId);
        _configurationRefreshPending.Remove(project.DeviceId);
    }

    private void RequestDeviceConfigurationRefresh(IEnumerable<string>? deviceIds = null)
    {
        if (Program.Offline || _startupWindowClosed || _allowClose || _applicationExitRequested) return;
        _configurationRefreshPending.UnionWith(deviceIds ?? DeviceCatalog.All.Where(device => !device.IsOfflineOnly).Select(device => device.Id));
        if (_startupStarted && !_initialConnectionsPending && CanRefreshConnections) _ = RefreshDeviceConfigurationsAsync();
    }

    private async Task RefreshDeviceConfigurationsAsync(
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<StartupDeviceResult>>>? read = null)
    {
        if (_configurationRefreshPending.Count == 0 || !CanRefreshConnections || (Program.Offline && read is null)) return;
        var ids = _configurationRefreshPending.ToArray(); _configurationRefreshPending.Clear();
        var active = _state;
        var preserveActive = _pendingApply is not null;
        _startupLoading = _busy = true; _root.IsEnabled = false;
        foreach (var id in ids) RequireDeviceConfigurationRead(id);
        using var cancellation = _startupCancellation = new CancellationTokenSource();
        Status(L.T("Konfigurationen werden von den Geräten gelesen …"));
        try
        {
            await _sharedBrightness.StopAsync();
            IReadOnlyList<StartupDeviceResult> results;
            try { results = await (read ?? StartupDeviceLoader.LoadDevicesAsync)(ids, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                Trace.TraceError("Configuration refresh failed: {0}", error);
                results = ids.Select(id => new StartupDeviceResult(id, StartupDeviceState.ReadFailed)).ToArray();
            }
            if (_startupWindowClosed || _allowClose) return;
            results = ids.Select(id => results.Count(result => result.DeviceId == id) == 1
                ? results.Single(result => result.DeviceId == id) : new StartupDeviceResult(id, StartupDeviceState.ReadFailed)).ToArray();
            ApplyRefreshedDeviceConfigurations(results, active.Device.Id, preserveActive);
            if (!ReferenceEquals(active, _state)) Build();
        }
        finally
        {
            _startupCancellation = null; _startupLoading = _busy = false;
            if (!_startupWindowClosed && !_allowClose)
            {
                _root.IsEnabled = true; UpdateConnectionSelector(); UpdateFooter();
                ShowStartupNotice(); await UpdateBrightnessService();
            }
        }
    }

    private void ApplyRefreshedDeviceConfigurations(IReadOnlyList<StartupDeviceResult> results, string activeId, bool preserveActive)
    {
        if (_startupWindowClosed || _allowClose) return;
        foreach (var group in results.Where(result => DeviceCatalog.All.Any(device => device.Id == result.DeviceId)).GroupBy(result => result.DeviceId))
        {
            var id = group.Key;
            var result = group.Count() == 1 ? group.Single() : new StartupDeviceResult(id, StartupDeviceState.ReadFailed);
            if (!result.Loaded || result.Project!.DeviceId != id || result.Info!.DeviceId != id)
            {
                if (result.State == StartupDeviceState.Loaded) result = new(id, StartupDeviceState.ReadFailed);
                RequireDeviceConfigurationRead(id, result.Message); continue;
            }
            var automatic = _sessions.TryGetValue(id, out var fallbackSession) && _automaticFallbackSessions.TryGetValue(id, out var fallback) &&
                ReferenceEquals(fallback.Session, fallbackSession) && fallback.Fingerprint == EditorState.Fingerprint(fallbackSession.Project) && fallback.Path == fallbackSession.Path;
            var preserve = _sessions.TryGetValue(id, out var session) &&
                (session.Dirty || session.Path is not null && !automatic || id == activeId && preserveActive);
            var previousKnown = _deviceReadCrc.TryGetValue(id, out var previousCrc);
            var changed = !previousKnown || previousCrc != result.Info.ActiveCrc;
            if (preserve && changed)
            {
                RequireDeviceConfigurationRead(id, previousKnown ? L.T("Die Konfiguration auf der Tastatur hat sich geändert. Deine Änderungen bleiben erhalten. Lade zuerst den aktuellen Gerätestand.") : null);
                continue;
            }
            MarkDeviceConfigurationRead(result.Project, result.Info);
            _startupFallbackNotices.Remove(id);
            if (result.Info.Persistent) _confirmed[id] = EditorState.Fingerprint(result.Project);
            else { _confirmed.Remove(id); _startupFallbackNotices[id] = result.Message; }
            _startupNotices[id] = preserve ? L.T("Aktueller Gerätestand gelesen. Deine Änderungen bleiben erhalten.") : result.Message;
            _startupReadFingerprints[id] = EditorState.Fingerprint(preserve ? session!.Project : result.Project);
            if (preserve) continue;
            var replacement = EditorState.FromDevice(result.Project);
            replacement.LayerIndex = session?.LayerIndex ?? 0;
            _sessions[id] = replacement;
            if (id == activeId) { _state = replacement; _macroId = null; }
        }
    }

    private void ShowStartupNotice()
    {
        ClearChangedStartupNotice();
        Status(_startupNotices.TryGetValue(_state.Device.Id, out var notice) ? notice : L.T("Bereit"));
    }

    private void ClearChangedStartupNotice()
    {
        var id = _state.Device.Id;
        if (!_startupReadFingerprints.TryGetValue(id, out var read) || read == EditorState.Fingerprint(_state.Project)) return;
        _startupReadFingerprints.Remove(id);
        if (_startupNotices.Remove(id, out var notice) && _status == notice) Status(L.T("Bereit"));
    }
}
