using System.Diagnostics;
using System.Reflection;
using InGlorious.Core;

if (args.Length == 3 && args[0] is "--app" or "--installer")
{
    if (!args[1].StartsWith("INGLORIOUS.StartupGuardTest.", StringComparison.Ordinal) ||
        !args[2].StartsWith("INGLORIOUS.StartupGuardTest.", StringComparison.Ordinal)) return 2;
    if (args[0] == "--app")
    {
        using var guard = new WindowsInstallationGuard(args[1], args[2], enabled: true);
        Console.WriteLine(guard.State);
        Console.ReadLine();
    }
    else
    {
        // Models SetupMutex creation followed by PrepareToInstall's mandatory final check.
        using var setup = new Mutex(false, args[2]);
        bool appPresent = Mutex.TryOpenExisting(args[1], out var running);
        running?.Dispose();
        Console.WriteLine(appPresent ? "Blocked" : "Ready");
        Console.ReadLine();
    }
    return 0;
}

int checks = 0;
void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
bool Exists(string name) { bool exists = Mutex.TryOpenExisting(name, out var handle); handle?.Dispose(); return exists; }
(string Running, string Setup) Names()
{
    string prefix = "INGLORIOUS.StartupGuardTest." + Guid.NewGuid().ToString("N");
    return (prefix + ".Running", prefix + ".Setup");
}

var disabledNames = Names();
using (var disabled = new WindowsInstallationGuard(disabledNames.Running, disabledNames.Setup, enabled: false))
    Check(disabled.CanStart && disabled.State == ApplicationStartupState.Ready, "non-Windows startup needs no installer gate");

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine($"PASS {checks} portable startup check; Windows mutex process checks require Windows. No app or device access.");
    return 0;
}

var plain = Names();
using (var guard = new WindowsInstallationGuard(plain.Running, plain.Setup, enabled: true))
{
    Check(guard.CanStart, "ordinary startup is admitted");
    Check(Exists(plain.Running), "running handle stays present throughout app lifetime");
}
Check(!Exists(plain.Running), "shutdown releases running handle");

var conflict = Names();
using (var incompatible = new EventWaitHandle(false, EventResetMode.ManualReset, conflict.Running))
using (var guard = new WindowsInstallationGuard(conflict.Running, conflict.Setup, enabled: true))
    Check(!guard.CanStart && guard.State == ApplicationStartupState.GuardUnavailable, "unverifiable running gate blocks startup");

var installerFirst = Names();
using (var installer = new Child("--installer", installerFirst.Running, installerFirst.Setup))
{
    Check(await installer.ReadAsync() == "Ready", "installer may prepare when no app runs");
    using var app = new Child("--app", installerFirst.Running, installerFirst.Setup);
    Check(await app.ReadAsync() == nameof(ApplicationStartupState.InstallerRunning), "app cannot initialize while installer owns setup marker");
    Check(Exists(installerFirst.Running), "blocked notice retains process-lifetime running marker");
}
Check(!Exists(installerFirst.Running) && !Exists(installerFirst.Setup), "both isolated processes release their handles");

var appFirst = Names();
using (var app = new Child("--app", appFirst.Running, appFirst.Setup))
{
    Check(await app.ReadAsync() == nameof(ApplicationStartupState.Ready), "app may initialize before setup begins");
    using var installer = new Child("--installer", appFirst.Running, appFirst.Setup);
    Check(await installer.ReadAsync() == "Blocked", "PrepareToInstall blocks replacement of an already-running app");
}

var startupGap = Names();
Check(!Exists(startupGap.Running), "initial installer AppMutex check sees no app");
using (var app = new Child("--app", startupGap.Running, startupGap.Setup))
{
    Check(await app.ReadAsync() == nameof(ApplicationStartupState.Ready), "app starts in Inno initial-check/SetupMutex gap");
    using var installer = new Child("--installer", startupGap.Running, startupGap.Setup);
    Check(await installer.ReadAsync() == "Blocked", "mandatory later installer check closes initial-check gap");
}

for (int repeat = 0; repeat < 24; repeat++)
{
    var names = Names();
    using var app = new Child("--app", names.Running, names.Setup);
    using var installer = new Child("--installer", names.Running, names.Setup);
    var results = await Task.WhenAll(app.ReadAsync(), installer.ReadAsync());
    Check(!(results[0] == nameof(ApplicationStartupState.Ready) && results[1] == "Ready"), "concurrent start never admits both app and file replacement");
    Check(results[0] is nameof(ApplicationStartupState.Ready) or nameof(ApplicationStartupState.InstallerRunning) && results[1] is "Ready" or "Blocked",
        "race only yields a verified admitted or blocked state");
}

Console.WriteLine($"PASS {checks} startup/installer process checks. Random test mutex names only; no product process, user settings or USB access.");
return 0;

sealed class Child : IDisposable
{
    private readonly Process _process;
    internal Child(string mode, string running, string setup)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test host.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(mode); start.ArgumentList.Add(running); start.ArgumentList.Add(setup);
        _process = Process.Start(start) ?? throw new IOException("Test helper did not start.");
    }
    internal async Task<string> ReadAsync() => await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
        ?? throw new IOException("Test helper returned no result: " + await _process.StandardError.ReadToEndAsync());
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) { _process.StandardInput.WriteLine(); _process.StandardInput.Flush(); }
            if (!_process.WaitForExit(10000)) throw new TimeoutException("Owned test helper did not stop.");
            if (_process.ExitCode != 0) throw new IOException("Owned test helper failed: " + _process.StandardError.ReadToEnd());
        }
        finally { _process.Dispose(); }
    }
}
