namespace InGlorious.Core;

/// <summary>Consumes one device-bound setup confirmation for capture followed by installation.</summary>
internal sealed class DeviceSetupInstallSequence
{
    internal bool Started { get; private set; }

    internal async Task<SetupProgress> RunAsync(SetupSession session, string deviceId, bool explicitlyConfirmed,
        Func<bool> contextIsCurrent, Func<Task<SetupProgress>> capture, Action<SetupProgress> backupVerified,
        Func<Task<SetupProgress>> install)
    {
        if (Started || !explicitlyConfirmed || !contextIsCurrent() || session.Operation != SetupOperation.Install ||
            session.Stage != SetupStage.ReadyToBackup || session.DeviceId != deviceId ||
            !session.Device.CandidateDeviceIds.Contains(deviceId) || session.WriteAttempted || session.Consumed || session.OutcomeUncertain)
            throw Stopped();

        // A failure consumes this attempt too. A fresh wizard is required to authorize a new attempt.
        Started = true;
        var result = await capture();
        bool StillVerified() => contextIsCurrent() && session.Operation == SetupOperation.Install &&
            session.DeviceId == deviceId && result.DeviceId == deviceId && result.CanInstall &&
            session.Stage == SetupStage.ReadyToInstall && !session.WriteAttempted && !session.Consumed && !session.OutcomeUncertain;
        if (!StillVerified()) throw Stopped();
        backupVerified(result);
        if (!StillVerified()) throw Stopped();
        // InstallAsync retains the backend's archive, identity, current-port and full prewrite checks.
        return await install();
    }

    private static SetupException Stopped() => new(L.T("Starte die Einrichtung erneut."));
}
