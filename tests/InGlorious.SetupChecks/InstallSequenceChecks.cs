using InGlorious.Core;

internal static partial class Program
{
    private static async Task CheckInstallSequence()
    {
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            using var fixture = new SetupFixture(board.DeviceId);
            var session = await fixture.ReadyToCapture();
            var sequence = new DeviceSetupInstallSequence();
            var verified = false;
            var result = await sequence.RunAsync(session, board.DeviceId, true, () => true,
                () => fixture.Service.CaptureAsync(session), progress =>
                {
                    verified = true;
                    Check(progress.CanInstall && fixture.Environment.Writes == 0 && File.Exists(Path.Combine(session.Directory, "recovery.json")),
                        "Combined setup has a durable verified original archive before installation: " + board.DeviceId);
                }, () => fixture.Service.InstallAsync(session, explicitlyConfirmed: true));
            Check(sequence.Started && verified && result.Stage == SetupStage.Reconnect && fixture.Environment.Writes == 1,
                "One confirmation captures and installs once: " + board.DeviceId);
        }

        foreach (var fault in new[] { "mismatch", "missing", "wrong-hash", "short", "conflicting-model", "wrong-identity", "throw" })
        {
            using var fixture = new SetupFixture("numpad"); var session = await fixture.ReadyToCapture();
            fixture.Environment.CaptureFault = fault;
            var sequence = new DeviceSetupInstallSequence(); var writes = 0;
            await RejectAsync(() => sequence.RunAsync(session, "numpad", true, () => true,
                () => fixture.Service.CaptureAsync(session), _ => { }, () => { writes++; return fixture.Service.InstallAsync(session, true); }),
                "Combined setup rejects an unverified backup: " + fault);
            Check(sequence.Started && writes == 0 && fixture.Environment.Writes == 0,
                "Failed backup consumes the confirmation without calling installation: " + fault);
        }

        foreach (var fault in new[] { "port-changed", "disconnected", "core-opened" })
        {
            using var fixture = new SetupFixture("numpad"); var session = await fixture.ReadyToCapture();
            var sequence = new DeviceSetupInstallSequence();
            await RejectAsync(() => sequence.RunAsync(session, "numpad", true, () => true,
                () => fixture.Service.CaptureAsync(session), _ =>
                {
                    if (fault == "port-changed") fixture.Environment.Nodes = [fixture.Environment.Boot with { LocationPaths = [SetupFixture.OtherControllerPort] }];
                    if (fault == "disconnected") fixture.Environment.Nodes = [];
                    if (fault == "core-opened") fixture.Environment.CoreRunning = true;
                }, () => fixture.Service.InstallAsync(session, true)), "Fresh backend preflight stops changed conditions after backup: " + fault);
            Check(sequence.Started && fixture.Environment.Writes == 0, "Combined action preserves the backend's immediate prewrite guard: " + fault);
        }

        SetupSession NewSession(SetupOperation operation = SetupOperation.Install) => new(
            new("fixture", "GMMK Numpad", ["numpad"], 1, 2, [], false), operation, "fixture")
            { DeviceId = "numpad", Stage = SetupStage.ReadyToBackup };
        foreach (var fault in new[] { "context", "session-stage", "model", "result-model", "result-stage", "write-attempt", "consumed", "uncertain", "callback-context" })
        {
            var session = NewSession(); var sequence = new DeviceSetupInstallSequence(); var current = true; var writes = 0;
            Task<SetupProgress> Capture()
            {
                session.Stage = SetupStage.ReadyToInstall;
                if (fault == "context") current = false;
                if (fault == "session-stage") session.Stage = SetupStage.Disconnect;
                if (fault == "model") session.DeviceId = "pro-iso";
                if (fault == "write-attempt") session.WriteAttempted = true;
                if (fault == "consumed") session.Consumed = true;
                if (fault == "uncertain") session.OutcomeUncertain = true;
                return Task.FromResult(new SetupProgress(fault == "result-stage" ? SetupStage.Blocked : SetupStage.ReadyToInstall,
                    "fixture", fault == "result-model" ? "pro-iso" : "numpad"));
            }
            Task<SetupProgress> Write() { writes++; return Task.FromResult(new SetupProgress(SetupStage.Reconnect, "fixture", "numpad")); }
            await RejectAsync(() => sequence.RunAsync(session, "numpad", true, () => current, Capture,
                _ => { if (fault == "callback-context") current = false; }, Write), "Changed setup binding prevents installation: " + fault);
            await RejectAsync(() => sequence.RunAsync(NewSession(), "numpad", true, () => true, Capture, _ => { }, Write),
                "A failed combined attempt cannot reuse its confirmation: " + fault);
            Check(writes == 0, "No write delegate after changed capture context: " + fault);
        }

        foreach (var operation in Enum.GetValues<SetupOperation>())
        {
            var session = NewSession(operation); var sequence = new DeviceSetupInstallSequence(); var captures = 0;
            await RejectAsync(() => sequence.RunAsync(session, "numpad", false, () => true,
                () => { captures++; return Task.FromResult(new SetupProgress(SetupStage.ReadyToInstall, "fixture", "numpad")); }, _ => { },
                () => throw new InvalidOperationException("Write must never be called")), "No combined setup before explicit confirmation");
            Check(!sequence.Started && captures == 0, "Missing confirmation causes no capture or installation");
            if (operation != SetupOperation.Install)
            {
                await RejectAsync(() => sequence.RunAsync(session, "numpad", true, () => true,
                    () => { captures++; throw new InvalidOperationException("No capture"); }, _ => { }, () => throw new InvalidOperationException("No write")),
                    "Combined setup cannot consume update/restore authorization");
                Check(captures == 0, "Other operations keep their own workflow");
            }
        }
        {
            var session = NewSession(); var sequence = new DeviceSetupInstallSequence(); var writes = 0;
            var pendingCapture = new TaskCompletionSource<SetupProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<SetupProgress> Write() { writes++; return Task.FromResult(new SetupProgress(SetupStage.Reconnect, "fixture", "numpad")); }
            var active = sequence.RunAsync(session, "numpad", true, () => true, () => pendingCapture.Task, _ => { }, Write);
            Check(sequence.Started && !active.IsCompleted, "The single-use confirmation is consumed before the first await");
            await RejectAsync(() => sequence.RunAsync(session, "numpad", true, () => true, () => pendingCapture.Task, _ => { }, Write),
                "A second click cannot queue another capture or write");
            session.Stage = SetupStage.ReadyToInstall;
            pendingCapture.SetResult(new(SetupStage.ReadyToInstall, "fixture", "numpad"));
            await active;
            Check(writes == 1, "Concurrent clicks still result in one installation");
        }
    }
}
