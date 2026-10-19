using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private async Task RunHardwareProfileChecksAsync(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Offline checks only.");
        var read = _readHardwareSwitches; var language = L.CurrentLanguage;
        var devices = _devices; var revision = _connectionRevision;
        var startupResults = _startupResults.ToArray();
        const string id = "gmmk3-pro-100-iso";
        var installation = Guid.Parse("2d79e58b-dead-471a-82cb-7011252ad63c");
        StartupDeviceResult Loaded(byte slot, ulong generation = 4294967297, Guid? identity = null)
        {
            var project = ProjectService.Create(id); project.Name = "Slot " + slot;
            var image = OnboardSerializer.Prepare(project);
            var status = new OnboardHardwareProfileStatus(id, slot, slot, 1, slot, generation, image.Crc32, image.UsedBytes, image.SchemaVersion);
            var snapshot = new OnboardHardwareProfileSnapshot(identity ?? installation, status, image.Payload);
            return new(id, StartupDeviceState.Loaded, project, new OnboardInfo(id, 249, image.UsedBytes,
                (uint)Math.Min(generation, uint.MaxValue), image.Crc32, 0, image.SchemaVersion), snapshot);
        }
        try
        {
            ResetWorkflowFixture(id); SetOfflineConnectionFixture([id]); _connectionScanFailed = false; _scanning = false;
            var first = Loaded(1); var second = Loaded(2);
            var unavailableSnapshot = first.ProfileSnapshot! with { Status = first.ProfileSnapshot.Status with { Flags = 0 } };
            var unavailable = first with { ProfileSnapshot = unavailableSnapshot };
            ApplyStartupResults([unavailable], id, model => new(ProjectService.Create(model), null, "fixture", null));
            check(!unavailable.StorageHealthy && !_confirmedDeviceSaves.ContainsKey(id) && StartupNotice == unavailable.Message,
                "Hardware profiles: bank health overrides the legacy persistent flag at startup");
            RecordDeviceRead(_project, first.Info, unavailableSnapshot);
            check(!_confirmedDeviceSaves.ContainsKey(id) && StartupNotice is not null,
                "Hardware profiles: reading an unavailable bank never reports a confirmed device save");
            ApplyStartupResults([first], id, model => new(ProjectService.Create(model), null, "fixture", null));
            check(_hardwareProfileBindings[id].Generation == 4294967297 && _hardwareProfileBindings[id].Slot == 1,
                "Hardware profiles: startup binds the real slot and full 64-bit generation");
            _hardwareSwitchReading = new("fixture", null, second.ProfileSnapshot!.Status, installation);
            _hardwareSwitchDevice = id; L.SetLanguage("en"); RefreshHardwareSwitchView(); UpdateLayout();
            check(HardwareOsValue.Text == "Windows" && HardwareProfileValue.Text == "Profile 2" &&
                HardwareSwitchHost.Visibility == Visibility.Visible && HardwareProfilePosition.Visibility == Visibility.Visible,
                "Hardware switches: compact physical positions have no of-three suffix");
            check(HardwareOsPosition.ToolTip.ToString() == L.T("Stellung des Windows/Mac-Schalters an deiner Tastatur.") &&
                HardwareProfilePosition.ToolTip.ToString() == L.T("Stellung des Profilschalters an deiner Tastatur."),
                "Hardware switches: each icon explains which physical keyboard switch it represents");
            check(HardwareSwitchHost.TranslatePoint(new Point(), this).X >= WorkspaceHeadingHost.TranslatePoint(new Point(), this).X &&
                HardwareSwitchHost.TranslatePoint(new Point(HardwareSwitchHost.ActualWidth, 0), this).X <= CurrentLayerHost.TranslatePoint(new Point(), this).X,
                "Hardware switches: compact controls sit between model and layer selection");
            _hardwareSwitchReading = new("fixture", null, second.ProfileSnapshot.Status with { PhysicalSlot = 3, Flags = 85 }, installation);
            RefreshHardwareSwitchView();
            check(HardwareOsValue.Text == "Mac" && HardwareProfileValue.Text == "Profile 3" && HardwareSwitchPending.Visibility == Visibility.Visible,
                "Hardware switches: pending position is distinct from the currently effective setting");
            _hardwareSwitchReading = new("fixture", null, second.ProfileSnapshot.Status with { PhysicalSlot = 0 }, installation);
            RefreshHardwareSwitchView();
            check(HardwareProfileValue.Text == "Profile —", "Hardware switches: an unknown contact position is never reported as profile one");
            foreach (var code in L.SupportedLanguages.Select(item => item.Code))
            {
                L.SetLanguage(code); RefreshHardwareSwitchView();
                check(code == "de" || HardwareProfileValue.Text != "Profil —" || code is "fr" or "pl" or "tr",
                    "Hardware switches: localized profile position " + code);
            }
            L.SetLanguage(language);
            _hardwareSwitchReading = new("fixture", null, first.ProfileSnapshot!.Status, installation);
            _project.Name = "Unsaved slot one"; RefreshAll(); CaptureSession();
            var draftOne = Fingerprint(_project);
            check(ApplyHardwareProfileRefresh(second, id, false), "Hardware profiles: changing the selector adopts the selected slot");
            RestoreEditorSession(_sessions[id]); RefreshAll();
            check(_hardwareProfileBindings[id].Slot == 2 && _project.Name == "Slot 2" &&
                _hardwareProfileDrafts[(id, installation, 1)].Session.Project.Name == "Unsaved slot one",
                "Hardware profiles: an outgoing unsaved draft retains its explicit old slot");
            var asked = Array.Empty<string>();
            check(!ConfirmCloseProjects(names => { asked = names; return UnsavedChangesChoice.Cancel; }) && asked.Length == 1 && asked[0].Contains("Unsaved slot one"),
                "Hardware profiles: exiting a clean active slot still asks about inactive unsaved drafts");
            _project.Name = "Unsaved slot two"; RefreshAll(); CaptureSession();
            check(ApplyHardwareProfileRefresh(first, id, false), "Hardware profiles: returning to a slot can restore its draft");
            RestoreEditorSession(_sessions[id]); RefreshAll();
            check(Fingerprint(_project) == draftOne && _hardwareProfileBindings[id] == first.ProfileSnapshot.Binding,
                "Hardware profiles: returning restores all draft data without rebinding it to the other slot");
            var newer = Loaded(1, 4294967298);
            ApplyHardwareProfileRefresh(newer, id, false); RestoreEditorSession(_sessions[id]); RefreshAll();
            check(DeviceConfigurationNeedsRead && Fingerprint(_project) == draftOne && _hardwareProfileBindings[id] == first.ProfileSnapshot.Binding,
                "Hardware profiles: external save preserves edits and blocks stale-generation writes");
            var before = _hardwareProfileBindings[id];
            check(!ApplyHardwareProfileRefresh(second, id, true) && _hardwareProfileBindings[id] == before,
                "Hardware profiles: uncommitted editor input prevents silent profile replacement");

            _hardwareSwitchNextRead = default; _configurationRefreshPending.Clear(); _hardwareRefreshObserved.Clear();
            _readHardwareSwitches = (_, _) => Task.FromResult(new HardwareSwitchReading("fixture", null, first.ProfileSnapshot.Status, installation));
            await RefreshHardwareSwitchesAsync(true);
            var displayed = (HardwareOsValue.Text, HardwareProfileValue.Text, HardwareSwitchHost.Visibility);
            _hardwareSwitchNextRead = default;
            _readHardwareSwitches = (_, _) => throw new System.IO.IOException("Injected busy poll");
            await RefreshHardwareSwitchesAsync(true);
            check(displayed == (HardwareOsValue.Text, HardwareProfileValue.Text, HardwareSwitchHost.Visibility),
                "Hardware switches: a transient failure does not blink or replace the last confirmed display");
            _hardwareSwitchNextRead = default;
            var pending = new TaskCompletionSource<HardwareSwitchReading>(TaskCreationOptions.RunContinuationsAsynchronously);
            _readHardwareSwitches = (_, _) => pending.Task;
            var poll = RefreshHardwareSwitchesAsync(true);
            var pause = PauseKnobStatusAsync();
            check(!pause.IsCompleted, "Hardware switches: foreground work waits for the shared status lease");
            pending.SetResult(new("fixture", null, first.ProfileSnapshot.Status, installation));
            await poll; using (await pause)
                check(!CanReadHardwareSwitches && !CanReadKnobStatus, "Hardware switches: one foreground pause covers switch and knob polling");
            RunHardwareProfileRecoveryChecks(check, first);
            await RunHardwareProfileRereadChecksAsync(check, first);
            RunDeviceReadFailureTextChecks(check);
            SetOfflineConnectionFixture([]); RefreshHardwareSwitchView();
            check(HardwareSwitchHost.Visibility == Visibility.Collapsed && _hardwareSwitchReading is null,
                "Hardware switches: disconnect clears obsolete positions");
        }
        finally
        {
            _readHardwareSwitches = read; L.SetLanguage(language); _devices = devices; _connectionRevision = revision;
            _hardwareSwitchReading = null; _hardwareSwitchDevice = null; _hardwareSwitchNextRead = default;
            _hardwareProfileBindings.Clear(); _hardwareProfileDrafts.Clear(); _hardwareRefreshObserved.Clear();
            _startupResults.Clear(); foreach (var pair in startupResults) _startupResults[pair.Key] = pair.Value;
            _configurationRefreshPending.Clear(); ResetWorkflowFixture("pro-iso");
        }
    }

    private void RunHardwareProfileRecoveryChecks(Action<bool, string> check, StartupDeviceResult incoming)
    {
        var id = incoming.DeviceId;
        void PrepareFallback()
        {
            ApplyStartupResults([new(id, StartupDeviceState.ReadFailed)], id, model =>
            {
                var project = ProjectService.Create(model);
                project.Macros.Add(new MacroDefinition { Id = "fallback-macro", Name = "Original macro" });
                return new(project, null, "offline fallback fixture", null);
            });
            _configurationRefreshPending.Clear();
            CaptureSession();
        }
        bool Recover() => ApplyRefreshedDeviceConfigurations([incoming], id, HasPendingHardwareEditorInput);

        PrepareFallback();
        var fallback = _project;
        ProjectName.Text = "Uncommitted profile name";
        check(HasPendingHardwareEditorInput && !IsDirty && !Recover() && ReferenceEquals(_sessions[id].Project, fallback) &&
            ProjectName.Text == "Uncommitted profile name" && _project.Name != ProjectName.Text &&
            !_hardwareProfileBindings.ContainsKey(id) && DeviceConfigurationNeedsRead,
            "Hardware recovery: first device read preserves an uncommitted profile name outside the project fingerprint");

        PrepareFallback();
        fallback = _project;
        _macroId = "fallback-macro"; RefreshMacros(); CaptureSession();
        MacroName.Text = "Uncommitted macro name";
        check(HasPendingHardwareEditorInput && !IsDirty && !Recover() && ReferenceEquals(_sessions[id].Project, fallback) &&
            MacroName.Text == "Uncommitted macro name" && CurrentMacro?.Name == "Original macro" &&
            !_hardwareProfileBindings.ContainsKey(id) && DeviceConfigurationNeedsRead,
            "Hardware recovery: first device read preserves an uncommitted macro name outside the project fingerprint");

        PrepareFallback();
        var explicitProject = ProjectService.Clone(_project);
        var explicitPath = System.IO.Path.Combine(Paths.WorkspaceRoot, "workspace", "checks", "hardware-profile-recovery", "local-profile.inglorious.json");
        ApplyOpening(new(explicitProject, explicitPath, "explicit local profile fixture", null));
        RefreshAll(); CaptureSession();
        check(!IsDirty && !HasPendingHardwareEditorInput && !Recover() && ReferenceEquals(_sessions[id].Project, explicitProject) &&
            _sessions[id].Path == explicitPath && !_hardwareProfileBindings.ContainsKey(id) && DeviceConfigurationNeedsRead,
            "Hardware recovery: a deliberately opened clean local profile is never replaced by the first recovered read");

        PrepareFallback();
        _project.Name = "Deliberately saved fallback edits";
        _savedFingerprint = Fingerprint(_project); RefreshAll(); CaptureSession(); fallback = _project;
        check(!IsDirty && !Recover() && ReferenceEquals(_sessions[id].Project, fallback) &&
            !_hardwareProfileBindings.ContainsKey(id) && DeviceConfigurationNeedsRead,
            "Hardware recovery: saving fallback edits does not make them safe for automatic replacement");

        PrepareFallback();
        _project.Name = "Unsaved fallback edits"; RefreshAll(); CaptureSession(); fallback = _project;
        check(IsDirty && !Recover() && ReferenceEquals(_sessions[id].Project, fallback) &&
            !_hardwareProfileBindings.ContainsKey(id) && DeviceConfigurationNeedsRead,
            "Hardware recovery: an unbound dirty fallback remains intact after a recovered device read");

        PrepareFallback();
        fallback = _project;
        check(!HasPendingHardwareEditorInput && Recover() && !ReferenceEquals(_sessions[id].Project, fallback) &&
            _hardwareProfileBindings[id] == incoming.ProfileSnapshot!.Binding && !DeviceConfigurationNeedsRead &&
            Fingerprint(_sessions[id].Project) == Fingerprint(incoming.Project!),
            "Hardware recovery: an untouched automatic fallback is replaced by the verified device configuration");

        PrepareFallback();
        _sessions.Remove(id);
        check(ApplyHardwareProfileRefresh(incoming, id, false) && _hardwareProfileBindings[id] == incoming.ProfileSnapshot!.Binding &&
            !DeviceConfigurationNeedsRead && Fingerprint(_sessions[id].Project) == Fingerprint(incoming.Project!),
            "Hardware recovery: a verified first read is adopted when no local editor session exists");
    }

    private async Task RunHardwareProfileRereadChecksAsync(Action<bool, string> check, StartupDeviceResult incoming)
    {
        var id = incoming.DeviceId;
        var snapshot = incoming.ProfileSnapshot ?? throw new InvalidOperationException("Hardware reread fixture requires a profile snapshot.");
        ApplyStartupResults([incoming], id, model => new(ProjectService.Create(model), null, "offline fallback fixture", null));
        SetOfflineConnectionFixture([id]);
        _project.Layers[0].Keys.First().Value.Color = "#B01337";
        CaptureSession();
        var draft = _project; var fingerprint = Fingerprint(draft); var binding = _hardwareProfileBindings[id];
        _configurationRefreshPending.Clear();

        // The tray/reconnect paths enqueue a read. An exhausted native retry budget reaches this consumer as ReadFailed.
        _configurationRefreshPending.Add(id);
        var reads = 0;
        await RefreshDeviceConfigurationsAsync((requested, _) =>
        {
            reads++;
            check(requested.SequenceEqual(new[] { id }), "Hardware reread: an explicit refresh reads only its requested model");
            return Task.FromResult<IReadOnlyList<StartupDeviceResult>>([new(id, StartupDeviceState.ReadFailed)]);
        });
        check(reads == 1 && _configurationRefreshPending.Count == 0 && DeviceConfigurationNeedsRead &&
            ReferenceEquals(_project, draft) && Fingerprint(_project) == fingerprint && _hardwareProfileBindings[id] == binding,
            "Hardware reread: exhausted metadata reads retain the bound draft and do not schedule another UI retry");

        _hardwareSwitchNextRead = default;
        _readHardwareSwitches = (_, _) => Task.FromResult(new HardwareSwitchReading("fixture", null,
            snapshot.Status, snapshot.InstallationId));
        await RefreshHardwareSwitchesAsync(true);
        await RefreshDeviceConfigurationsAsync((_, _) =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<StartupDeviceResult>>([incoming]);
        });
        check(reads == 1 && _configurationRefreshPending.Count == 0 && DeviceConfigurationNeedsRead &&
            ReferenceEquals(_project, draft) && _hardwareProfileBindings[id] == binding,
            "Hardware reread: healthy unchanged switch polling cannot create an endless configuration retry loop");

        // A later explicit request remains valid even though the previously retained binding is unchanged.
        _configurationRefreshPending.Add(id);
        await RefreshDeviceConfigurationsAsync((_, _) =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<StartupDeviceResult>>([incoming]);
        });
        check(reads == 2 && !DeviceConfigurationNeedsRead && _configurationRefreshPending.Count == 0 &&
            ReferenceEquals(_project, draft) && Fingerprint(_project) == fingerprint && IsDirty && _hardwareProfileBindings[id] == binding,
            "Hardware reread: a later requested read clears the unread gate without replacing an unchanged-slot draft");
    }

    private static void RunDeviceReadFailureTextChecks(Action<bool, string> check)
    {
        var language = L.CurrentLanguage;
        try
        {
            L.SetLanguage("de"); var german = DeviceMetadataReadFailureText();
            foreach (var supported in L.SupportedLanguages)
            {
                L.SetLanguage(supported.Code); var text = DeviceMetadataReadFailureText();
                check(!string.IsNullOrWhiteSpace(text) && (supported.Code == "de" || text != german) &&
                    !text.Contains("metadata", StringComparison.OrdinalIgnoreCase) && !text.Contains("995", StringComparison.Ordinal),
                    "Device read errors: localized user notice contains no native query details for " + supported.Code);
            }
            L.SetLanguage("en");
            check(DeviceMetadataReadFailureText() == "The keyboard could not be read just now. Please try again. Your profile is unchanged.",
                "Device read errors: the notice explains retry and preservation without raw exception text");
        }
        finally { L.SetLanguage(language); }
    }
}
