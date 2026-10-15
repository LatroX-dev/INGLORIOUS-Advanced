using InGlorious.Core;

internal static partial class Program
{
    private static async Task CheckFirmwareSequence()
    {
        static Task<SetupProgress> Write(SetupFixture fixture, SetupSession session) => session.Operation switch
        {
            SetupOperation.Install => fixture.Service.InstallAsync(session, explicitlyConfirmed: true),
            SetupOperation.Update => fixture.Service.UpdateAsync(session, explicitlyConfirmed: true),
            SetupOperation.Restore => fixture.Service.RestoreAsync(session, explicitlyConfirmed: true),
            _ => throw new InvalidOperationException("Unexpected fixture operation.")
        };
        async Task<SetupSession> Prepare(SetupFixture fixture, SetupOperation operation)
        {
            if (operation != SetupOperation.Install)
            {
                var install = await fixture.ReadyToCapture();
                await fixture.Service.CaptureAsync(install); await Write(fixture, install);
            }
            return await fixture.ReadyToCapture(operation);
        }

        foreach (var board in RoundTripBoards)
        {
            using var fixture = new SetupFixture(board.DeviceId);
            var original = fixture.Environment.Current.ToArray();
            byte[]? recovery = null; string? recoveryPath = null;
            foreach (var operation in new[] { SetupOperation.Install, SetupOperation.Update, SetupOperation.Restore })
            {
                var session = await fixture.ReadyToCapture(operation);
                var sequence = new DeviceSetupFirmwareSequence();
                var writesBefore = fixture.Environment.Writes; var verified = false;
                var result = await sequence.RunAsync(session, operation, board.DeviceId, true, () => true,
                    () => fixture.Service.CaptureAsync(session), progress =>
                    {
                        verified = true;
                        Check(progress.CanInstall && fixture.Environment.Writes == writesBefore &&
                            File.Exists(session.BackupPath) && File.Exists(Path.Combine(session.BackupDirectory!, "capture.json")),
                            "Combined firmware action has both verified backups before writing: " + operation + " " + board.DeviceId);
                        if (operation == SetupOperation.Install)
                        {
                            recoveryPath = Path.Combine(session.Directory, "recovery.json");
                            recovery = File.ReadAllBytes(recoveryPath);
                        }
                        else Check(File.ReadAllBytes(recoveryPath!).SequenceEqual(recovery!), "Update and restore retain the original recovery archive");
                    }, () => Write(fixture, session));
                Check(sequence.Started && verified && result.Stage == SetupStage.Reconnect && fixture.Environment.Writes == writesBefore + 1,
                    "One confirmation captures then changes firmware exactly once: " + operation + " " + board.DeviceId);
                await RejectAsync(() => sequence.RunAsync(session, operation, board.DeviceId, true, () => true,
                    () => fixture.Service.CaptureAsync(session), _ => { }, () => Write(fixture, session)), "A completed action cannot be repeated");
                Check(fixture.Environment.Writes == writesBefore + 1, "Completed operation has no automatic retry");
            }
            Check(fixture.Environment.Current.SequenceEqual(original), "Combined restore writes the exact manufacturer original: " + board.DeviceId);
        }

        foreach (var operation in Enum.GetValues<SetupOperation>())
        {
            foreach (var fault in new[] { "mismatch", "missing", "wrong-hash", "short", "conflicting-model", "wrong-identity", "throw" })
            {
                // A changed manufacturer descriptor only identifies stock firmware; restore accepts changed runtime code.
                if (fault == "conflicting-model" && operation == SetupOperation.Restore) continue;
                using var fixture = new SetupFixture("numpad"); var session = await Prepare(fixture, operation);
                fixture.Environment.CaptureFault = fault;
                var sequence = new DeviceSetupFirmwareSequence(); var writesBefore = fixture.Environment.Writes; var calls = 0;
                await RejectAsync(() => sequence.RunAsync(session, operation, "numpad", true, () => true,
                    () => fixture.Service.CaptureAsync(session), _ => { }, () => { calls++; return Write(fixture, session); }),
                    "Combined action rejects an unverified backup: " + operation + " " + fault);
                Check(sequence.Started && calls == 0 && fixture.Environment.Writes == writesBefore,
                    "Failed backup consumes confirmation without calling a write: " + operation + " " + fault);
            }
            foreach (var fault in new[] { "port-changed", "disconnected", "core-opened" })
            {
                using var fixture = new SetupFixture("numpad"); var session = await Prepare(fixture, operation);
                var sequence = new DeviceSetupFirmwareSequence(); var writesBefore = fixture.Environment.Writes;
                await RejectAsync(() => sequence.RunAsync(session, operation, "numpad", true, () => true,
                    () => fixture.Service.CaptureAsync(session), _ =>
                    {
                        if (fault == "port-changed") fixture.Environment.Nodes = [fixture.Environment.Boot with { LocationPaths = [SetupFixture.OtherControllerPort] }];
                        if (fault == "disconnected") fixture.Environment.Nodes = [];
                        if (fault == "core-opened") fixture.Environment.CoreRunning = true;
                    }, () => Write(fixture, session)), "Fresh backend preflight stops changed conditions after backup: " + operation + " " + fault);
                Check(sequence.Started && fixture.Environment.Writes == writesBefore,
                    "Combined action preserves immediate prewrite guards: " + operation + " " + fault);
            }
            // The stable confirmation button deliberately does not poll; CaptureAsync must still reject stale USB state.
            foreach (var fault in new[] { "port-changed", "disconnected", "core-opened" })
            {
                using var fixture = new SetupFixture("numpad"); var session = await Prepare(fixture, operation);
                var sequence = new DeviceSetupFirmwareSequence(); var writesBefore = fixture.Environment.Writes;
                if (fault == "port-changed") fixture.Environment.Nodes = [fixture.Environment.Boot with { LocationPaths = [SetupFixture.OtherControllerPort] }];
                if (fault == "disconnected") fixture.Environment.Nodes = [];
                if (fault == "core-opened") fixture.Environment.CoreRunning = true;
                await RejectAsync(() => sequence.RunAsync(session, operation, "numpad", true, () => true,
                    () => fixture.Service.CaptureAsync(session), _ => { }, () => Write(fixture, session)),
                    "A stale confirmation cannot start a write: " + operation + " " + fault);
                Check(sequence.Started && fixture.Environment.Writes == writesBefore, "No write after changed conditions before capture");
            }
        }

        SetupSession NewSession(SetupOperation operation) => new(
            new("fixture", "GMMK Numpad", ["numpad"], 1, 2, [], false), operation, "fixture")
            { DeviceId = "numpad", Stage = SetupStage.ReadyToBackup };
        foreach (var operation in Enum.GetValues<SetupOperation>())
        {
            foreach (var fault in new[] { "context", "session-stage", "model", "result-model", "result-stage", "write-attempt", "consumed", "uncertain", "callback-context" })
            {
                var session = NewSession(operation); var sequence = new DeviceSetupFirmwareSequence(); var current = true; var writes = 0;
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
                Task<SetupProgress> AttemptWrite() { writes++; return Task.FromResult(new SetupProgress(SetupStage.Reconnect, "fixture", "numpad")); }
                await RejectAsync(() => sequence.RunAsync(session, operation, "numpad", true, () => current, Capture,
                    _ => { if (fault == "callback-context") current = false; }, AttemptWrite), "Changed binding prevents writing: " + operation + " " + fault);
                await RejectAsync(() => sequence.RunAsync(NewSession(operation), operation, "numpad", true, () => true, Capture, _ => { }, AttemptWrite),
                    "A failed combined attempt cannot reuse its confirmation");
                Check(writes == 0, "No write delegate after changed capture context");
            }
            foreach (var fault in new[] { "no-consent", "wrong-operation", "unknown-operation", "wrong-model", "context", "write-attempt", "consumed", "uncertain" })
            {
                var session = NewSession(operation); var sequence = new DeviceSetupFirmwareSequence(); var captures = 0;
                if (fault == "write-attempt") session.WriteAttempted = true;
                if (fault == "consumed") session.Consumed = true;
                if (fault == "uncertain") session.OutcomeUncertain = true;
                var requested = fault == "unknown-operation" ? (SetupOperation)99 : fault == "wrong-operation" ? Enum.GetValues<SetupOperation>().First(item => item != operation) : operation;
                await RejectAsync(() => sequence.RunAsync(session, requested, fault == "wrong-model" ? "pro-iso" : "numpad", fault != "no-consent", () => fault != "context",
                    () => { captures++; return Task.FromResult(new SetupProgress(SetupStage.ReadyToInstall, "fixture", "numpad")); }, _ => { },
                    () => throw new InvalidOperationException("Write must never be called")), "No combined operation without valid bound authorization: " + operation + " " + fault);
                Check(!sequence.Started && captures == 0, "Invalid authorization starts neither capture nor writing");
            }
            {
                var session = NewSession(operation); var sequence = new DeviceSetupFirmwareSequence(); var writes = 0;
                var pendingCapture = new TaskCompletionSource<SetupProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<SetupProgress> AttemptWrite() { writes++; return Task.FromResult(new SetupProgress(SetupStage.Reconnect, "fixture", "numpad")); }
                var active = sequence.RunAsync(session, operation, "numpad", true, () => true, () => pendingCapture.Task, _ => { }, AttemptWrite);
                Check(sequence.Started && !active.IsCompleted, "Single-use confirmation is consumed before the first await: " + operation);
                await RejectAsync(() => sequence.RunAsync(session, operation, "numpad", true, () => true, () => pendingCapture.Task, _ => { }, AttemptWrite),
                    "A second click cannot queue another capture or write");
                session.Stage = SetupStage.ReadyToInstall;
                pendingCapture.SetResult(new(SetupStage.ReadyToInstall, "fixture", "numpad"));
                await active;
                Check(writes == 1, "Concurrent clicks result in only one firmware write: " + operation);
            }
        }
    }
}
