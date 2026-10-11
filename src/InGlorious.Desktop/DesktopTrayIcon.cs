using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

internal sealed class DesktopTrayIcon : IDisposable
{
    private readonly Application _app;
    private readonly TrayIcon _icon;
    private readonly Action _updateLanguage;
    private readonly DispatcherTimer _availability = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _checking, _disposed;

    private DesktopTrayIcon(Application app, TrayIcon icon, Action unavailable, Action updateLanguage)
    {
        _app = app; _icon = icon;
        _updateLanguage = updateLanguage;
        L.LanguageChanged += _updateLanguage;
        _availability.Tick += async (_, _) =>
        {
            if (_disposed || _checking) return;
            _checking = true;
            try { if (!await HasNotificationAreaAsync() && !_disposed) unavailable(); }
            finally { _checking = false; }
        };
        _availability.Start();
    }

    internal static async Task<bool> HasNotificationAreaAsync()
    {
        if (OperatingSystem.IsWindows()) return FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
        if (OperatingSystem.IsMacOS()) return true;
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/dbus-send")) return false;
        // Avalonia can create a Linux tray backend even when no panel displays it.
        // Read the session bus host state before allowing an invisible startup.
        try
        {
            var start = new ProcessStartInfo("/usr/bin/dbus-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "--session", "--print-reply", "--reply-timeout=1500", "--dest=org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.freedesktop.DBus.Properties.Get", "string:org.kde.StatusNotifierWatcher", "string:IsStatusNotifierHostRegistered" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } return false; }
            await errors;
            return process.ExitCode == 0 && Regex.IsMatch(await output, @"\bvariant\s+boolean\s+true\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or RegexMatchTimeoutException) { return false; }
    }

    internal static DesktopTrayIcon? TryCreate(Application app, Action open, Action close, Action unavailable)
    {
        TrayIcon? icon = null;
        try
        {
            var menu = new NativeMenu();
            menu.Items.Add(new NativeMenuItem("INGLORIOUS Advanced") { IsEnabled = false });
            menu.Items.Add(new NativeMenuItemSeparator());
            var show = new NativeMenuItem(L.T("Öffnen")); show.Click += (_, _) => open(); menu.Items.Add(show);
            var exit = new NativeMenuItem(L.T("Beenden")); exit.Click += (_, _) => close(); menu.Items.Add(exit);
            using var image = AssetLoader.Open(new Uri("avares://INGLORIOUS Advanced/Assets/inglorious.ico"));
            icon = new TrayIcon { Icon = new WindowIcon(image), ToolTipText = "INGLORIOUS Advanced", Menu = menu, IsVisible = true };
            icon.Clicked += (_, _) => open();
            TrayIcon.SetIcons(app, new TrayIcons { icon });
            if (icon.NativeMenuExporter is null)
            { TrayIcon.SetIcons(app, null); icon.Dispose(); return null; }
            return new DesktopTrayIcon(app, icon, unavailable,
                () => { show.Header = L.T("Öffnen"); exit.Header = L.T("Beenden"); });
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or ArgumentException or ExternalException)
        { TrayIcon.SetIcons(app, null); icon?.Dispose(); return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _availability.Stop(); TrayIcon.SetIcons(_app, null); _icon.Dispose();
        L.LanguageChanged -= _updateLanguage;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
}
