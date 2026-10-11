using System.Windows;
using System.Windows.Interop;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _backgroundSession;

    internal void InitializeInBackground()
    {
        if (_screenshotMode) return;
        _backgroundSession = true;
        // Receive WM_DEVICECHANGE while hidden; EnsureHandle does not show the window.
        new WindowInteropHelper(this).EnsureHandle();
        Window_Loaded(this, new RoutedEventArgs());
    }

    internal void OpenFromTray()
    {
        if (_startupClosed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        foreach (Window dialog in OwnedWindows) if (dialog.IsVisible) dialog.Activate();
    }
}
