using System.Diagnostics;
using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _startupLoading, _startupStarted, _startupComplete, _startupClosed, _startupFinishing, _loadedOnce;
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly Dictionary<string, StartupDeviceResult> _startupResults = [];

    private string? StartupNotice => _startupResults.TryGetValue(Device.Id, out var result)
        && result.State != StartupDeviceState.OfflineOnly && (!result.Loaded || result.Info?.Persistent != true) ? result.Message : null;

    private void InitializeStartup()
    {
        _startupLoading = !_screenshotMode;
        _startupComplete = _screenshotMode;
        EditorSurface.IsEnabled = !_startupLoading;
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
        _sessions.Clear(); _startupResults.Clear(); _confirmedDeviceSaves.Clear();
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
                opening = new(ProjectService.Clone(result.Project!), null,
                    "Vom Gerät gelesen · Stand " + result.Info!.Generation, null);
                if (result.Info.Persistent)
                    _confirmedDeviceSaves[device.Id] = (Fingerprint(opening.Project), DateTime.Now);
            }
            else opening = openFallback?.Invoke(device.Id) ?? OpenDefaultProject(device.Id, allowDefault: _loadDefaultProjects);
            var fingerprint = Fingerprint(opening.Project);
            _sessions[device.Id] = new(opening.Project, fingerprint, opening.Path, 0, null, [], [], [],
                opening.SourceDescription, fingerprint, opening.Error);
        }
        var selected = StartupDeviceLoader.SelectDevice(preferredId, _startupResults.Values.ToArray());
        RestoreEditorSession(_sessions[selected]);
        RefreshAll();
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
