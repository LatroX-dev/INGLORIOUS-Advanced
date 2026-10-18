using Avalonia.Controls;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private bool _openedOnce;
    private BackgroundApplicationSession? _traySession;
    private bool _applicationExitRequested;

    internal bool ApplicationExitApproved => _allowClose || _startupWindowClosed;
    internal void AttachTraySession(BackgroundApplicationSession session) => _traySession = session;

    internal void HideToTray() => Hide();

    internal void RequestApplicationExit()
    {
        if (_startupWindowClosed || _closing) return;
        _applicationExitRequested = true;
        try { OpenFromTray(); Close(); }
        finally
        {
            // An owned firmware dialog can veto Close before MainWindow.Closing runs.
            // Async pending-edit prompts instead retain this flag until their own finally block.
            if (!_closing && !_allowClose) _applicationExitRequested = false;
        }
    }

    private bool KeepWindowInTray(WindowCloseReason reason) => _traySession?.HandleWindowClose(
        _applicationExitRequested || reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown,
        OwnedWindows.Any(window => window.IsVisible)) == true;

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
        var reopening = !IsVisible;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        foreach (var dialog in OwnedWindows) if (dialog.IsVisible) dialog.Activate();
        if (reopening) RequestDeviceConfigurationRefresh();
    }

    private void RunBackgroundWindowChecks(Action<bool, string> check)
    {
        if (!Program.Offline) throw new InvalidOperationException("Tray checks require offline mode.");
        var previousSession = _traySession; var previousExit = _applicationExitRequested;
        var previousState = _state; var previousPending = _pendingApply;
        var initializations = 0; var disposals = 0; var pendingCalls = 0;
        using var session = new BackgroundApplicationSession(OpenFromTray, () => initializations++, () => { }, HideToTray);
        EventHandler<WindowClosingEventArgs> veto = (_, e) => e.Cancel = true;
        Window? dialog = null;
        try
        {
            _state = new EditorState("pro-iso"); _state.MarkInitialClean();
            _state.SelectKey(_state.Device.Keys.First().Id, false);
            _state.Change(project => project.Name = "Unsaved close-to-tray check");
            Func<bool> pending = () => { pendingCalls++; return false; }; _pendingApply = pending;
            var editor = _state; var fingerprint = EditorState.Fingerprint(_state.Project); var selection = _state.SelectedKeys.ToArray();
            AttachTraySession(session);
            session.Start(() => new BackgroundCheckTray(() => disposals++), startHidden: false);
            Close();
            check(!IsVisible && !_startupWindowClosed, "window X hides the existing editor without closing its services");
            check(ReferenceEquals(editor, _state) && EditorState.Fingerprint(_state.Project) == fingerprint && _state.Dirty && _state.CanUndo
                && _state.SelectedKeys.SequenceEqual(selection) && ReferenceEquals(_pendingApply, pending) && pendingCalls == 0,
                "hiding preserves the unsaved profile, pending editor, history and selection");
            session.Open();
            check(IsVisible && initializations == 0 && ReferenceEquals(_state, editor), "tray Open restores the same editor without reading devices again");
            dialog = new Window { Width = 160, Height = 80, ShowInTaskbar = false };
            dialog.Closing += veto; dialog.Show(this);
            check(KeepWindowInTray(WindowCloseReason.WindowClosing) && IsVisible && dialog.IsVisible && ReferenceEquals(dialog.Owner, this),
                "close-to-tray keeps an owned dialog visible and attached");
            RequestApplicationExit();
            check(!_startupWindowClosed && !_applicationExitRequested && IsVisible && dialog.IsVisible,
                "child firmware-style veto before MainWindow.Closing clears the explicit Quit flag");
            dialog.Closing -= veto; dialog.Close(); dialog = null;
            Close();
            check(!IsVisible && !_startupWindowClosed, "X still hides after a child veto canceled explicit Quit");
            session.Open();
            check(!KeepWindowInTray(WindowCloseReason.OSShutdown) && !KeepWindowInTray(WindowCloseReason.ApplicationShutdown),
                "OS and application shutdown pass through to pending-edit and firmware guards");
            _applicationExitRequested = true;
            check(!KeepWindowInTray(WindowCloseReason.WindowClosing), "explicit tray Quit passes through normal close guards");
            _applicationExitRequested = false;
            session.TrayUnavailable();
            check(IsVisible && disposals == 1 && !KeepWindowInTray(WindowCloseReason.WindowClosing), "lost tray reveals the window and restores normal guarded closing");
        }
        finally
        {
            if (dialog is not null) { dialog.Closing -= veto; dialog.Close(); }
            _traySession = previousSession; _applicationExitRequested = previousExit;
            _state = previousState; _pendingApply = previousPending;
            OpenFromTray();
        }
    }

    private sealed class BackgroundCheckTray(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
