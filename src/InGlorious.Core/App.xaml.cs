using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InGlorious.Core;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyWindowFrame((Window)sender)));
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        base.OnStartup(e);
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
        base.OnExit(e);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
