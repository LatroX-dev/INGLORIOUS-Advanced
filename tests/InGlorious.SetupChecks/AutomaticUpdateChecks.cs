using InGlorious.Core;

internal static partial class Program
{
    private static async Task CheckAutomaticUpdates()
    {
        CheckAutomaticUpdateMessages();
        var version = new OnboardFirmwareVersion("numpad", new(0, 11, 12), 0x000B0C01);
        var snapshot = OnboardSerializer.Prepare(ProjectService.Create("numpad")).Payload;
        async Task<SetupSession> BeginRuntime(SetupFixture fixture, SetupOperation operation = SetupOperation.Update)
        {
            fixture.Environment.Nodes = [fixture.Environment.Runtime];
            return await fixture.Service.BeginAsync((await fixture.Service.ListDevicesAsync()).Single(), operation);
        }

        Check(BoardDescriptorRegistry.ProtocolBoards.Count == 16 && !DeviceSetupService.SupportsAutomaticUpdateDevice("unknown-board"),
            "Automatic updates are explicitly gated to the 16 protocol identities, never arbitrary models.");
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            using var f = new SetupFixture(board.DeviceId, automaticUpdateTarget: true);
            var modelVersion = version with { DeviceId = board.DeviceId };
            var modelSnapshot = AutomaticSnapshot(board.DeviceId);
            var session = await BeginRuntime(f); var entries = 0;
            var progress = await f.Service.StartAutomaticUpdateAsync(session, modelVersion, true,
                () => Task.FromResult(modelSnapshot), () => { entries++; f.Environment.Nodes = [f.Environment.Boot]; return Task.CompletedTask; });
            Check(progress.Stage == SetupStage.ConnectBootloader && session.AutomaticUpdate && entries == 1 && f.Environment.Writes == 0,
                "One confirmed command starts update mode without flashing or requiring a missing-device polling sample.");
            Check(File.ReadAllBytes(Path.Combine(session.Directory, "before-update.igcfg.bin")).SequenceEqual(modelSnapshot.Payload),
                "Automatic entry durably saves the runtime configuration before changing USB mode.");
            Check(session.ExpectedHardwareProfile == modelSnapshot.HardwareProfile &&
                File.Exists(Path.Combine(session.Directory, "before-update-profile.json")) == (board.DeviceId == "gmmk3-pro-100-iso"),
                "Hardware profile identity, slot and generation are bound and archived only for profile-capable models.");
            Check((await f.Service.ObserveAsync(session)).CanCapture, "Fast acknowledged runtime-to-bootloader transition retains the verified port and probe checks.");
            await RejectAsync(() => f.Service.StartAutomaticUpdateAsync(session, modelVersion, true,
                () => Task.FromResult(modelSnapshot), () => { entries++; return Task.CompletedTask; }), "One automatic entry cannot be sent twice.");
            Check(entries == 1 && f.Environment.Writes == 0, "Duplicate confirmation sends no command or flash write.");
        }

        foreach (var fault in new[] { "consent", "model", "unknown-model", "old-version", "current-version", "newer-version", "zero-build", "restore", "core", "port", "unavailable", "other-bootloader" })
        {
            using var f = new SetupFixture("numpad", automaticUpdateTarget: true);
            var session = await BeginRuntime(f, fault == "restore" ? SetupOperation.Restore : SetupOperation.Update);
            var expected = fault switch
            {
                "model" => version with { DeviceId = "pro-iso" },
                "unknown-model" => version with { DeviceId = "unknown-board" },
                "old-version" => version with { Version = new(0, 11, 11) },
                "current-version" => version with { Version = new(0, 11, 13) },
                "newer-version" => version with { Version = new(0, 11, 14) },
                "zero-build" => version with { BuildId = 0 },
                _ => version
            };
            if (fault == "core") f.Environment.CoreRunning = true;
            if (fault == "port") f.Environment.Nodes = [f.Environment.Runtime with { LocationPaths = [SetupFixture.OtherControllerPort] }];
            if (fault == "unavailable") f.Environment.Nodes = [f.Environment.Runtime with { Service = "Unavailable" }];
            if (fault == "other-bootloader") f.Environment.Nodes = [f.Environment.Runtime, f.Environment.Boot with { LocationPaths = [SetupFixture.OtherControllerPort] }];
            int entries = 0, reads = 0;
            await RejectAsync(() => f.Service.StartAutomaticUpdateAsync(session, expected, fault != "consent",
                () => { reads++; return Task.FromResult(new SetupRuntimeConfiguration(snapshot)); }, () => { entries++; return Task.CompletedTask; }),
                "Automatic entry refuses invalid authorization or device state: " + fault);
            Check(entries == 0 && reads == 0 && f.Environment.Writes == 0, "Rejected automatic entry has no runtime command or firmware write: " + fault);
        }

