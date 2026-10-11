using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace InGlorious.Core;

public partial class MacroRecorderWindow : Window
{
    private readonly MacroRecording _recording = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private HwndSource? _source;
    private int _displayedSteps = -1;
    public IReadOnlyList<MacroStep>? RecordedSteps { get; private set; }

    public MacroRecorderWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowMessage);
        };
        Deactivated += (_, _) => StopForFocusLoss();
        IsKeyboardFocusWithinChanged += (_, _) => { if (!IsKeyboardFocusWithin) StopForFocusLoss(); };
        _timer.Tick += (_, _) => { _recording.Advance(_clock.ElapsedMilliseconds); Refresh(); };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _recording.Stop(_clock.ElapsedMilliseconds);
            _source?.RemoveHook(WindowMessage); _source = null;
        };
    }
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            RecordingStatusText.Text = L.T("Zum Start zuerst Strg, Umschalt, Alt und Windows-Taste loslassen.");
            return;
        }
        RecordedSteps = null;
        // Focus first: the triggering Space/Enter down is not part of the recording.
        Keyboard.Focus(RecordingSurface);
        if (!IsActive || !IsKeyboardFocusWithin) return;
        _recording.Start(_clock.ElapsedMilliseconds); _displayedSteps = -1;
        _timer.Start(); Refresh();
    }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _recording.Stop(_clock.ElapsedMilliseconds); Refresh();
    }
    private void StopForFocusLoss()
    {
        if (!_recording.IsRecording) return;
        _recording.Stop(_clock.ElapsedMilliseconds, "Aufnahme bei Fokusverlust gestoppt."); Refresh();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _recording.Stop(_clock.ElapsedMilliseconds); RecordedSteps = null; DialogResult = false;
    }
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_recording.IsRecording || _recording.StepCount == 0) return;
        RecordedSteps = _recording.Steps; DialogResult = true;
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Window-local messages only: no system keyboard hooks and no synthetic input.
        if (!_recording.IsRecording || !IsActive || !IsKeyboardFocusWithin) return 0;
        if (message is 0x0102 or 0x0103 or 0x0106 or 0x0107) { handled = true; return 0; } // char/dead-char
        if (message == 0x0319) // WM_APPCOMMAND: outside the macro keyboard vocabulary.
        {
            _recording.Unsupported(L.T("Medien-/Anwendungstaste"), _clock.ElapsedMilliseconds);
            Refresh(); handled = true; return 1;
        }
        if (message is not (0x0100 or 0x0101 or 0x0104 or 0x0105)) return 0;
        var flags = unchecked((uint)lParam.ToInt64());
        var scan = (int)((flags >> 16) & 0xFF);
        var extended = (flags & (1U << 24)) != 0;
        var down = message is 0x0100 or 0x0104;
        if (MacroPhysicalKeys.TryMap(scan, extended, unchecked((int)wParam.ToInt64()), out var code))
        {
            if (down) _recording.KeyDown(code, _clock.ElapsedMilliseconds, (flags & (1U << 30)) != 0);
            else _recording.KeyUp(code, _clock.ElapsedMilliseconds);
        }
        else if (down)
        {
            var name = KeyInterop.KeyFromVirtualKey(unchecked((int)wParam.ToInt64())).ToString();
            _recording.Unsupported(extended ? L.F($"{name} (Scan {scan:X2}, erweitert)") : L.F($"{name} (Scan {scan:X2})"), _clock.ElapsedMilliseconds);
        }
        // Suppress normal dialog shortcuts while recording, including Tab/Enter/Escape.
        handled = true; Refresh(); return 0;
    }
    private void Refresh()
    {
        var active = _recording.IsRecording;
        StartRecordingButton.IsEnabled = !active;
        StartRecordingButton.Content = _recording.StepCount == 0 ? L.T("Aufnahme starten") : L.T("Neu aufnehmen");
        StopRecordingButton.IsEnabled = active;
        AcceptRecordingButton.IsEnabled = !active && _recording.StepCount > 0;
        // The recorder retains its original status tokens; only their display is localized.
        const string shortened = " Die letzte Haltezeit wurde wegen der Schrittgrenze gekürzt.";
        RecordingStatusText.Text = _recording.Status.EndsWith(shortened, StringComparison.Ordinal)
            ? L.T(_recording.Status[..^shortened.Length]) + L.T(shortened)
            : L.T(_recording.Status);
        RecordingCountText.Text = L.F($"{_recording.StepCount} / 128 Schritte · {_recording.ElapsedMs / 1000.0:0.0} s");
        UnsupportedKeysText.Text = _recording.UnsupportedInputs.Count == 0 ? "" : L.T("Nicht aufgezeichnet: ") + string.Join(", ", _recording.UnsupportedInputs);
        if (_displayedSteps != _recording.StepCount)
        {
            _displayedSteps = _recording.StepCount;
            RecordedStepsList.ItemsSource = _recording.Steps.Select((step, index) =>
                $"{index + 1:000}  " + (step.Kind == "delay" ? L.F($"Pause {step.DelayMs} ms") : (step.Kind == "down" ? L.T("Drücken") : L.T("Loslassen")) + "  " + MacroPhysicalKeys.Label(step.Key))).ToArray();
            if (_displayedSteps > 0) RecordedStepsList.ScrollIntoView(RecordedStepsList.Items[^1]);
        }
        if (!active) _timer.Stop();
    }
}
