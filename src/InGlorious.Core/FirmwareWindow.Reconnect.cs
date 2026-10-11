using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace InGlorious.Core;

public partial class FirmwareWindow
{
    private FirmwareReconnectTarget? _reconnectTarget;
    private FirmwareReconnectTarget? _completedTarget;
    private bool _firmwareVerified, _checkingReconnect;
    private bool _lightingTestRunning;
    private CancellationTokenSource? _lightingTestCancellation;
    private long? _firstReconnectedAt;
    private string? _evidenceDirectory;
    private readonly Func<FirmwareReconnectTarget, Task<FirmwareAvailability>> _readNormalConnection;
    private readonly DispatcherTimer _reconnectTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private (Guid RunId, OnboardFirmwareVersion Version)? _normalVersion;
    public FirmwareRuntimeVersion? ConfirmedRuntimeVersion { get; private set; }

    private async Task<FirmwareAvailability> ReadNormalModeAsync(FirmwareReconnectTarget target)
    {
        var normal = await _service.CheckNormalConnectionAsync(target);
        if (!normal.Available || target.Operation == FirmwareOperation.RestoreOriginal) return normal;
        if (string.IsNullOrWhiteSpace(normal.NormalInstanceId)) return new(false, L.T("Die Firmware-Version konnte nicht geprüft werden."));
        var devices = (await Task.Run(OnboardClient.FindDevices)).Where(device => device.DeviceId == target.DeviceId
            && string.Equals(device.UsbInstanceId, normal.NormalInstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (devices.Length != 1) return new(false, L.T("Die Firmware-Version konnte nicht geprüft werden."));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var client = OnboardClient.Open(devices[0]);
        var info = await client.GetInfoAsync(timeout.Token);
        var extensions = await client.GetExtensionsAsync(timeout.Token);
        var version = await client.GetFirmwareVersionAsync(timeout.Token);
        if (!info.Persistent || (info.Flags & 4) != 0 || !info.SupportsEffects || !info.SupportsTiming
            || !info.SupportsColorSequences || !info.SupportsRuntimeBrightness || !info.SupportsStandby
            || !extensions.SupportsLightingTest || !IsExpectedRuntimeVersion(version, target.DeviceId))
            return new(false, L.T("Die erwartete Firmware-Version wurde noch nicht bestätigt."));
        if (!_closed && ReferenceEquals(target, _reconnectTarget)) _normalVersion = (target.RunId, version!);
        return normal;
    }
    internal static bool IsExpectedRuntimeVersion(OnboardFirmwareVersion? version, string deviceId) =>
        version is not null && version.DeviceId == deviceId && version.Version == FirmwareRelease.CurrentVersion && version.BuildId == FirmwareRelease.CurrentBuildId;

    private bool CanObserveReconnect => !_closed && !_busy && !_preparing && !_advancing && _executionAttempted && _firmwareVerified
        && _step == Step.Reconnect && _coreRunning == false && _reconnectTarget is not null
        && _reconnectTarget.DeviceId == _deviceId && _reconnectTarget.Operation == Operation;

    private void StartReconnectObservation(FirmwareResult result)
    {
        StopReconnectObservation();
        _completedTarget = null;
        _normalVersion = null;
        ConfirmedRuntimeVersion = null;
        _firmwareVerified = result.Verified;
        if (!result.Verified || result.ReconnectTarget is not { } target || target.DeviceId != _deviceId || target.Operation != Operation) return;
        _reconnectTarget = target;
        if (!_offlinePreview) { _coreTimer.Start(); _reconnectTimer.Start(); }
    }

    private void StopReconnectObservation()
    {
        _reconnectTimer.Stop(); _reconnectTarget = null; _firstReconnectedAt = null;
    }

    private async Task ObserveReconnectAsync()
    {
        if (_offlinePreview || _closed || _checkingReconnect) return;
        if (!CanObserveReconnect) { _firstReconnectedAt = null; return; }
        _checkingReconnect = true;
        var target = _reconnectTarget!; var revision = _availabilityRevision;
        bool IsCurrent() => CanObserveReconnect && revision == _availabilityRevision && ReferenceEquals(target, _reconnectTarget);
        try
        {
            await RefreshCoreStatusAsync(advanceWhenClosed: false);
            if (!IsCurrent()) return;
            var normal = await _readNormalConnection(target);
            if (!IsCurrent()) return;
            // CORE may have started while Windows was looking for the device.
            await RefreshCoreStatusAsync(advanceWhenClosed: false);
            if (!IsCurrent()) return;
            if (!normal.Available)
            {
                ApplyReconnectState(false, Environment.TickCount64);
                StatusText.Text = FirmwareMessages.ForUser(normal.Message);
                AppendServiceLog(StatusText.Text);
                return;
            }
            if (ApplyReconnectState(normal.Available, Environment.TickCount64))
            {
                _completedTarget = target;
                StopReconnectObservation(); _coreTimer.Stop(); SetStep(Step.Complete);
                StatusText.Text = L.T("Die Tastatur ist wieder verbunden. Prüfe jetzt Tasten und Beleuchtung.");
                if (_normalVersion is { } runtime && runtime.RunId == target.RunId)
                {
                    if (!string.IsNullOrWhiteSpace(normal.NormalInstanceId))
                        ConfirmedRuntimeVersion = new(runtime.Version, normal.NormalInstanceId, DateTimeOffset.UtcNow);
                    var message = L.Format("Installierte Firmware bestätigt: {0}.", "INGLORIOUS " + runtime.Version.Version);
                    StatusText.Text = message + Environment.NewLine + StatusText.Text;
                    AppendServiceLog(message);
                }
                AppendServiceLog(L.T("Die Tastatur ist wieder verbunden."));
                if (IsActive && Operation != FirmwareOperation.RestoreOriginal) LightingTestButton.Focus();
            }
        }
        catch
        {
            if (IsCurrent())
            {
                ApplyReconnectState(false, Environment.TickCount64);
                StatusText.Text = L.T("Die USB-Verbindung konnte nicht geprüft werden. Die App versucht es erneut.");
                AppendServiceLog(StatusText.Text);
            }
        }
        finally
        {
            if (!IsCurrent() && ReferenceEquals(target, _reconnectTarget)) _firstReconnectedAt = null;
            _checkingReconnect = false;
        }
    }

    private bool ApplyReconnectState(bool connected, long now)
    {
        if (!connected)
        {
            _firstReconnectedAt = null;
            StatusText.Text = L.T("Nach dem Wiederanschließen geht es automatisch weiter.");
            return false;
        }
        if (_firstReconnectedAt is null) { _firstReconnectedAt = now; return false; }
        return now - _firstReconnectedAt.Value >= 750;
    }

    private void OpenEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = _evidenceDirectory ?? _service?.DiagnosticDirectory;
        if (_offlinePreview || _busy || string.IsNullOrWhiteSpace(directory)) return;
        try { Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true }); }
        catch (Exception ex) { AppendLog(ex.Message); LogExpander.IsExpanded = true; }
    }

    private bool CanFinishFromTyping => !_offlinePreview && !_closed && !_busy && !_lightingTestRunning
        && _firmwareVerified && _executionAttempted && _step == Step.Complete
        && string.Equals(TypingTest?.Text.Trim(), _deviceId == "numpad" ? "1234567890" : "INGLORIOUS", StringComparison.OrdinalIgnoreCase);

    private void TypingTest_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (CanFinishFromTyping) Close();
    }

    private async void LightingTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_offlinePreview || _closed || _busy || _lightingTestRunning || !_firmwareVerified || _step != Step.Complete
            || _completedTarget is not { } target || Operation == FirmwareOperation.RestoreOriginal) return;
        _lightingTestRunning = true;
        _lightingTestCancellation = new CancellationTokenSource();
        LightingTestStatus.Text = L.T(_deviceId == "numpad"
            ? "Alle LEDs leuchten weiß, dann die Ziffern 1 bis 0 nacheinander. Rote Tropfen fallen von oben und füllen die Beleuchtung von unten auf. Zum Schluss leuchten alle LEDs rot, dann kehrt die normale Beleuchtung zurück."
            : "Alle LEDs leuchten weiß, dann die Buchstaben von INGLORIOUS nacheinander. Rote Tropfen fallen von oben und füllen die Beleuchtung von unten auf. Zum Schluss leuchten alle LEDs rot, dann kehrt die normale Beleuchtung zurück.");
        RenderStep();
        var owner = Owner as MainWindow;
        var resumeSharedBrightness = false;
        try
        {
            if (owner is not null) resumeSharedBrightness = await owner.PauseSharedBrightnessForLightingTestAsync();
            _lightingTestCancellation.Token.ThrowIfCancellationRequested();
            if (await ReadCoreStatusAsync() != false)
            {
                LightingTestStatus.Text = L.T("Glorious CORE schließen, bevor der Beleuchtungstest gestartet wird.");
                return;
            }
            var normal = await _service.CheckNormalConnectionAsync(target);
            if (_closed || _step != Step.Complete || !ReferenceEquals(target, _completedTarget)) return;
            if (!normal.Available || string.IsNullOrWhiteSpace(normal.NormalInstanceId)) throw new InvalidOperationException(normal.Message);
            await new FirmwareLightingTestService().BlinkAsync(_deviceId, normal.NormalInstanceId, ReadCoreStatusAsync, _lightingTestCancellation.Token);
            if (!_closed) LightingTestStatus.Text = L.T("Prüfe, ob die Tasten und Seitenleisten aufgeleuchtet haben.");
        }
        catch (OperationCanceledException)
        {
            if (!_closed) LightingTestStatus.Text = L.T("Der Beleuchtungstest wurde beendet.");
        }
        catch (Exception ex)
        {
            _service.RecordLightingTestFailure(ex);
            var message = ex is FirmwareLightingTestException lightingError
                ? lightingError.UserMessage
                : L.T("Der Beleuchtungstest konnte nicht abgeschlossen werden. Prüfe die USB-Verbindung.");
            AppendServiceLog(message);
            if (!_closed) LightingTestStatus.Text = message;
        }
        finally
        {
            _lightingTestCancellation.Dispose(); _lightingTestCancellation = null;
            _lightingTestRunning = false;
            owner?.ResumeSharedBrightnessAfterLightingTest(resumeSharedBrightness);
            if (!_closed) RenderStep();
            if (CanFinishFromTyping) Close();
        }
    }
}