        foreach (var fault in new[] { "invalid-configuration", "port-after-read", "core-after-read", "lost-ack" })
        {
            using var f = new SetupFixture("numpad", automaticUpdateTarget: true); var session = await BeginRuntime(f); int entries = 0;
            await RejectAsync(() => f.Service.StartAutomaticUpdateAsync(session, version, true,
                () =>
                {
                    if (fault == "port-after-read") f.Environment.Nodes = [f.Environment.Runtime with { LocationPaths = [SetupFixture.OtherControllerPort] }];
                    if (fault == "core-after-read") f.Environment.CoreRunning = true;
                    return Task.FromResult(new SetupRuntimeConfiguration(fault == "invalid-configuration" ? Array.Empty<byte>() : snapshot));
                },
                () => { entries++; return Task.FromException(new OnboardBootloaderEntryException("RAW25/26 synthetic diagnostic", new IOException("simulated missing acknowledgement"))); }),
                "Entry preparation and missing acknowledgement cannot proceed: " + fault);
            Check(session.Stage == SetupStage.Blocked && session.BootloaderEntryAttempted && f.Environment.Writes == 0,
                "An entry attempt is consumed even if the response is uncertain.");
            await RejectAsync(() => f.Service.StartAutomaticUpdateAsync(session, version, true,
                () => Task.FromResult(new SetupRuntimeConfiguration(snapshot)), () => { entries++; return Task.CompletedTask; }), "Uncertain entry never retries.");
            Check(entries == (fault == "lost-ack" ? 1 : 0), "No entry is sent after failed preparation and none is repeated.");
        }

        foreach (var fault in new[] { "runtime-remains", "runtime-and-bootloader", "other-port", "ambiguous", "timeout" })
        {
            using var f = new SetupFixture("numpad", automaticUpdateTarget: true); var session = await BeginRuntime(f);
            await f.Service.StartAutomaticUpdateAsync(session, version, true, () => Task.FromResult(new SetupRuntimeConfiguration(snapshot)), () => Task.CompletedTask);
            f.Environment.Nodes = fault switch
            {
                "runtime-remains" => [f.Environment.Runtime],
                "runtime-and-bootloader" => [f.Environment.Runtime, f.Environment.Boot],
                "other-port" => [f.Environment.Boot with { LocationPaths = [SetupFixture.OtherControllerPort] }],
                "ambiguous" => [f.Environment.Boot, f.Environment.Boot with { InstanceId = f.Environment.Boot.InstanceId + "2", LocationPaths = [SetupFixture.OtherControllerPort] }],
                _ => []
            };
            if (fault == "timeout") session.BootloaderEntryDeadline = DateTimeOffset.UtcNow.AddSeconds(-1);
            Check(!(await f.Service.ObserveAsync(session)).CanCapture && f.Environment.Requests.Count == 0 && f.Environment.Writes == 0,
                "The automatic transition cannot bypass bootloader identity or timeout checks: " + fault);
            if (fault == "timeout") Check(session.Stage == SetupStage.Blocked, "A missing automatic bootloader ends in an explicit stopped state.");
            if (fault == "runtime-and-bootloader") Check(session.Stage == SetupStage.Blocked, "Simultaneous runtime and bootloader nodes cannot prove a software transition.");
        }

