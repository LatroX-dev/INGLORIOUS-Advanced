using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace InGlorious.Core;

public partial class App : Application
{
    private WindowsInstallationGuard? _installationGuard;
    private BackgroundApplicationSession? _backgroundSession;
    private ApplicationInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The installer checks this process-lifetime handle before replacing app files.
        _installationGuard = new WindowsInstallationGuard();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyWindowFrame((Window)sender)));
        if (!_installationGuard.CanStart)
        {
            // MainWindow is constructed explicitly only after both startup guards succeed.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            L.SetLanguage(AppPreferencesStore.Load().Preferences.Language);
            ShowStartupNotice(_installationGuard.State);
            Shutdown();
            return;
        }
        bool offline = e.Args.Contains("--screenshot") || e.Args.Contains("--ui-smoke");
        if (!offline)
        {
            try
            {
                _instance = new ApplicationInstance(() => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.OpenFromTray()));
                if (!_instance.IsPrimary)
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    if (!_instance.NotifyExistingAsync(!e.Args.Contains("--background")).GetAwaiter().GetResult())
                    { L.SetLanguage(AppPreferencesStore.Load().Preferences.Language); ShowStartupNotice(ApplicationStartupState.GuardUnavailable); }
                    Shutdown(); return;
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                L.SetLanguage(AppPreferencesStore.Load().Preferences.Language); ShowStartupNotice(ApplicationStartupState.GuardUnavailable); Shutdown(); return;
            }
        }
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow();
        MainWindow = window;
        if (offline) window.Show();
        else
        {
            _backgroundSession = new(window.OpenFromTray, window.InitializeInBackground, window.RequestApplicationExit, window.HideToTray);
            window.AttachTraySession(_backgroundSession);
            _backgroundSession.Start(() => WindowsTrayIcon.TryCreate(_backgroundSession.Open, _backgroundSession.Exit, _backgroundSession.TrayUnavailable),
                startHidden: BackgroundApplicationSession.Requested(e.Args, offline));
        }
        base.OnStartup(e);
    }

    private static void ShowStartupNotice(ApplicationStartupState state)
    {
        var dialog = new Window
        {
            Title = "INGLORIOUS Advanced", Width = 490, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/inglorious.ico"))
        };
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "INGLORIOUS Advanced", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        content.Children.Add(new TextBlock { Text = L.T(state == ApplicationStartupState.InstallerRunning
            ? "Die Installation läuft. Öffne INGLORIOUS Advanced erneut, sobald sie abgeschlossen ist."
            : "INGLORIOUS Advanced konnte nicht gestartet werden. Bitte starte die App erneut."), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        var close = new Button { Content = L.T("Schließen"), IsDefault = true, IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0), Style = (Style)Current.FindResource("PrimaryButton") };
        close.Click += (_, _) => dialog.Close();
        content.Children.Add(close); dialog.Content = content;
        dialog.ShowDialog();
    }

    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast) || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { foreach (Window window in Windows) ApplyWindowFrame(window); });
    }

    private static void ApplyWindowFrame(Window window)
    {
        if (window.WindowStyle == WindowStyle.None) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var contrast = SystemParameters.HighContrast;
        // Native Windows frame: preserve snap, resize and standard window buttons.
        // COLORREF is BGR. Unsupported DWM attributes leave the system defaults intact.
        uint dark = contrast ? 0u : 1u;
        uint caption = contrast ? uint.MaxValue : 0x00221F1C; // #1C1F22
        uint text = contrast ? uint.MaxValue : 0x00F3F1F0; // #F0F1F3
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(uint));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        _backgroundSession?.Dispose(); _backgroundSession = null;
        _instance?.Dispose(); _instance = null;
        _installationGuard?.Dispose();
        _installationGuard = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // WPF can end a Windows session without raising the main window's Closing event.
        // Use that same guarded path explicitly; a busy firmware window may veto it.
        if (MainWindow is MainWindow window && !window.RequestSessionEnd()) e.Cancel = true;
        base.OnSessionEnding(e);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
