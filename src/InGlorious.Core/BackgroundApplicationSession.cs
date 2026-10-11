namespace InGlorious.Core;

/// <summary>Owns the background entry point without bypassing the window's normal close checks.</summary>
public sealed class BackgroundApplicationSession(Action showWindow, Action initialize, Action requestClose, Action? hideWindow = null) : IDisposable
{
    private IDisposable? _tray;
    private bool _started, _disposed;

    public static bool Requested(IEnumerable<string> arguments, bool offline) => !offline && arguments.Contains("--background", StringComparer.Ordinal);

    public void Start(Func<IDisposable?> createTray, bool startHidden = true)
    {
        if (_started || _disposed) return;
        _started = true;
        _tray = createTray();
        if (_tray is null || !startHidden) { showWindow(); return; }
        initialize();
    }

    // True means that the window must cancel closing. Its editor and device services stay alive.
    public bool HandleWindowClose(bool closingApplication, bool hasOpenDialog = false)
    {
        if (_disposed || _tray is null || hideWindow is null || closingApplication) return false;
        if (hasOpenDialog) showWindow();
        else hideWindow();
        return true;
    }

    public void Open() { if (!_disposed) showWindow(); }
    public void Exit()
    {
        if (_disposed) return;
        // The owner must be visible for unsaved-profile and running-operation dialogs.
        showWindow();
        requestClose();
    }
    public void TrayUnavailable()
    {
        if (_disposed) return;
        _tray?.Dispose(); _tray = null;
        showWindow();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _tray?.Dispose(); _tray = null;
    }
}