        foreach (var board in RoundTripBoards)
        foreach (var restartFault in new[] { "acknowledged", "restart-unacknowledged" })
        {
            using var f = new SetupFixture(board.DeviceId, automaticUpdateTarget: true);
            var modelSnapshot = AutomaticSnapshot(board.DeviceId);
            var install = await f.ReadyToCapture(); await f.Service.CaptureAsync(install); await f.Service.InstallAsync(install, true);
            // Entry command safety is tested above. This fixture exercises update/restart against the current reviewed catalog,
            // including catalogs containing an older manual-only firmware during the first migration release.
            var session = await f.ReadyToCapture(SetupOperation.Update);
            FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(board.DeviceId), out var current);
            session.AutomaticUpdate = true; session.ExpectedRuntimeVersion = version with { DeviceId = board.DeviceId, Version = current };
            session.ExpectedRuntimeConfiguration = modelSnapshot.Payload.ToArray(); session.ExpectedHardwareProfile = modelSnapshot.HardwareProfile;
            await f.Service.CaptureAsync(session);
            f.Environment.WriteFault = restartFault;
            var writes = f.Environment.Writes;
            Check((await f.Service.UpdateAsync(session, true)).Stage == SetupStage.Reconnect &&
                session.AutomaticRestartRequested && !session.OutcomeUncertain && f.Environment.Writes == writes + 1,
                "A verified automatic update waits for runtime return even if restart acknowledgement is missing.");
            Check(session.AutomaticRestartAcknowledged == (restartFault == "acknowledged"), "Restart acknowledgement is separate from verified firmware bytes.");
            Check(f.Environment.Requests.Last().GetProperty("restartAfterVerify").GetBoolean(), "Only the confirmed automatic update requests a post-verification restart.");
            session.RuntimeReturnDeadline = DateTimeOffset.UtcNow.AddSeconds(-1);
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Reconnect && !session.AutomaticRestartRequested &&
                f.Environment.Writes == writes + 1, "A restart timeout offers a manual reconnect without repeating the flash.");
            f.Environment.Nodes = [f.Environment.Runtime];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Complete, "Returned runtime must still occupy the bound physical port.");
            f.Service.VerifyReturnedConfiguration(session, modelSnapshot.Payload, modelSnapshot.HardwareProfile);
            var changed = modelSnapshot.Payload.ToArray(); changed[^1] ^= 1;
            Reject(() => f.Service.VerifyReturnedConfiguration(session, changed, modelSnapshot.HardwareProfile), "Returned settings must match the pre-entry runtime snapshot.");
            Check(session.Stage == SetupStage.Blocked, "A returned configuration mismatch stops completion instead of silently retrying or flashing.");
        }

        foreach (var fault in new[] { "missing-binding", "wrong-slot", "pending-switch", "pending-os", "upload-open", "unhealthy", "wrong-generation", "wrong-crc", "empty-installation", "unexpected-binding" })
        {
            var id = fault == "unexpected-binding" ? "numpad" : "gmmk3-pro-100-iso";
            using var f = new SetupFixture(id, automaticUpdateTarget: true); var session = await BeginRuntime(f);
            var state = AutomaticSnapshot("gmmk3-pro-100-iso");
            state = fault switch
            {
                "missing-binding" => state with { HardwareProfile = null },
                "wrong-slot" => state with { HardwareProfile = state.HardwareProfile! with { Slot = 3 } },
                "pending-switch" => state with { ProfileStatus = state.ProfileStatus! with { Flags = 5 } },
                "pending-os" => state with { ProfileStatus = state.ProfileStatus! with { Flags = 65 } },
                "upload-open" => state with { ProfileStatus = state.ProfileStatus! with { Flags = 9 } },
                "unhealthy" => state with { ProfileStatus = state.ProfileStatus! with { Flags = 0 } },
                "wrong-generation" => state with { HardwareProfile = state.HardwareProfile! with { Generation = 7 } },
                "wrong-crc" => state with { HardwareProfile = state.HardwareProfile! with { Crc32 = 17 } },
                "empty-installation" => state with { HardwareProfile = state.HardwareProfile! with { InstallationId = Guid.Empty } },
                "unexpected-binding" => state with { Payload = snapshot },
                _ => state
            };
            int entries = 0;
            await RejectAsync(() => f.Service.StartAutomaticUpdateAsync(session, version with { DeviceId = id }, true,
                () => Task.FromResult(state), () => { entries++; return Task.CompletedTask; }), "Invalid profile binding blocks entry: " + fault);
            Check(entries == 0 && f.Environment.Writes == 0, "Invalid profile binding never changes device mode or firmware: " + fault);
        }

        foreach (var generation in new ulong[] { 0, 0x100000007 })
        {
            const string id = "gmmk3-pro-100-iso";
            using var f = new SetupFixture(id, automaticUpdateTarget: true); var session = await BeginRuntime(f);
            var state = AutomaticSnapshot(id, generation);
            await f.Service.StartAutomaticUpdateAsync(session, version with { DeviceId = id }, true,
                () => Task.FromResult(state), () => Task.CompletedTask);
            session.Stage = SetupStage.Complete;
            f.Service.VerifyReturnedConfiguration(session, state.Payload, state.HardwareProfile);
            Reject(() => f.Service.VerifyReturnedConfiguration(session, state.Payload, state.HardwareProfile! with { Generation = generation + 1 }),
                "Returned profile verification preserves full-width generation, including a healthy legacy fallback.");
            Check(session.Stage == SetupStage.Blocked, "A profile identity mismatch blocks completion even with identical payload bytes.");
        }
    }

    private static SetupRuntimeConfiguration AutomaticSnapshot(string deviceId, ulong generation = 0x100000007)
    {
        var payload = OnboardSerializer.Prepare(ProjectService.Create(deviceId)).Payload;
        if (deviceId != "gmmk3-pro-100-iso") return new(payload);
        var status = new OnboardHardwareProfileStatus(deviceId, 2, 2, (byte)(generation == 0 ? 3 : 1), 2, generation,
            OnboardProtocol.Crc32(payload), payload.Length, payload[4] | (payload[5] << 8));
        return new(payload, status.Bind(Guid.NewGuid()), status);
    }

    private static void CheckAutomaticUpdateMessages()
    {
        const string diagnostic = "#< CLIXML RAW26 USB\\VID_FAKE private-path timeout";
        var previousLanguage = L.CurrentLanguage;
        try
        {
            foreach (var language in L.SupportedLanguages)
            {
                L.SetLanguage(language.Code);
                var entryError = new OnboardBootloaderEntryException(diagnostic, new OnboardCommandException(3, diagnostic));
                Check(DeviceSetupService.ForUser(entryError) == L.T("Der Wechsel in den Update-Modus wurde nicht bestätigt. Prüfe die USB-Verbindung."),
                    "An uncertain bootloader commit keeps its fixed localized recovery message even when its inner error reports busy.");
                Check(DeviceSetupService.ForUser(new OnboardCommandException(3, diagnostic)) == L.T("Die Tastatur ist beschäftigt. Warte kurz und versuche es erneut."),
                    "A known busy response has a localized next step without exposing raw device details.");
                foreach (var error in new Exception[] { new IOException(diagnostic), new OnboardCommandException(7, diagnostic), new InvalidOperationException(diagnostic, entryError) })
                    Check(DeviceSetupService.ForUser(error) == L.T("Der Vorgang konnte nicht abgeschlossen werden. Es wird nichts automatisch wiederholt."),
                        "Unknown errors and wrappers never expose protocol messages, file paths or nested exception text.");
                Check(!DeviceSetupService.ForUser(entryError).Contains("RAW26", StringComparison.Ordinal) &&
                    !DeviceSetupService.ForUser(new OnboardCommandException(3, diagnostic)).Contains("CLIXML", StringComparison.Ordinal),
                    "Known error types use reviewed text, never their arbitrary supplied message.");
            }
        }
        finally { L.SetLanguage(previousLanguage); }
    }
}
