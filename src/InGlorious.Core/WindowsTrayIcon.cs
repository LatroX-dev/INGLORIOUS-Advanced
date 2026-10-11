using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;

namespace InGlorious.Core;

/// <summary>A native notification-area icon; its message window is never shown.</summary>
internal sealed class WindowsTrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private readonly HwndSource _messages;
    private readonly ContextMenu _menu = CreateMenu();
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
        var title = new MenuItem { Header = "INGLORIOUS Advanced", IsEnabled = false, FontWeight = FontWeights.SemiBold };
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

    // A tray popup has no Window from which to inherit our colors. Override its
    // templates as well as the brushes, so Windows does not draw a light gutter.
    private static ContextMenu CreateMenu() => (ContextMenu)XamlReader.Parse("""
        <ContextMenu xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     Background="{DynamicResource Surface}" Foreground="{DynamicResource Text}"
                     BorderBrush="{DynamicResource Stroke}" BorderThickness="1" Padding="5"
                     FontFamily="Segoe UI" FontSize="12" MinWidth="224" UseLayoutRounding="True"
                     SnapsToDevicePixels="True" TextOptions.TextFormattingMode="Display">
          <ContextMenu.Resources>
            <Style TargetType="MenuItem">
              <Setter Property="Foreground" Value="{DynamicResource Text}"/>
              <Setter Property="Background" Value="Transparent"/>
              <Setter Property="BorderBrush" Value="Transparent"/>
              <Setter Property="BorderThickness" Value="1"/>
              <Setter Property="Padding" Value="10,8"/>
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="VerticalContentAlignment" Value="Center"/>
              <Setter Property="Template"><Setter.Value>
                <ControlTemplate TargetType="MenuItem">
                  <Border x:Name="Row" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          CornerRadius="3" Padding="{TemplateBinding Padding}">
                    <ContentPresenter ContentSource="Header" RecognizesAccessKey="True"
                                      HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                      <Setter TargetName="Row" Property="Background" Value="{DynamicResource SurfaceRaised}"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                      <Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Foreground" Value="{DynamicResource Muted}"/>
                      <Setter TargetName="Row" Property="Background" Value="Transparent"/>
                      <Setter TargetName="Row" Property="BorderBrush" Value="Transparent"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value></Setter>
            </Style>
            <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator">
              <Setter Property="Height" Value="1"/>
              <Setter Property="Margin" Value="10,4"/>
              <Setter Property="Template"><Setter.Value>
                <ControlTemplate TargetType="Separator"><Border Background="{DynamicResource Stroke}"/></ControlTemplate>
              </Setter.Value></Setter>
            </Style>
          </ContextMenu.Resources>
          <ContextMenu.Template>
            <ControlTemplate TargetType="ContextMenu">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                      BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5" Padding="{TemplateBinding Padding}">
                <ItemsPresenter/>
              </Border>
            </ControlTemplate>
          </ContextMenu.Template>
        </ContextMenu>
        """);

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
