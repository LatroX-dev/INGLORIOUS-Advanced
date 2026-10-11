using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class App : Application
{
    private WindowsInstallationGuard? _installationGuard;
    private BackgroundApplicationSession? _backgroundSession;
    private ApplicationInstance? _instance;
    private bool _pendingActivation;

    public override void Initialize()
    {
        Name = "INGLORIOUS Advanced";
        AvaloniaXamlLoader.Load(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsWindows())
            {
                _installationGuard = new WindowsInstallationGuard();
                desktop.Exit += (_, _) => { _installationGuard?.Dispose(); _installationGuard = null; };
            }
            var settings = Program.Offline ? new AppPreferencesLoadResult(new(), null) : AppPreferencesStore.Load();
            var language = Array.IndexOf(Program.Arguments, "--language");
            if (Program.Offline && language >= 0 && language + 1 < Program.Arguments.Length && L.IsLanguagePreference(Program.Arguments[language + 1]))
                settings = settings with { Preferences = settings.Preferences with { Language = Program.Arguments[language + 1] } };
            L.SetLanguage(settings.Preferences.Language);
            if (_installationGuard is { CanStart: false })
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = CreateStartupNotice(_installationGuard.State);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            if (!Program.Offline)
            {
                try
                {
                    _instance = new ApplicationInstance(() => Dispatcher.UIThread.Post(() =>
                    {
                        if (desktop.MainWindow is MainWindow main) main.OpenFromTray();
                        else _pendingActivation = true;
                    }));
                    desktop.Exit += (_, _) => { _instance?.Dispose(); _instance = null; };
                    if (!_instance.IsPrimary)
                    {
                        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        bool notified = _instance.NotifyExistingAsync(!Program.Arguments.Contains("--background")).GetAwaiter().GetResult();
                        if (!notified) { desktop.ShutdownMode = ShutdownMode.OnMainWindowClose; desktop.MainWindow = CreateStartupNotice(ApplicationStartupState.GuardUnavailable); }
                        else Dispatcher.UIThread.Post(() => desktop.Shutdown());
                        base.OnFrameworkInitializationCompleted(); return;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
                {
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    desktop.MainWindow = CreateStartupNotice(ApplicationStartupState.GuardUnavailable);
                    base.OnFrameworkInitializationCompleted(); return;
                }
            }
            var window = new MainWindow(settings.Preferences, settings.Warning);
            if (BackgroundApplicationSession.Requested(Program.Arguments, Program.Offline))
            {
                // The lifetime automatically shows MainWindow before starting its loop.
                // Assign it only after that stage, so a tray launch never flashes a window.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.Exit += (_, _) => { _backgroundSession?.Dispose(); _backgroundSession = null; };
                Dispatcher.UIThread.Post(async () =>
                {
                    desktop.MainWindow = window;
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    if (_pendingActivation) { _pendingActivation = false; window.OpenFromTray(); }
                    _backgroundSession = new(window.OpenFromTray, window.InitializeWindowOnce, window.Close);
                    bool available = await DesktopTrayIcon.HasNotificationAreaAsync();
                    _backgroundSession.Start(() => available ? DesktopTrayIcon.TryCreate(this, _backgroundSession.Open, _backgroundSession.Exit, _backgroundSession.TrayUnavailable) : null);
                });
            }
            else { desktop.MainWindow = window; if (_pendingActivation) { _pendingActivation = false; window.OpenFromTray(); } }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static Window CreateStartupNotice(ApplicationStartupState state)
    {
        var dialog = new Window
        {
            Title = "INGLORIOUS Advanced", Width = 490, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        using (var icon = AssetLoader.Open(new Uri("avares://INGLORIOUS Advanced/Assets/inglorious.ico"))) dialog.Icon = new WindowIcon(icon);
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        content.Children.Add(new TextBlock { Text = "INGLORIOUS Advanced", FontSize = 20, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = L.T(state == ApplicationStartupState.InstallerRunning
            ? "Die Installation läuft. Öffne INGLORIOUS Advanced erneut, sobald sie abgeschlossen ist."
            : "INGLORIOUS Advanced konnte nicht gestartet werden. Bitte starte die App erneut."), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        var close = new Button { Content = L.T("Schließen"), IsDefault = true, IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Classes.Add("primary"); close.Click += (_, _) => dialog.Close();
        content.Children.Add(close); dialog.Content = content;
        return dialog;
    }
}
