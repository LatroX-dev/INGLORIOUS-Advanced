using System.Diagnostics;
using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _startupLoading, _startupStarted, _startupComplete, _startupClosed, _startupFinishing, _loadedOnce;
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly Dictionary<string, StartupDeviceResult> _startupResults = [];
    private readonly Dictionary<string, uint> _deviceReadCrc = [];
    private readonly Dictionary<string, string> _deviceReadRequired = [];
    private readonly HashSet<string> _configurationRefreshPending = [];
    private readonly Dictionary<string, (AppProject Project, string Fingerprint, string? Path)> _automaticFallbackProjects = [];
    private bool DeviceConfigurationNeedsRead => _deviceReadRequired.ContainsKey(Device.Id);
    private string DeviceConfigurationReadNotice => _deviceReadRequired.GetValueOrDefault(Device.Id)
        ?? L.T("Die aktuelle Konfiguration wurde noch nicht gelesen. Wähle „Von Tastatur laden“, bevor du Änderungen überträgst.");

    private string? StartupNotice => _deviceReadRequired.TryGetValue(Device.Id, out var required) ? required : _startupResults.TryGetValue(Device.Id, out var result)
        && result.State != StartupDeviceState.OfflineOnly && (!result.Loaded || !result.StorageHealthy) ? result.Message : null;

    private void InitializeStartup()
    {
        _startupLoading = !_screenshotMode;
        _startupComplete = _screenshotMode;
        EditorSurface.IsEnabled = !_startupLoading;
        if (!_screenshotMode)
            foreach (var device in DeviceCatalog.All.Where(device => !device.IsOfflineOnly)) RequireDeviceConfigurationRead(device.Id);
    }

    // The optional providers are used only by the offline state checks. A screenshot
    // never reaches descriptor discovery, even if Loaded is raised more than once.
    private async Task LoadStartupDevicesAsync(
        Func<CancellationToken, Task<IReadOnlyList<StartupDeviceResult>>>? read = null,
        Func<string, ProjectOpening>? openFallback = null)
    {
        if (_startupStarted || _startupClosed || (_screenshotMode && read is null)) return;
        _startupStarted = true;
        _startupLoading = true; _startupComplete = false;
        EditorSurface.IsEnabled = false;
        UpdateSourceHint();
        Status(L.T("Konfigurationen werden von den Geräten gelesen …"));
        try
        {
            IReadOnlyList<StartupDeviceResult> results;
            try { results = await (read ?? StartupDeviceLoader.LoadAsync)(_startupCancellation.Token); }
            catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Debug.WriteLine("Startup configuration read failed: " + ex);
                results = DeviceCatalog.All.Select(d => new StartupDeviceResult(d.Id, d.IsOfflineOnly ? StartupDeviceState.OfflineOnly : StartupDeviceState.ReadFailed)).ToArray();
            }
            if (_startupClosed || _startupCancellation.IsCancellationRequested) return;
            ApplyStartupResults(results, _preferences.ResolveStartDevice(), openFallback);
        }
        finally
        {
            _startupLoading = false;
            _startupComplete = !_startupClosed && !_startupCancellation.IsCancellationRequested;
            if (!_startupClosed && !_startupCancellation.IsCancellationRequested)
            {
                EditorSurface.IsEnabled = !_startupFinishing;
                UpdateSourceHint();
                Status(_defaultLoadError ?? StartupNotice ?? L.T(Device.IsOfflineOnly ? "Offline-Profil" : "Konfiguration von Tastatur geladen"));
            }
        }
    }

    private void ApplyStartupResults(IReadOnlyList<StartupDeviceResult> results, string preferredId,
        Func<string, ProjectOpening>? openFallback = null)
    {
        _sessions.Clear(); _startupResults.Clear(); _confirmedDeviceSaves.Clear(); _deviceReadCrc.Clear(); _deviceReadRequired.Clear(); _automaticFallbackProjects.Clear();
        _hardwareProfileBindings.Clear(); _hardwareProfileDrafts.Clear(); _hardwareRefreshObserved.Clear(); _hardwareRefreshRetried.Clear();
        _macroAssigning = false; _selectedSideLeds.Clear();
        _lightingTarget = LightingTarget.Keys;
        foreach (var device in DeviceCatalog.All)
        {
            var matches = results.Where(r => r.DeviceId == device.Id).ToArray();
            var result = device.IsOfflineOnly ? new(device.Id, StartupDeviceState.OfflineOnly) : matches.Length == 1 ? matches[0] : new(device.Id, StartupDeviceState.ReadFailed);
            if (result.State == StartupDeviceState.Loaded && (!result.Loaded || result.Project!.DeviceId != device.Id || result.Info!.DeviceId != device.Id))
                result = new(device.Id, StartupDeviceState.ReadFailed);
            _startupResults[device.Id] = result;
            ProjectOpening opening;
            if (result.Loaded)
            {
                if (result.ProfileSnapshot is { } snapshot) RememberHardwareProfileSnapshot(snapshot);
                _deviceReadCrc[device.Id] = result.Info!.ActiveCrc;
                opening = new(ProjectService.Clone(result.Project!), null,
                    result.ProfileSnapshot is { } loadedProfile ? L.Format("Profil {0} von Tastatur gelesen", loadedProfile.Status.Slot) :
                    "Vom Gerät gelesen · Stand " + result.Info!.Generation, null);
                if (result.StorageHealthy)
                    _confirmedDeviceSaves[device.Id] = (Fingerprint(opening.Project), DateTime.Now);
            }
            else
            {
                if (result.State != StartupDeviceState.OfflineOnly) RequireDeviceConfigurationRead(device.Id, result.Message);
                opening = openFallback?.Invoke(device.Id) ?? OpenDefaultProject(device.Id, allowDefault: _loadDefaultProjects);
                _automaticFallbackProjects[device.Id] = (opening.Project, Fingerprint(opening.Project), opening.Path);
            }
            var fingerprint = Fingerprint(opening.Project);
            _sessions[device.Id] = new(opening.Project, fingerprint, opening.Path, 0, null, [], [], [],
                opening.SourceDescription, fingerprint, opening.Error);
        }
        var selected = StartupDeviceLoader.SelectDevice(preferredId, _startupResults.Values.ToArray());
        RestoreEditorSession(_sessions[selected]);
        RefreshAll();
    }

    private void RequireDeviceConfigurationRead(string deviceId, string? message = null)
    {
        _deviceReadRequired[deviceId] = message ?? L.T("Die aktuelle Konfiguration wurde noch nicht gelesen. Wähle „Von Tastatur laden“, bevor du Änderungen überträgst.");
        _confirmedDeviceSaves.Remove(deviceId);
    }

    private void MarkDeviceConfigurationRead(AppProject project, OnboardInfo? info = null)
    {
        _deviceReadCrc[project.DeviceId] = info?.ActiveCrc ?? OnboardSerializer.Prepare(project).Crc32;
        _deviceReadRequired.Remove(project.DeviceId);
        _automaticFallbackProjects.Remove(project.DeviceId);
        _configurationRefreshPending.Remove(project.DeviceId);
    }

    private void RequestDeviceConfigurationRefresh(IEnumerable<string>? deviceIds = null)
    {
        if (_screenshotMode || _startupClosed || _applicationExitRequested) return;
        _configurationRefreshPending.UnionWith(deviceIds ?? DeviceCatalog.All.Where(device => !device.IsOfflineOnly).Select(device => device.Id));
        if (_startupComplete && CanRefreshConnections()) _ = RefreshDeviceConfigurationsAsync();
    }

    private async Task RefreshDeviceConfigurationsAsync(
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<StartupDeviceResult>>>? read = null)
    {
        if (_configurationRefreshPending.Count == 0 || !_startupComplete || !CanRefreshConnections() || (_screenshotMode && read is null)) return;
        var ids = _configurationRefreshPending.ToArray(); _configurationRefreshPending.Clear();
        var connectionRevision = _connectionRevision;
        var activeId = Device.Id;
        // A text field can still contain uncommitted input after the window was hidden.
        var preserveActive = HasPendingEdits || ProjectName.Text != _project.Name ||
            CurrentMacro is { } macro && MacroName.Text != macro.Name;
        CaptureSession();
        _startupLoading = true; EditorSurface.IsEnabled = false; UpdateSourceHint();
        foreach (var id in ids) RequireDeviceConfigurationRead(id);
        Status(L.T("Konfigurationen werden von den Geräten gelesen …"));
        try
        {
            using var pausedKnob = await PauseKnobStatusAsync();
            await _sharedBrightness.StopAsync();
            IReadOnlyList<StartupDeviceResult> results;
            try { results = await (read ?? StartupDeviceLoader.LoadDevicesAsync)(ids, _startupCancellation.Token); }
            catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Debug.WriteLine("Configuration refresh failed: " + ex);
                results = ids.Select(id => new StartupDeviceResult(id, StartupDeviceState.ReadFailed)).ToArray();
            }
            if (_startupClosed || _startupCancellation.IsCancellationRequested) return;
            if (connectionRevision != _connectionRevision)
            {
                _configurationRefreshPending.UnionWith(ids);
                return;
            }
            results = ids.Select(id => results.Count(result => result.DeviceId == id) == 1
                ? results.Single(result => result.DeviceId == id) : new StartupDeviceResult(id, StartupDeviceState.ReadFailed)).ToArray();
            var replacedActive = ApplyRefreshedDeviceConfigurations(results, activeId, preserveActive);
            if (replacedActive) { RestoreEditorSession(_sessions[activeId]); RefreshAll(); }
            else { BuildDeviceButtons(); UpdateSourceHint(); }
        }
        finally
        {
            _startupLoading = false;
            if (!_startupClosed && !_startupCancellation.IsCancellationRequested)
            {
                EditorSurface.IsEnabled = true; BuildDeviceButtons(); UpdateSourceHint();
                Status(StartupNotice ?? L.T(IsDirty || preserveActive || _projectPath is not null
                    ? "Aktueller Gerätestand gelesen. Deine Änderungen bleiben erhalten."
                    : "Konfiguration von Tastatur geladen")); StartSharedBrightnessIfReady();
            }
        }
    }

    private bool ApplyRefreshedDeviceConfigurations(IReadOnlyList<StartupDeviceResult> results, string activeId, bool preserveActive)
    {
        var replacedActive = false;
        foreach (var group in results.Where(result => DeviceCatalog.All.Any(device => device.Id == result.DeviceId)).GroupBy(result => result.DeviceId))
        {
            var id = group.Key;
            var result = group.Count() == 1 ? group.Single() : new StartupDeviceResult(id, StartupDeviceState.ReadFailed);
            if (!result.Loaded || result.Project!.DeviceId != id || result.Info!.DeviceId != id)
            {
                if (result.State == StartupDeviceState.Loaded) result = new(id, StartupDeviceState.ReadFailed);
                _startupResults[id] = result; RequireDeviceConfigurationRead(id, result.Message);
                RetryHardwareProfileRead(id); continue;
            }
            var automatic = _sessions.TryGetValue(id, out var fallbackSession) && _automaticFallbackProjects.TryGetValue(id, out var fallback) &&
                ReferenceEquals(fallback.Project, fallbackSession.Project) && fallback.Fingerprint == Fingerprint(fallbackSession.Project) && fallback.Path == fallbackSession.Path;
            if (result.ProfileSnapshot is not null)
            {
                _startupResults[id] = result;
                replacedActive |= ApplyHardwareProfileRefresh(result, activeId, preserveActive, automatic);
                continue;
            }
            _hardwareProfileBindings.Remove(id);
            var preserve = _sessions.TryGetValue(id, out var session) &&
                (Fingerprint(session.Project) != session.SavedFingerprint ||
                 session.Path is not null && !automatic || id == activeId && preserveActive);
            var previousKnown = _deviceReadCrc.TryGetValue(id, out var previousCrc);
            var changed = !previousKnown || previousCrc != result.Info.ActiveCrc;
            _startupResults[id] = result;
            if (preserve && changed)
            {
                RequireDeviceConfigurationRead(id, previousKnown ? L.T("Die Konfiguration auf der Tastatur hat sich geändert. Deine Änderungen bleiben erhalten. Lade zuerst den aktuellen Gerätestand.") : null);
                continue;
            }
            MarkDeviceConfigurationRead(result.Project, result.Info);
            if (result.Info.Persistent) _confirmedDeviceSaves[id] = (Fingerprint(result.Project), DateTime.Now);
            else _confirmedDeviceSaves.Remove(id);
            if (preserve) continue;
            var project = ProjectService.Clone(result.Project);
            var fingerprint = Fingerprint(project);
            _sessions[id] = new(project, fingerprint, null, session?.Layer ?? 0, null, session?.Selection ?? [], [], [],
                "Vom Gerät gelesen · Stand " + result.Info.Generation, fingerprint, null);
            replacedActive |= id == activeId;
        }
        return replacedActive;
    }

    private void RestoreEditorSession(EditorSession session)
    {
        _selection.Clear(); _undo.Clear(); _redo.Clear();
        _project = session.Project; _layerIndex = session.Layer; _projectPath = session.Path;
        _savedFingerprint = session.SavedFingerprint; _macroId = session.MacroId;
        _sourceDescription = session.SourceDescription; _sourceFingerprint = session.SourceFingerprint;
        _defaultLoadError = session.LoadError;
        foreach (var key in session.Selection) _selection.Add(key);
        foreach (var state in session.Undo.Reverse()) _undo.Push(state);
        foreach (var state in session.Redo.Reverse()) _redo.Push(state);
    }

    private void StartSharedBrightnessIfReady()
    {
        if (_startupComplete && !_startupFinishing && !_startupClosed && !_readingDevice && !_checkingFirmwareGuide
            && !_screenshotMode && (IsVisible || _backgroundSession) && _preferences.SharedBrightness)
            _sharedBrightness.Start();
    }
}
