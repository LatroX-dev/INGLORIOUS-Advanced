using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace InGlorious.Core;

public enum ApplicationStartupState { Ready, InstallerRunning, GuardUnavailable }

/// <summary>Announces the app before checking setup. The installer checks again immediately
/// before replacing files, while its Setup mutex is present. No mutex is used for ownership.</summary>
public sealed class WindowsInstallationGuard : IDisposable
{
    public const string RunningMutexName = "INGLORIOUS.Advanced.Running";
    public const string SetupMutexName = "INGLORIOUS.Advanced.Setup";
    private Mutex? _running;
    private readonly string _setupName;
    public ApplicationStartupState State { get; private set; }
    public bool CanStart => State == ApplicationStartupState.Ready;

    public WindowsInstallationGuard() : this(RunningMutexName, SetupMutexName, OperatingSystem.IsWindows()) { }

    internal WindowsInstallationGuard(string runningName, string setupName, bool enabled)
    {
        _setupName = setupName;
        if (!enabled) { State = ApplicationStartupState.Ready; return; }
        try
        {
            // Order matters: setup's final check must see us before any app/device work starts.
            _running = new Mutex(false, runningName);
            State = ReadSetupState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or WaitHandleCannotBeOpenedException or NotSupportedException)
        {
            // An unverified process gate must never allow a normal app startup.
            State = ApplicationStartupState.GuardUnavailable;
        }
    }

    /// <summary>Allows the installer's requested launch to wait for its process to finish.
    /// The running marker remains present; this never permits startup during installation.</summary>
    public Task WaitForSetupExitAsync() => WaitForSetupExitAsync(TimeSpan.FromSeconds(15));

    internal async Task WaitForSetupExitAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var timer = Stopwatch.StartNew();
        while (State == ApplicationStartupState.InstallerRunning)
        {
            if (_running is null) { State = ApplicationStartupState.GuardUnavailable; return; }
            try { State = ReadSetupState(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or WaitHandleCannotBeOpenedException or NotSupportedException)
            { State = ApplicationStartupState.GuardUnavailable; return; }
            if (State != ApplicationStartupState.InstallerRunning) return;
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
        }
    }

    private ApplicationStartupState ReadSetupState()
    {
        // TryOpenExisting also returns false for a same-name object of another type.
        // Only a genuinely absent marker permits startup; access/type failures block it.
        using var setup = OpenMutex(0x00100000 /* SYNCHRONIZE */, false, _setupName);
        if (setup.IsInvalid)
            return Marshal.GetLastWin32Error() == 2 /* ERROR_FILE_NOT_FOUND */
                ? ApplicationStartupState.Ready : ApplicationStartupState.GuardUnavailable;
        // The marker is unowned. Close our handle before waiting, otherwise we would
        // keep it alive after Setup exits and could never observe its disappearance.
        return ApplicationStartupState.InstallerRunning;
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenMutex(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    public void Dispose() { _running?.Dispose(); _running = null; }
}
