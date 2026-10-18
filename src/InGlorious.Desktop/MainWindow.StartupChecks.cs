using InGlorious.Core;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private async Task RunStartupSessionChecksAsync(Action<bool, string> check)
    {
        if (!Program.Offline) throw new InvalidOperationException("Startup checks require offline mode.");
        var savedSessions = _sessions.ToArray();
        var savedConfirmed = _confirmed.ToArray();
        var savedNotices = _startupNotices.ToArray();
        var savedFingerprints = _startupReadFingerprints.ToArray();
        var savedFallbacks = _startupFallbackNotices.ToArray();
        var savedPlaceholders = _startupPlaceholders.ToArray();
        var savedCrc = _deviceReadCrc.ToArray(); var savedRequired = _deviceReadRequired.ToArray(); var savedRefresh = _configurationRefreshPending.ToArray();
        var savedAutomatic = _automaticFallbackSessions.ToArray();
        var savedState = _state;
        var savedPreferences = _preferences;
        var savedStatus = _status;
        var savedBusy = _busy;
        var savedLoading = _startupLoading;
        var savedStarted = _startupStarted;
        var savedClosed = _startupWindowClosed;
        var savedAllowClose = _allowClose;
        var savedPending = _pendingApply;
        try
        {
            _sessions.Clear(); _confirmed.Clear(); _startupNotices.Clear(); _startupReadFingerprints.Clear(); _startupFallbackNotices.Clear(); _startupPlaceholders.Clear();
            _preferences = _preferences with { StartDevice = "pro-iso" };
            var proSaved = ProjectService.Create("pro-iso"); proSaved.Name = "Saved PRO profile";
            var padSaved = ProjectService.Create("numpad"); padSaved.Name = "Saved Numpad profile";
            var proLocal = new EditorState(proSaved, "saved-pro.inglorious.json");
            var padLocal = new EditorState(padSaved, "saved-pad.inglorious.json");
            _sessions["pro-iso"] = proLocal; _sessions["numpad"] = padLocal; _state = proLocal;
            var proFingerprint = EditorState.Fingerprint(proLocal.Project);
            var padFingerprint = EditorState.Fingerprint(padLocal.Project);

            StartupDeviceResult Loaded(string id, bool persistent = true)
            {
                var project = ProjectService.Create(id); project.Name = "Device " + id;
                var key = DeviceCatalog.Get(id).Keys.First(k => k.LedIndex.HasValue);
                project.Layers[0].Keys[key.Id].Color = "#123456";
                var image = OnboardSerializer.Prepare(project);
                return new(id, StartupDeviceState.Loaded, project,
                    new OnboardInfo(id, persistent ? (byte)1 : (byte)0, image.UsedBytes, 7, image.Crc32, 0, image.SchemaVersion));
            }

            var loadedPro = Loaded("pro-iso"); var loadedPad = Loaded("numpad");
            ApplyStartupResults([loadedPro, loadedPad]);
            check(_state.Device.Id == "pro-iso", "startup selects preferred device when both device reads succeed");
            check(_sessions.Count == 2 && _sessions.Values.All(s => s.Path is null && !s.Dirty && !s.CanUndo && !s.CanRedo), "startup device sessions are clean, detached and have no file path or history");
            check(_confirmed.Count == 2 && _sessions.All(s => _confirmed[s.Key] == EditorState.Fingerprint(s.Value.Project)), "startup confirms only matching persistent device projects");
            check(proLocal.Path == "saved-pro.inglorious.json" && padLocal.Path == "saved-pad.inglorious.json" &&
                EditorState.Fingerprint(proLocal.Project) == proFingerprint && EditorState.Fingerprint(padLocal.Project) == padFingerprint,
                "startup adoption does not alter saved-profile sessions or their paths");
            loadedPro.Project!.Name = "Changed source object";
            check(_sessions["pro-iso"].Project.Name == "Device pro-iso", "startup clones incoming project data instead of keeping mutable aliases");
            var proRead = _sessions["pro-iso"]; proRead.Change(p => p.Name = "Edited device project");
            check(proRead.Dirty && proRead.Path is null, "editing a loaded device project becomes dirty without binding it to an old profile");
            ShowStartupNotice();
            check(!_startupNotices.ContainsKey("pro-iso"), "edited device projects no longer show a stale loaded-at-startup notice");
            proRead.Undo(); check(!proRead.Dirty, "undo returns a loaded device session to its clean device state");

            _sessions["pro-iso"] = proLocal; _sessions["numpad"] = padLocal;
            ApplyStartupResults([new("pro-iso", StartupDeviceState.Ambiguous), Loaded("numpad")]);
            check(_state.Device.Id == "numpad" && ReferenceEquals(_sessions["pro-iso"], proLocal), "an ambiguous preferred model preserves its local profile and selects the successfully read device");
            check(!_confirmed.ContainsKey("pro-iso") && _startupNotices["pro-iso"].Length > 0, "ambiguous device has a visible fallback notice and no stale confirmation");
            _state = proLocal; ShowStartupNotice();
            check(_status == _startupNotices["pro-iso"], "switching to a fallback device displays its own startup notice");
            Build(); Status("Unrelated later status");
            check(_root.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "StartupFallbackNotice").Text == _startupNotices["pro-iso"],
                "fallback warning remains visible when a later status replaces the footer");

            foreach (var failure in new[] { StartupDeviceState.NotConnected, StartupDeviceState.ReadFailed })
            {
                _sessions["pro-iso"] = proLocal; _sessions["numpad"] = padLocal;
                ApplyStartupResults([new("pro-iso", failure), new("numpad", failure)]);
                check(ReferenceEquals(_state, proLocal) && ReferenceEquals(_sessions["numpad"], padLocal) && !_state.Dirty,
                    "no successful startup read preserves preferred clean local sessions: " + failure);
                check(_confirmed.Count == 0 && _startupNotices.Values.All(s => s.Length > 0), "startup failure provides fallback notices without device confirmations: " + failure);
            }

            ApplyStartupResults([Loaded("pro-iso", false), new("numpad", StartupDeviceState.NotConnected)]);
            check(_state.Path is null && !_state.Dirty && !_confirmed.ContainsKey("pro-iso"), "readable nonpersistent device is loaded clean but never confirmed as saved");
            check(_startupFallbackNotices.ContainsKey("pro-iso"), "readable nonpersistent storage keeps a persistent warning");
            var placeholder = new EditorState("pro-iso"); placeholder.MarkInitialClean();
            _sessions["pro-iso"] = placeholder; _startupPlaceholders.Add("pro-iso");
            ApplyStartupResults([new("pro-iso", StartupDeviceState.NotConnected), new("numpad", StartupDeviceState.NotConnected)]);
            check(!ReferenceEquals(_state, placeholder) && !_startupPlaceholders.Contains("pro-iso"), "failed startup replaces its temporary placeholder with the local fallback only after reading");
            ApplyStartupResults([new("pro-iso", StartupDeviceState.Loaded), Loaded("numpad")]);
            check(_state.Device.Id == "numpad" && !_confirmed.ContainsKey("pro-iso") &&
                _startupFallbackNotices["pro-iso"] == new StartupDeviceResult("pro-iso", StartupDeviceState.ReadFailed).Message,
                "incomplete loaded results become explicit failures without false confirmation");

            var current = Loaded("pro-iso");
            ApplyStartupResults([current, Loaded("numpad")]);
            _state = _sessions["pro-iso"];
            var refreshedProject = ProjectService.Clone(current.Project!);
            refreshedProject.Layers[0].Keys.First().Value.Color = "#AB2309";
            var refreshed = current with { Project = refreshedProject, Info = current.Info! with { ActiveCrc = current.Info.ActiveCrc + 1 } };
            ApplyRefreshedDeviceConfigurations([refreshed], "pro-iso", false);
            check(_state.Project.Layers[0].Keys.First().Value.Color == "#AB2309" && !_state.Dirty && _state.Path is null && !DeviceConfigurationNeedsRead,
                "reopening a clean session adopts the latest actual device configuration");
            _state.Change(project => project.Name = "Retain my draft");
            var draft = _state; var draftFingerprint = EditorState.Fingerprint(draft.Project);
            ApplyRefreshedDeviceConfigurations([refreshed], "pro-iso", false);
            check(ReferenceEquals(_state, draft) && _state.Dirty && _state.CanUndo && !DeviceConfigurationNeedsRead,
                "an unchanged device keeps the edited draft and its undo history");
            ApplyRefreshedDeviceConfigurations([refreshed with { Info = refreshed.Info! with { ActiveCrc = refreshed.Info.ActiveCrc + 1 } }], "pro-iso", false);
            check(ReferenceEquals(_state, draft) && EditorState.Fingerprint(_state.Project) == draftFingerprint && DeviceConfigurationNeedsRead && !_confirmed.ContainsKey("pro-iso"),
                "a changed device keeps the draft but prevents a stale transfer until an explicit read");
            MarkDeviceConfigurationRead(refreshed.Project!, refreshed.Info);
            check(!DeviceConfigurationNeedsRead, "an explicit verified read clears the stale-state transfer gate");
            ApplyRefreshedDeviceConfigurations([new("pro-iso", StartupDeviceState.ReadFailed)], "pro-iso", false);
            check(ReferenceEquals(_state, draft) && DeviceConfigurationNeedsRead,
                "failed refresh retains edits and requires reading before transfer");
            ApplyStartupResults([current, Loaded("numpad")]);
            var pendingSession = _state;
            ApplyRefreshedDeviceConfigurations([refreshed], "pro-iso", true);
            check(ReferenceEquals(_state, pendingSession) && DeviceConfigurationNeedsRead,
                "uncommitted fields preserve the exact editor even when the project itself is still clean");

            _pendingApply = null; _busy = _startupLoading = false;
            var refreshSource = new TaskCompletionSource<IReadOnlyList<StartupDeviceResult>>();
            _configurationRefreshPending.Add("pro-iso");
            var refreshing = RefreshDeviceConfigurationsAsync((ids, _) =>
            {
                check(ids.SequenceEqual(new[] { "pro-iso" }), "reopening only reads requested models");
                return refreshSource.Task;
            });
            check(_startupLoading && _busy && !_root.IsEnabled, "reopening disables editing before awaiting device data");
            refreshSource.SetResult([]); await refreshing;
            check(!_startupLoading && !_busy && _root.IsEnabled && DeviceConfigurationNeedsRead,
                "an incomplete refresh response unlocks local editing but cannot enable transfer");
            _configurationRefreshPending.Add("pro-iso");
            await RefreshDeviceConfigurationsAsync((_, _) => Task.FromResult<IReadOnlyList<StartupDeviceResult>>([current]));
            check(!DeviceConfigurationNeedsRead && !_state.Dirty && _state.Path is null,
                "a subsequent successful current read recovers the unchanged clean session");

            _sessions["pro-iso"] = proLocal; _state = proLocal;
            ApplyStartupResults([new("pro-iso", StartupDeviceState.ReadFailed)]);
            ApplyRefreshedDeviceConfigurations([current], "pro-iso", false);
            check(!ReferenceEquals(_state, proLocal) && _state.Path is null && !DeviceConfigurationNeedsRead,
                "an untouched automatically opened local fallback recovers to the real device configuration");
            _sessions["pro-iso"] = proLocal; _state = proLocal;
            ApplyStartupResults([new("pro-iso", StartupDeviceState.ReadFailed)]);
            proLocal.Change(project => project.Name = "Saved deliberate changes"); proLocal.MarkSaved("saved-pro.inglorious.json");
            ApplyRefreshedDeviceConfigurations([current], "pro-iso", false);
            check(ReferenceEquals(_state, proLocal) && !proLocal.Dirty && DeviceConfigurationNeedsRead,
                "saving edited fallback data preserves that intentional profile during subsequent refresh");

            _busy = _startupLoading = true; Build();
            check(!_root.IsEnabled && !GuardNavigation().GetAwaiter().GetResult(), "startup read locks editing and navigation before device results arrive");
            _busy = _startupLoading = false; _startupStarted = false; Build();
            var beforeOffline = EditorState.Fingerprint(_state.Project);
            await LoadStartupDevicesAsync();
            check(!_startupStarted && !_busy && _root.IsEnabled && EditorState.Fingerprint(_state.Project) == beforeOffline,
                "offline startup bypass neither discovers hardware nor changes sessions or editability");
            var beforeClose = _state;
            _startupWindowClosed = true;
            ApplyStartupResults([Loaded("pro-iso"), Loaded("numpad")]);
            check(ReferenceEquals(_state, beforeClose), "closed windows ignore late startup results");
            _startupWindowClosed = false; _allowClose = true;
            ApplyStartupResults([Loaded("pro-iso"), Loaded("numpad")]);
            check(ReferenceEquals(_state, beforeClose), "closing windows ignore startup results before the Closed event");
        }
        finally
        {
            _sessions.Clear(); foreach (var item in savedSessions) _sessions.Add(item.Key, item.Value);
            _confirmed.Clear(); foreach (var item in savedConfirmed) _confirmed.Add(item.Key, item.Value);
            _startupNotices.Clear(); foreach (var item in savedNotices) _startupNotices.Add(item.Key, item.Value);
            _startupReadFingerprints.Clear(); foreach (var item in savedFingerprints) _startupReadFingerprints.Add(item.Key, item.Value);
            _startupFallbackNotices.Clear(); foreach (var item in savedFallbacks) _startupFallbackNotices.Add(item.Key, item.Value);
            _startupPlaceholders.Clear(); _startupPlaceholders.UnionWith(savedPlaceholders);
            _deviceReadCrc.Clear(); foreach (var item in savedCrc) _deviceReadCrc.Add(item.Key, item.Value);
            _deviceReadRequired.Clear(); foreach (var item in savedRequired) _deviceReadRequired.Add(item.Key, item.Value);
            _configurationRefreshPending.Clear(); _configurationRefreshPending.UnionWith(savedRefresh);
            _automaticFallbackSessions.Clear(); foreach (var item in savedAutomatic) _automaticFallbackSessions.Add(item.Key, item.Value);
            _state = savedState; _preferences = savedPreferences; _status = savedStatus;
            _busy = savedBusy; _startupLoading = savedLoading; _startupStarted = savedStarted;
            _startupWindowClosed = savedClosed; _allowClose = savedAllowClose;
            _pendingApply = savedPending;
            Build();
        }
    }
}
