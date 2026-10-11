using Avalonia.Controls;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private bool _openedOnce;

    internal async void InitializeWindowOnce()
    {
        if (_openedOnce || _startupWindowClosed) return;
        _openedOnce = true;
        await OnOpened();
        await StartConnectionsAsync();
    }

    internal void OpenFromTray()
    {
        if (_startupWindowClosed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        foreach (var dialog in OwnedWindows) if (dialog.IsVisible) dialog.Activate();
    }
}
