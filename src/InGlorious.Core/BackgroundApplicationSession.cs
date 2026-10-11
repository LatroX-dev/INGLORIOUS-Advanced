namespace InGlorious.Core;

/// <summary>Owns the background entry point without bypassing the window's normal close checks.</summary>
public sealed class BackgroundApplicationSession(Action showWindow, Action initialize, Action requestClose) : IDisposable
{
    private IDisposable? _tray;
    private bool _started, _disposed;

    public static bool Requested(IEnumerable<string> arguments, bool offline) => !offline && arguments.Contains("--background", StringComparer.Ordinal);

    public void Start(Func<IDisposable?> createTray)
    {
        if (_started || _disposed) return;
        _started = true;
        _tray = createTray();
        if (_tray is null) { showWindow(); return; }
        initialize();
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
