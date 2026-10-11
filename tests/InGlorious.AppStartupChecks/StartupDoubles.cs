using System.IO;
using System.Windows;

// Only this test assembly replaces the external boundaries. App.xaml/App.xaml.cs and
// BackgroundApplicationSession are compiled unchanged from the production sources.
namespace InGlorious.Core;

internal static class StartupFixture
{
    internal static string Mode = "";
    internal static int GuardsCreated, GuardsDisposed, InstancesCreated, InstancesDisposed, WindowsCreated,
        BackgroundInitializations, Opens, TrayAttempts, TraysDisposed, PreferenceReads, Notices;
    internal static bool? Activation;
    internal static Action? Open, Exit;
}

public enum ApplicationStartupState { Ready, InstallerRunning, GuardUnavailable }
public sealed class WindowsInstallationGuard : IDisposable
{
    public WindowsInstallationGuard() => StartupFixture.GuardsCreated++;
    public ApplicationStartupState State => StartupFixture.Mode switch
    {
        "installer-running" => ApplicationStartupState.InstallerRunning,
        "guard-unavailable" => ApplicationStartupState.GuardUnavailable,
        _ => ApplicationStartupState.Ready
    };
    public bool CanStart => State == ApplicationStartupState.Ready;
    public void Dispose() => StartupFixture.GuardsDisposed++;
}

public sealed class ApplicationInstance : IDisposable
{
    public ApplicationInstance(Action activate)
    {
        StartupFixture.InstancesCreated++;
        if (StartupFixture.Mode == "instance-error") throw new IOException("Synthetic unavailable instance boundary.");
    }
    public bool IsPrimary => !StartupFixture.Mode.StartsWith("secondary", StringComparison.Ordinal);
    public Task<bool> NotifyExistingAsync(bool activate)
    {
        StartupFixture.Activation = activate;
        return Task.FromResult(StartupFixture.Mode != "secondary-unreachable");
    }
    public void Dispose() => StartupFixture.InstancesDisposed++;
}

public sealed class WindowsTrayIcon : IDisposable
{
    public static IDisposable? TryCreate(Action open, Action close, Action unavailable)
    {
        StartupFixture.TrayAttempts++; StartupFixture.Open = open; StartupFixture.Exit = close;
        return StartupFixture.Mode == "no-tray" ? null : new WindowsTrayIcon();
    }
    public void Dispose() => StartupFixture.TraysDisposed++;
}

public sealed class MainWindow : Window
{
    private bool _exiting;
    private BackgroundApplicationSession? _session;
    public MainWindow()
    {
        StartupFixture.WindowsCreated++;
        Width = 120; Height = 60; ShowInTaskbar = false; WindowStyle = WindowStyle.None;
        Closing += (_, e) =>
        {
            if (_session?.HandleWindowClose(_exiting) == true) e.Cancel = true;
        };
    }
    internal void AttachTraySession(BackgroundApplicationSession session) => _session = session;
    internal void InitializeInBackground() => StartupFixture.BackgroundInitializations++;
    internal void OpenFromTray() { StartupFixture.Opens++; Show(); }
    internal void HideToTray() => Hide();
    internal void RequestApplicationExit() { _exiting = true; Close(); }
    internal bool RequestSessionEnd() { RequestApplicationExit(); return true; }
}

public sealed record TestPreferences(string Language = "en");
public sealed record TestPreferencesResult(TestPreferences Preferences);
public static class AppPreferencesStore
{
    public static TestPreferencesResult Load() { StartupFixture.PreferenceReads++; return new(new()); }
}
public static class L
{
    public static void SetLanguage(string _) { }
    public static string T(string value) => value;
}
