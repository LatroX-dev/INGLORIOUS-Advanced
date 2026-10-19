using Avalonia.Controls;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _knobStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private Func<Task<IReadOnlyList<OnboardDevice>>> _findKnobDevices = PlatformDeviceService.FindAsync;
    private Func<OnboardDevice, CancellationToken, Task<OnboardKnobStatus?>> _readKnobStatus = PlatformDeviceService.GetKnobStatusAsync;
    private CancellationTokenSource? _knobStatusCancellation;
    private TaskCompletionSource? _knobStatusCompletion;
    private int _knobStatusPauseDepth;
    private OnboardKnobStatus? _knobStatus;
    private EditorState? _knobStatusState;
    private string? _knobStatusPath;
    private bool _knobStatusReading, _knobStatusStarted;
    private long _knobStatusRevision, _knobNoticeRevision;
    private int _knobStatusConnectionRevision;
    private DateTime _knobStatusNextRead, _knobStatusReadAt;
    private IDisposable? _knobNoticeTimer;
    private string? _knobNoticePrevious;

    private bool CanReadKnobStatus => _knobStatusPauseDepth == 0 && CanRefreshConnections && IsVisible && WindowState != WindowState.Minimized && CurrentModelConnected &&
        _page == "Tastenbelegung" && (IsKnobSelection() || _state.Device.Keys.Any(key => (key.IsEncoder || key.Label == "Knob") &&
            _keyButtons.TryGetValue(key.Id, out var button) && button.IsPointerOver));

    private void StartKnobStatus()
    {
        if (Program.Offline || _knobStatusStarted || _startupWindowClosed) return;
        _knobStatusStarted = true;
        _knobStatusTimer.Tick += async (_, _) => await RefreshKnobStatusAsync();
        _knobStatusTimer.Start();
    }

    private void StopKnobStatus()
    {
        _knobStatusTimer.Stop(); _knobStatusRevision++;
        _knobStatusCancellation?.Cancel(); _knobNoticeTimer?.Dispose(); _knobNoticeTimer = null;
    }

    private void ResetKnobStatus()
    {
        _knobStatusRevision++;
        if (_knobStatusPauseDepth == 0) _knobStatusCancellation?.Cancel();
        _knobStatus = null; _knobStatusState = null; _knobStatusPath = null; _knobStatusNextRead = default; _knobStatusReadAt = default;
    }

    private async Task<IDisposable> PauseKnobStatusAsync()
    {
        _knobStatusPauseDepth++;
        ResetKnobStatus();
        var pause = new KnobStatusPause(() => _knobStatusPauseDepth--);
        try
        {
            // A canceled read can still be disposing its shared RAW lease. Do not let
            // a foreground operation open that channel until the entire poll ends.
            if (_knobStatusCompletion is { } completion) await completion.Task;
            return pause;
        }
        catch { pause.Dispose(); throw; }
    }

    private sealed class KnobStatusPause(Action resume) : IDisposable
    {
        private Action? _resume = resume;
        public void Dispose() => Interlocked.Exchange(ref _resume, null)?.Invoke();
    }

    private void SetKnobToolTip(Control control, string baseline)
    {
        var state = _state;
        var content = new TextBlock { Text = baseline };
        ToolTip.SetTip(control, content);
        // Never replace or update an open tooltip from the polling loop.
        ToolTip.AddToolTipOpeningHandler(control, (_, _) => content.Text = KnobToolTipText(baseline, state));
        control.PointerEntered += async (_, _) => await RefreshKnobStatusAsync();
    }

    private string KnobToolTipText(string baseline, EditorState state)
    {
        var age = DateTime.UtcNow - _knobStatusReadAt;
        if (!CanReadKnobStatus || !ReferenceEquals(state, _state) || !ReferenceEquals(_knobStatusState, state) ||
            _knobStatus?.DeviceId != state.Device.Id || string.IsNullOrEmpty(_knobStatusPath) ||
            _knobStatusConnectionRevision != _connectionRevision || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(3)) return baseline;
        return baseline + "\n\n" + L.F($"Auf der Tastatur aktiv: {_knobStatus.Label}") + "\n" + L.F($"Ebene {_knobStatus.Layer}");
    }

    private void ShowKnobChangeNotice(OnboardKnobStatus status)
    {
        if (_knobNoticePrevious is null || _statusRevision != _knobNoticeRevision) _knobNoticePrevious = _status;
        _knobNoticeTimer?.Dispose();
        Status(L.F($"Drehregler: {status.Label}"));
        var revision = _knobNoticeRevision = _statusRevision;
        _knobNoticeTimer = DispatcherTimer.RunOnce(() => ExpireKnobChangeNotice(revision), TimeSpan.FromSeconds(3));
    }

    private void ExpireKnobChangeNotice(long revision)
    {
        if (revision != _knobNoticeRevision) return;
        if (_statusRevision == revision && _knobNoticePrevious is { } previous) Status(previous);
        _knobNoticePrevious = null; _knobNoticeTimer?.Dispose(); _knobNoticeTimer = null;
    }

    private async Task RefreshKnobStatusAsync(bool allowOfflineFixture = false)
    {
        if (_knobStatusPauseDepth != 0) return;
        if (!CanReadKnobStatus) { ResetKnobStatus(); return; }
        if (Program.Offline && !allowOfflineFixture || _knobStatusReading || DateTime.UtcNow < _knobStatusNextRead) return;
        var state = _state; var model = state.Device.Id; var revision = _knobStatusRevision; var connection = _connectionRevision;
        bool Current() => revision == _knobStatusRevision && connection == _connectionRevision && ReferenceEquals(state, _state) && CanReadKnobStatus;
        _knobStatusReading = true;
        var completion = _knobStatusCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _knobStatusCancellation = cancellation;
        try
        {
            var matches = (await _findKnobDevices().WaitAsync(cancellation.Token)).Where(device => device.DeviceId == model).ToArray();
            if (!Current()) return;
            if (matches.Length != 1) throw new InvalidOperationException("No unique knob device.");
            var device = matches[0];
            var status = await _readKnobStatus(device, cancellation.Token);
            if (!Current()) return;
            if (status is not null && status.DeviceId != model) throw new InvalidDataException("Unexpected knob status device.");
            var sameRuntime = status is not null && _knobStatus is not null && ReferenceEquals(_knobStatusState, state) &&
                _knobStatusConnectionRevision == connection && _knobStatusPath == device.Path &&
                status.Generation == _knobStatus.Generation && status.ActiveCrc == _knobStatus.ActiveCrc;
            var changed = sameRuntime && status!.ChangeCounter != _knobStatus!.ChangeCounter;
            _knobStatus = status; _knobStatusState = state; _knobStatusPath = device.Path;
            _knobStatusConnectionRevision = connection; _knobStatusReadAt = DateTime.UtcNow;
            _knobStatusNextRead = status is null ? DateTime.UtcNow.AddSeconds(15) : default;
            if (changed) ShowKnobChangeNotice(status!);
        }
        catch (Exception)
        {
            if (!Current()) return;
            _knobStatus = null; _knobStatusState = null; _knobStatusPath = null; _knobStatusReadAt = default;
            _knobStatusNextRead = DateTime.UtcNow.AddSeconds(3);
        }
        finally
        {
            if (ReferenceEquals(_knobStatusCancellation, cancellation)) _knobStatusCancellation = null;
            cancellation.Dispose();
            _knobStatusReading = false;
            if (ReferenceEquals(_knobStatusCompletion, completion)) _knobStatusCompletion = null;
            completion.TrySetResult();
        }
    }
}
