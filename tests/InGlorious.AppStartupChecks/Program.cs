using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using InGlorious.Core;

namespace StartupChecks;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
        _checks++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "--case") return RunCase(args[1]);
            var cases = new[] { "normal", "background", "no-tray", "offline-smoke", "offline-screenshot", "secondary", "secondary-background", "secondary-unreachable", "installer-running", "guard-unavailable", "instance-error",
                "after-setup-ready", "after-setup-release", "after-setup-background", "after-setup-timeout", "after-setup-unavailable", "after-setup-shutdown", "offline-after-setup" };
            int assertions = 0;
            foreach (string mode in cases)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Missing test host."))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--case"); start.ArgumentList.Add(mode);
                if (mode is "background" or "no-tray" or "secondary-background" or "after-setup-background") start.ArgumentList.Add("--background");
                if (mode == "offline-smoke") start.ArgumentList.Add("--ui-smoke");
                if (mode == "offline-screenshot") start.ArgumentList.Add("--screenshot");
                if (mode.StartsWith("after-setup", StringComparison.Ordinal) || mode == "offline-after-setup") start.ArgumentList.Add("--after-setup");
                if (mode == "offline-after-setup") start.ArgumentList.Add("--ui-smoke");
                using var process = Process.Start(start) ?? throw new IOException("Test helper did not start.");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) throw new TimeoutException("Startup fixture did not exit: " + mode);
                string result = output.GetAwaiter().GetResult().Trim();
                if (process.ExitCode != 0 || !result.StartsWith("PASS ", StringComparison.Ordinal)) throw new InvalidOperationException(mode + ": " + result + " " + error.GetAwaiter().GetResult());
                assertions += int.Parse(result.Split(' ')[1]);
            }
            Console.WriteLine($"PASS {assertions} WPF application startup checks across {cases.Length} isolated processes; production App.xaml/OnStartup, no native tray, mutex, settings or USB access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int RunCase(string mode)
    {
        StartupFixture.Mode = mode;
        var app = new App();
        app.InitializeComponent();
        Check(app.StartupUri is null, "Production XAML must not schedule a second implicit MainWindow.");
        bool reachedWindow = false;
        Exception? failure = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timeout.Tick += (_, _) => { failure = new TimeoutException("Isolated startup fixture timed out."); app.Shutdown(1); };
        timeout.Start();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is InGlorious.Core.MainWindow) return;
            // Only startup notices use modal windows; close these fixtures, not any real application.
            StartupFixture.Notices++;
            ((Window)sender).Dispatcher.BeginInvoke(() => ((Window)sender).Close());
        }));
        app.DispatcherUnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; app.Shutdown(1); };
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                reachedWindow = true;
                var window = app.MainWindow as InGlorious.Core.MainWindow ?? throw new InvalidOperationException("Missing explicit MainWindow.");
                bool offline = mode.StartsWith("offline", StringComparison.Ordinal);
                bool background = mode is "background" or "after-setup-background";
                Check(StartupFixture.WindowsCreated == 1, "Exactly one editor is constructed.");
                Check(app.ShutdownMode == ShutdownMode.OnMainWindowClose, "The actual main window owns application lifetime.");
                Check(window.IsVisible != background, "Normal/offline/no-tray shows the window; background starts hidden.");
                Check(StartupFixture.BackgroundInitializations == (background ? 1 : 0), "Hidden initialization runs only with a reachable tray.");
                Check(StartupFixture.TrayAttempts == (offline ? 0 : 1), "Offline startup cannot touch the native tray boundary.");
                Check(StartupFixture.InstancesCreated == (offline ? 0 : 1), "Offline startup cannot claim the production instance boundary.");
                Check(StartupFixture.PreferenceReads == 0, "Successful startup never reads settings from the notice boundary.");
                if (!offline && mode != "no-tray")
                {
                    StartupFixture.Open!(); Check(window.IsVisible, "Tray open shows the same main window.");
                    window.Close(); Check(!window.IsVisible && app.Windows.Count == 1, "X keeps the same window and process alive.");
                    StartupFixture.Exit!();
                }
                else window.Close();
            }
            catch (Exception error) { failure = error; app.Shutdown(1); }
        }));
        int exit = app.Run();
        timeout.Stop();
        if (failure is not null) throw failure;
        Check(exit == 0, "Startup and orderly exit succeed.");
        Check(StartupFixture.GuardsCreated == 1 && StartupFixture.GuardsDisposed == 1, "Startup guard lives until application exit, including rejected starts.");
        bool rejects = mode is "secondary" or "secondary-background" or "secondary-unreachable" or "installer-running" or "guard-unavailable" or "instance-error" or "after-setup-timeout" or "after-setup-unavailable" or "after-setup-shutdown";
        Check(reachedWindow != rejects, "Rejected startup never reaches the editor branch.");
        if (rejects)
        {
            Check(StartupFixture.WindowsCreated == 0 && StartupFixture.TrayAttempts == 0 && StartupFixture.BackgroundInitializations == 0, "Rejected startup never constructs an editor or initializes devices/tray.");
            int notices = mode is "secondary" or "secondary-background" or "after-setup-shutdown" ? 0 : 1;
            Check(StartupFixture.Notices == notices && StartupFixture.PreferenceReads == notices, "Only the expected startup error notice is shown.");
            if (mode.StartsWith("secondary", StringComparison.Ordinal)) Check(StartupFixture.Activation == (mode != "secondary-background"), "Second normal launch activates; background launch does not.");
        }
        Check(StartupFixture.InstancesDisposed == (StartupFixture.InstancesCreated - (mode == "instance-error" ? 1 : 0)), "All successfully acquired instance resources are released.");
        bool acquiredTray = !rejects && !mode.StartsWith("offline", StringComparison.Ordinal) && mode != "no-tray";
        Check(StartupFixture.TraysDisposed == (acquiredTray ? 1 : 0), "All successfully created tray resources are released.");
        Check(StartupFixture.SetupWaits == (mode.StartsWith("after-setup", StringComparison.Ordinal) ? 1 : 0), "Only explicit non-offline post-setup launches await the installer.");
        bool deferred = mode is "after-setup-release" or "after-setup-background" or "after-setup-timeout" or "after-setup-shutdown";
        Check(StartupFixture.DeferredBoundaryChecks == (deferred ? 2 : 0), "Deferred startup preserves all guard boundaries before and after yielding the dispatcher.");
        Console.WriteLine($"PASS {_checks} {mode}");
        return 0;
    }
}
