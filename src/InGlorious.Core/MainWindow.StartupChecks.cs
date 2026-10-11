using System.IO;
using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private async Task RunStartupChecksAsync(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Startup checks require offline UI-smoke mode.");
        var preferences = _preferences;
        var started = _startupStarted; var complete = _startupComplete;
        var resultsBefore = _startupResults.ToArray();
        var confirmedBefore = _confirmedDeviceSaves.ToArray();
        try
        {
            ResetWorkflowFixture("pro-iso");
            _startupStarted = false;
            await LoadStartupDevicesAsync();
            check(!_startupStarted && !_startupLoading && EditorSurface.IsEnabled,
                "Startup: screenshot mode skips the production device loader entirely");

            var pro = ProjectService.Create("pro-iso"); pro.Name = "Current PRO";
            pro.Layers[0].Keys.First().Value.Color = "#2187CB";
            pro.Layers[2].Name = "Onboard layer";
            var pad = ProjectService.Create("numpad"); pad.Name = "Current Numpad";
            pad.Layers[0].Keys.First().Value.Color = "#CB2145";
            static StartupDeviceResult Loaded(AppProject project, bool persistent = true) =>
                new(project.DeviceId, StartupDeviceState.Loaded, project,
                    new(project.DeviceId, (byte)(persistent ? 1 : 0), 3780, 7, 123, 0));
            IReadOnlyList<StartupDeviceResult> both = [Loaded(pro), Loaded(pad)];
            ProjectOpening Fallback(string id) => new(ProjectService.Create(id), id + ".inglorious.json", "Gespeichertes Profil · nicht mit dem Gerät abgeglichen", null);
            var fallbackCount = 0;
            ProjectOpening CountedFallback(string id) { fallbackCount++; return Fallback(id); }
            _preferences = _preferences with { StartDevice = "numpad", SharedBrightness = true };
            var pending = new TaskCompletionSource<IReadOnlyList<StartupDeviceResult>>();
            var reads = 0;
            Task<IReadOnlyList<StartupDeviceResult>> Read(CancellationToken _) { reads++; return pending.Task; }
            var loading = LoadStartupDevicesAsync(Read, CountedFallback);
            check(_startupLoading && !_startupComplete && !EditorSurface.IsEnabled && !SettingsButton.IsEnabled && !TransferButton.IsEnabled,
                "Startup: editor, settings and transfer are locked before the first read completes");
            check(ProjectSourceHint.Text == L.T("Konfigurationen werden von den Geräten gelesen …") && SafetyBanner.Visibility == Visibility.Visible,
                "Startup: the loading state stays visible while input is locked");
            StartSharedBrightnessIfReady();
            check(!_sharedBrightness.Running, "Startup: shared brightness cannot start before configuration reads finish");
            SwitchDevice("numpad");
            check(Device.Id == "pro-iso", "Startup: device switching cannot replace a session during loading");
            await LoadStartupDevicesAsync(Read, CountedFallback);
            check(reads == 1, "Startup: a second startup invocation cannot issue another read");
            pending.SetResult(both); await loading;
            check(_startupComplete && !_startupLoading && EditorSurface.IsEnabled && fallbackCount == DeviceCatalog.All.Count - both.Count,
                "Startup: successful reads unlock the editor; models without a successful read open local profiles");
            check(Device.Id == "numpad" && _project.Name == pad.Name && _projectPath is null && !IsDirty,
                "Startup: the connected preferred device becomes a clean, unbound project");
            check(_sessions.Count == DeviceCatalog.All.Count && _sessions.Values.All(s => (both.All(r => r.DeviceId != s.Project.DeviceId) || s.Path is null) && s.Undo.Length == 0 && s.Redo.Length == 0 && s.SavedFingerprint == Fingerprint(s.Project)),
                "Startup: every model has an independent clean session; successful reads have no file association");
            check(_confirmedDeviceSaves.Count == 2 && SafetyBanner.Visibility == Visibility.Collapsed && DeviceSaveState.Text.StartsWith("✓"),
                "Startup: only verified persistent device reads confirm the saved state");
            var closePrompts = 0;
            check(ConfirmCloseProjects(_ => { closePrompts++; return UnsavedChangesChoice.Cancel; }) && closePrompts == 0,
                "Startup: closing untouched device configurations requires no save or discard prompt");
            var padRead = Fingerprint(_project);
            pad.Name = "Provider changed after read";
            check(Fingerprint(_project) == padRead, "Startup: adopting a device result clones the provider project");
            var key = Device.Keys.First(k => k.LedIndex is not null).Id;
            _selection.Add(key); ApplyColor("#79C563"); var editedPad = Fingerprint(_project);
            SwitchDevice("pro-iso");
            check(_project.Name == pro.Name && _project.Layers[2].Name == "Onboard layer" && !IsDirty && _projectPath is null,
                "Startup: switching opens the already-read PRO configuration instead of a local default");
            SwitchDevice("numpad");
            check(Fingerprint(_project) == editedPad && IsDirty && _undo.Count == 1 && _selection.Contains(key),
                "Startup: edits, selection and undo survive switching between loaded sessions");
            await LoadStartupDevicesAsync(Read, CountedFallback);
            check(reads == 1 && Fingerprint(_project) == editedPad,
                "Startup: later invocations never overwrite in-session edits");

            foreach (var state in new[] { StartupDeviceState.NotConnected, StartupDeviceState.Ambiguous, StartupDeviceState.ReadFailed })
            {
                ApplyStartupResults([Loaded(pro), new("numpad", state)], "numpad", Fallback);
                check(Device.Id == "pro-iso", "Startup: " + state + " on the preferred device selects the successfully read device");
                SwitchDevice("numpad");
                var notice = new StartupDeviceResult("numpad", state).Message;
                check(_projectPath == "numpad.inglorious.json" && !IsDirty && !_confirmedDeviceSaves.ContainsKey("numpad") && DeviceSaveState.Text == L.T("Noch nicht übertragen"),
                    "Startup: " + state + " uses a clean local fallback without claiming device confirmation");
                check(SafetyBanner.Visibility == Visibility.Visible && ProjectSourceHint.Text == notice,
                    "Startup: " + state + " has a persistent, explicit offline explanation");
                ProjectName.Text = "Edited fallback"; CommitNames();
                SetProjectSource("Gespeichertes Profil · nicht mit dem Gerät abgeglichen"); UpdateDirtyState();
                SwitchDevice("pro-iso"); SwitchDevice("numpad");
                check(_project.Name == "Edited fallback" && IsDirty && ProjectSourceHint.Text == notice && SafetyBanner.Visibility == Visibility.Visible,
                    "Startup: " + state + " notice and edited fallback survive source updates and device switching");
            }

            ApplyStartupResults([new("pro-iso", StartupDeviceState.NotConnected), new("numpad", StartupDeviceState.ReadFailed)], "numpad", Fallback);
            check(Device.Id == "numpad" && _confirmedDeviceSaves.Count == 0,
                "Startup: when neither device can be read, the preferred local session remains selected");
            ApplyStartupResults([Loaded(pro, false), new("numpad", StartupDeviceState.NotConnected)], "pro-iso", Fallback);
            check(_project.Name == pro.Name && _projectPath is null && !IsDirty && !_confirmedDeviceSaves.ContainsKey("pro-iso") && ProjectSourceHint.Text == Loaded(pro, false).Message,
                "Startup: a readable RAM configuration is clean but reports the unavailable device memory without a saved confirmation");
            RecordDeviceRead(_project, Loaded(pro, false).Info);
            check(!_confirmedDeviceSaves.ContainsKey("pro-iso") && StartupNotice is not null,
                "Startup: a later nonpersistent manual read cannot clear the warning or falsely confirm device storage");
            RecordDeviceRead(_project);
            check(StartupNotice is null && SafetyBanner.Visibility == Visibility.Collapsed && DeviceSaveState.Text.StartsWith("✓"),
                "Startup: a later confirmed device read clears the earlier startup warning");
            ApplyStartupResults([new("pro-iso", StartupDeviceState.Loaded, pad, Loaded(pro).Info)], "pro-iso", Fallback);
            check(_project.DeviceId == "pro-iso" && _projectPath == "pro-iso.inglorious.json" && _confirmedDeviceSaves.Count == 0,
                "Startup: incomplete or mismatched device results fall back without adopting another model's project");

            _startupStarted = false;
            await LoadStartupDevicesAsync(_ => Task.FromException<IReadOnlyList<StartupDeviceResult>>(new IOException("offline simulated read failure")), Fallback);
            check(EditorSurface.IsEnabled && _startupComplete && _startupResults.Values.All(r => r.State == (DeviceCatalog.Get(r.DeviceId).IsOfflineOnly ? StartupDeviceState.OfflineOnly : StartupDeviceState.ReadFailed)) && _confirmedDeviceSaves.Count == 0,
                "Startup: an unexpected loader failure unlocks clean offline sessions with visible failure notices");
            _startupStarted = false; _startupFinishing = true;
            await LoadStartupDevicesAsync(_ => Task.FromResult(both), Fallback);
            check(_startupComplete && !EditorSurface.IsEnabled,
                "Startup: scan and onboarding finalization keep the editor locked after device results are adopted");
            var finalizingDevice = Device.Id;
            SwitchDevice(finalizingDevice == "pro-iso" ? "numpad" : "pro-iso");
            check(Device.Id == finalizingDevice,
                "Startup: finalization also blocks programmatic device navigation");
            _startupFinishing = false; EditorSurface.IsEnabled = true;
            _startupStarted = false;
            var closingRead = new TaskCompletionSource<IReadOnlyList<StartupDeviceResult>>();
            var closing = LoadStartupDevicesAsync(_ => closingRead.Task, Fallback);
            var beforeClose = Fingerprint(_project);
            _startupClosed = true;
            closingRead.SetResult(both); await closing;
            check(Fingerprint(_project) == beforeClose && !_startupComplete && !_sharedBrightness.Running,
                "Startup: a late read result after closing cannot adopt a project or restart background work");
        }
        finally
        {
            _preferences = preferences; _startupStarted = started; _startupComplete = complete;
            _startupLoading = false; _startupClosed = false; _startupFinishing = false; EditorSurface.IsEnabled = true;
            _startupResults.Clear(); foreach (var pair in resultsBefore) _startupResults.Add(pair.Key, pair.Value);
            _confirmedDeviceSaves.Clear(); foreach (var pair in confirmedBefore) _confirmedDeviceSaves.Add(pair.Key, pair.Value);
            ResetWorkflowFixture("pro-iso");
        }
    }
}
