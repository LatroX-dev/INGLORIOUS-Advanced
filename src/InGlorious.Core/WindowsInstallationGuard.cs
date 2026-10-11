using System.IO;

namespace InGlorious.Core;

public enum ApplicationStartupState { Ready, InstallerRunning, GuardUnavailable }

/// <summary>Announces the app before checking setup. The installer checks again immediately
/// before replacing files, while its Setup mutex is present. No mutex is used for ownership.</summary>
public sealed class WindowsInstallationGuard : IDisposable
{
    public const string RunningMutexName = "INGLORIOUS.Advanced.Running";
    public const string SetupMutexName = "INGLORIOUS.Advanced.Setup";
    private Mutex? _running;
    public ApplicationStartupState State { get; }
    public bool CanStart => State == ApplicationStartupState.Ready;

    public WindowsInstallationGuard() : this(RunningMutexName, SetupMutexName, OperatingSystem.IsWindows()) { }

    internal WindowsInstallationGuard(string runningName, string setupName, bool enabled)
    {
        if (!enabled) { State = ApplicationStartupState.Ready; return; }
        try
        {
            // Order matters: setup's final check must see us before any app/device work starts.
            _running = new Mutex(false, runningName);
            if (Mutex.TryOpenExisting(setupName, out var setup))
            {
                using (setup) State = ApplicationStartupState.InstallerRunning;
            }
            else State = ApplicationStartupState.Ready;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or WaitHandleCannotBeOpenedException or NotSupportedException)
        {
            // An unverified process gate must never allow a normal app startup.
            State = ApplicationStartupState.GuardUnavailable;
        }
    }

    public void Dispose() { _running?.Dispose(); _running = null; }
}
