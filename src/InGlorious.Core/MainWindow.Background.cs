using System.Windows;
using System.Windows.Interop;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _backgroundSession;
    private BackgroundApplicationSession? _traySession;
    private bool _applicationExitRequested;

    internal void AttachTraySession(BackgroundApplicationSession session) => _traySession = session;

    internal void HideToTray()
    {
        _backgroundSession = true;
        Hide();
    }

    internal void RequestApplicationExit()
    {
        if (_startupClosed || _applicationExitRequested) return;
        _applicationExitRequested = true;
        try { OpenFromTray(); Close(); }
        finally { _applicationExitRequested = false; }
    }

    internal bool RequestSessionEnd()
    {
        RequestApplicationExit();
        return _startupClosed;
    }

    private bool KeepWindowInTray() => _traySession?.HandleWindowClose(_applicationExitRequested,
        OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) == true;

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

    private void RunBackgroundWindowChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Tray checks require offline mode.");
        var previousSession = _traySession; var previousExit = _applicationExitRequested; var previousBackground = _backgroundSession;
        var previousName = _project.Name; var previousInput = ProjectName.Text; var previousDecision = _pendingEditDecisionOverride;
        var editor = _project; var undo = _undo.Count; var redo = _redo.Count; var selection = _selection.ToArray();
        var initializations = 0; var disposals = 0;
        using var session = new BackgroundApplicationSession(OpenFromTray, () => initializations++, () => { }, HideToTray);
        System.ComponentModel.CancelEventHandler veto = (_, e) => e.Cancel = true;
        Window? dialog = null;
        try
        {
            _project.Name = "Unsaved close-to-tray check";
            ProjectName.Text = "Uncommitted tray input";
            var fingerprint = Fingerprint(_project);
            _pendingEditDecisionOverride = () => throw new InvalidOperationException("Window hide must not ask to save or discard edits.");
            AttachTraySession(session);
            session.Start(() => new BackgroundCheckTray(() => disposals++), startHidden: false);
            Close();
            check(!IsVisible && !_startupClosed && _backgroundSession, "window X hides the existing editor without closing its services");
            check(ReferenceEquals(editor, _project) && Fingerprint(_project) == fingerprint && ProjectName.Text == "Uncommitted tray input"
                && _undo.Count == undo && _redo.Count == redo && _selection.SequenceEqual(selection), "hiding preserves the unsaved profile, pending name, history and selection");
            session.Open();
            check(IsVisible && initializations == 0 && Fingerprint(_project) == fingerprint, "tray Open restores the same editor without reading devices again");
            dialog = new Window { Owner = this, Width = 160, Height = 80, ShowInTaskbar = false };
            dialog.Show();
            check(KeepWindowInTray() && IsVisible && dialog.IsVisible && ReferenceEquals(dialog.Owner, this), "close-to-tray keeps an owned dialog visible and attached");
            dialog.Close(); dialog = null;
            Closing += veto;
            RequestApplicationExit();
            check(!_startupClosed && !_applicationExitRequested && IsVisible, "a firmware-style closing veto cancels explicit Quit and clears its bypass flag");
            Closing -= veto;
            Close();
            check(!IsVisible && !_startupClosed, "X still hides after an explicit Quit was canceled");
            session.TrayUnavailable();
            check(IsVisible && disposals == 1 && !KeepWindowInTray(), "lost tray reveals the window and restores normal guarded closing");
            _applicationExitRequested = true;
            check(!KeepWindowInTray(), "explicit shutdown cannot be intercepted by close-to-tray");
        }
        finally
        {
            Closing -= veto; dialog?.Close();
            _traySession = previousSession; _applicationExitRequested = previousExit; _backgroundSession = previousBackground;
            _project.Name = previousName; ProjectName.Text = previousInput; _pendingEditDecisionOverride = previousDecision;
            OpenFromTray();
        }
    }

    private sealed class BackgroundCheckTray(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
