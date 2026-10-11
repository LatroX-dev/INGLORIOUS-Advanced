using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace InGlorious.Core;

/// <summary>A native notification-area icon; its message window is never shown.</summary>
internal sealed class WindowsTrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private readonly HwndSource _messages;
    private readonly ContextMenu _menu = new();
    private readonly Action _open, _unavailable;
    private readonly Action _updateLanguage;
    private readonly DispatcherTimer _availability = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData _data;
    private IntPtr _smallIcon, _largeIcon;
    private bool _added, _disposed;

    private WindowsTrayIcon(Action open, Action close, Action unavailable)
    {
        _open = open; _unavailable = unavailable;
        _messages = new HwndSource(new HwndSourceParameters("INGLORIOUS Advanced notification area")
        { Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = 0x80 });
        _messages.AddHook(Message);
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _messages.Handle, Id = 1,
            Flags = 1 | 2 | 4 | 0x80, CallbackMessage = CallbackMessage,
            Tip = "INGLORIOUS Advanced", Info = "", InfoTitle = ""
        };
        var title = new MenuItem { Header = "INGLORIOUS Advanced", IsEnabled = false };
        var show = new MenuItem { Header = L.T("Öffnen") }; show.Click += (_, _) => open();
        var exit = new MenuItem { Header = L.T("Beenden") }; exit.Click += (_, _) => close();
        _menu.Items.Add(title); _menu.Items.Add(new Separator()); _menu.Items.Add(show); _menu.Items.Add(exit);
        _updateLanguage = () => { show.Header = L.T("Öffnen"); exit.Header = L.T("Beenden"); };
        L.LanguageChanged += _updateLanguage;
        _availability.Tick += (_, _) =>
        {
            if (_disposed) return;
            if (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero || !ShellNotifyIcon(1, ref _data)) LostTray();
        };
    }

    internal static WindowsTrayIcon? TryCreate(Action open, Action close, Action unavailable)
    {
        if (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero || Environment.ProcessPath is not string executable) return null;
        WindowsTrayIcon? tray = null;
        try
        {
            tray = new(open, close, unavailable);
            if (ExtractIconEx(executable, 0, out tray._largeIcon, out tray._smallIcon, 1) == 0 || tray._smallIcon == IntPtr.Zero)
            { tray.Dispose(); return null; }
            tray._data.Icon = tray._smallIcon;
            if (!tray.Add()) { tray.Dispose(); return null; }
            tray._availability.Start();
            return tray;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        { tray?.Dispose(); return null; }
    }

    private bool Add()
    {
        _added = ShellNotifyIcon(0, ref _data);
        if (!_added) return false;
        _data.Version = 4;
        if (ShellNotifyIcon(4, ref _data)) return true;
        ShellNotifyIcon(2, ref _data); _added = false; return false;
    }

    private IntPtr Message(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (message == _taskbarCreated)
        {
            _added = false;
            if (!Add()) _messages.Dispatcher.BeginInvoke(LostTray);
        }
        else if (message == CallbackMessage)
        {
            handled = true;
            int notification = (int)(lParam.ToInt64() & 0xffff);
            if (notification is 0x400 or 0x401) _messages.Dispatcher.BeginInvoke(_open);
            else if (notification == 0x7b)
            {
                SetForegroundWindow(window);
                _menu.Placement = PlacementMode.MousePoint; _menu.IsOpen = true;
                PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        return IntPtr.Zero;
    }

    private void LostTray()
    {
        if (_disposed) return;
        _availability.Stop(); _unavailable();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _availability.Stop(); _menu.IsOpen = false;
        L.LanguageChanged -= _updateLanguage;
        if (_added) ShellNotifyIcon(2, ref _data);
        _messages.RemoveHook(Message); _messages.Dispose();
        if (_smallIcon != IntPtr.Zero) DestroyIcon(_smallIcon);
        if (_largeIcon != IntPtr.Zero && _largeIcon != _smallIcon) DestroyIcon(_largeIcon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
