using System.IO;
using System.Runtime.ExceptionServices;

namespace InGlorious.Core;

public enum FirmwareLightingTestFailure { FirmwareUpdateRequired, CoreRunning, DeviceUnavailable, DeviceBusy, Interrupted, ConnectionLost, CleanupUnconfirmed, ConfigurationChanged }
public sealed class FirmwareLightingTestException(FirmwareLightingTestFailure reason, string userMessage, Exception? inner = null) : IOException(userMessage, inner)
{
    public FirmwareLightingTestFailure Reason { get; } = reason;
    public string UserMessage => Message;
}

/// <summary>Runs an explicit autonomous firmware overlay. No persistent upload, runtime dimmer change, flash, or USB reconnect.</summary>
public sealed class FirmwareLightingTestService
{
    private readonly Func<IReadOnlyList<OnboardDevice>> _discover;
    private readonly Func<OnboardDevice, OnboardClient> _open;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<long> _milliseconds;
    private int _running;

    public FirmwareLightingTestService() : this(OnboardClient.FindDevices, OnboardClient.Open, Task.Delay) { }
    public FirmwareLightingTestService(Func<IReadOnlyList<OnboardDevice>> discover, Func<OnboardDevice, OnboardClient> open, Func<TimeSpan, CancellationToken, Task> delay, Func<long>? milliseconds = null)
    { _discover = discover ?? throw new ArgumentNullException(nameof(discover)); _open = open ?? throw new ArgumentNullException(nameof(open)); _delay = delay ?? throw new ArgumentNullException(nameof(delay)); _milliseconds = milliseconds ?? (() => Environment.TickCount64); }

    public async Task BlinkAsync(string deviceId, string usbInstanceId, Func<Task<bool?>> coreStatus, CancellationToken cancellationToken)
    {
        if (!BoardDescriptorRegistry.TryGet(deviceId, out var board)) throw new ArgumentException("Unbekanntes Gerätemodell.", nameof(deviceId));
        var pid = board.RuntimeProductId.ToString("X4");
        if (!OnboardHidTransport.IsPhysicalUsbRoot(usbInstanceId) || !usbInstanceId.StartsWith($@"USB\VID_FEED&PID_{pid}\", StringComparison.OrdinalIgnoreCase))
            throw Error(FirmwareLightingTestFailure.DeviceUnavailable, "Der Beleuchtungstest benötigt das eindeutig erkannte Gerät mit INGLORIOUS-Firmware.");
        ArgumentNullException.ThrowIfNull(coreStatus);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw Error(FirmwareLightingTestFailure.DeviceBusy, "Der Beleuchtungstest läuft bereits.");
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            var token = limit.Token;
            await RequireCoreClosed(coreStatus, token).ConfigureAwait(false);
            var candidates = _discover().Where(device => device.DeviceId == deviceId && string.Equals(device.UsbInstanceId, usbInstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1 || !OnboardHidTransport.IsAllowedPath(candidates[0].Path)) throw Error(FirmwareLightingTestFailure.DeviceUnavailable, "Das Gerät für den Beleuchtungstest ist nicht eindeutig verbunden.");
            var device = candidates[0];
            OnboardInfo? before = null;
            var startAttempted = false;
            Exception? failure = null;
            try
            {
                await using var client = _open(device);
                before = await client.GetInfoAsync(token).ConfigureAwait(false);
                ValidateInfo(before, deviceId);
                var extensions = await client.GetExtensionsAsync(token).ConfigureAwait(false);
                if (!extensions.SupportsLightingTest) throw Error(FirmwareLightingTestFailure.FirmwareUpdateRequired, "Für den Beleuchtungstest ist ein Firmware-Update erforderlich.");
                if ((await client.GetLightingTestStatusAsync(token).ConfigureAwait(false)).Active) throw Error(FirmwareLightingTestFailure.DeviceBusy, "Der Beleuchtungstest läuft bereits.");
                await RequireCoreClosed(coreStatus, token).ConfigureAwait(false);
                startAttempted = true;
                var requestedAt = _milliseconds();
                var durationMs = extensions.LightingTestDurationMs;
                // The advertised autonomous duration drives the deadline, with bounded I/O slack.
                // Preflight keeps its separate 30 s bound; cancellation never shortens cleanup.
                limit.CancelAfter(TimeSpan.FromMilliseconds(durationMs + 10000));
                var started = await client.StartLightingTestAsync(token).ConfigureAwait(false);
                if (started.RemainingMs > durationMs) throw new InvalidDataException("Lighting-test duration changed during startup.");
                // Response transit time must not turn an ordinary firmware timeout into a false early-abort report.
                var earliestEnd = requestedAt + started.RemainingMs;
                var latestEnd = requestedAt + durationMs + 5000;
                var maximumPolls = (durationMs + 249) / 250 + 20;
                for (var poll = 0; ; poll++)
                {
                    if (poll >= maximumPolls || _milliseconds() >= latestEnd) throw Error(FirmwareLightingTestFailure.ConnectionLost, "Der Beleuchtungstest wurde nicht rechtzeitig beendet. Bitte die USB-Verbindung prüfen.");
                    await _delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                    await RequireCoreClosed(coreStatus, token).ConfigureAwait(false);
                    var state = await client.GetLightingTestStatusAsync(token).ConfigureAwait(false);
                    if (state.Active) continue;
                    if (_milliseconds() + 75 < earliestEnd) throw Error(FirmwareLightingTestFailure.Interrupted, "Der Beleuchtungstest wurde vorzeitig beendet. Bitte erneut starten und währenddessen keine Tasten drücken.");
                    break;
                }
            }
            catch (Exception ex) { failure = Friendly(ex, cancellationToken); }
            finally
            {
                if (startAttempted)
                {
                    // Firmware expires its advertised autonomous overlay even if the host disappears. Explicit cleanup verifies idle.
                    // A fresh handle avoids queued responses after a cancelled read; cleanup ignores cancellation and CORE state.
                    try
                    {
                        using var stopLimit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                        await using var cleanup = _open(device);
                        await cleanup.StopLightingTestAsync(stopLimit.Token).ConfigureAwait(false);
                        if ((await cleanup.GetLightingTestStatusAsync(stopLimit.Token).ConfigureAwait(false)).Active) throw new IOException("Lighting overlay is still active.");
                        var after = await cleanup.GetInfoAsync(stopLimit.Token).ConfigureAwait(false);
                        ValidateInfo(after, deviceId);
                        if (before is null || after.Generation != before.Generation || after.ActiveCrc != before.ActiveCrc || after.BlobSize != before.BlobSize || after.SchemaVersion != before.SchemaVersion)
                            throw Error(FirmwareLightingTestFailure.ConfigurationChanged, "Die gespeicherte Konfiguration hat sich während des Beleuchtungstests geändert.");
                    }
                    catch (Exception stopError)
                    {
                        if (stopError is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.ConfigurationChanged }) throw;
                        throw Error(FirmwareLightingTestFailure.CleanupUnconfirmed, "Das Ende des Beleuchtungstests konnte nicht bestätigt werden. Der Test endet auf dem Gerät selbstständig.", failure is null ? stopError : new AggregateException(failure, stopError));
                    }
                }
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        catch (Exception ex) when (ex is not FirmwareLightingTestException and not ArgumentException &&
            !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        { throw Friendly(ex, cancellationToken); }
        finally { Volatile.Write(ref _running, 0); }
    }

    private static void ValidateInfo(OnboardInfo info, string deviceId)
    {
        if (info.DeviceId != deviceId) throw Error(FirmwareLightingTestFailure.DeviceUnavailable, "Das Gerät für den Beleuchtungstest ist nicht eindeutig verbunden.");
        if (!info.Persistent || (info.Flags & 4) != 0 || info.StagingBytes != 0) throw Error(FirmwareLightingTestFailure.DeviceBusy, "Der Gerätespeicher ist nicht bereit oder eine Übertragung läuft. Der Beleuchtungstest wird nicht gestartet.");
    }
    private static async Task RequireCoreClosed(Func<Task<bool?>> coreStatus, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool? running;
        try { running = await coreStatus().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw Error(FirmwareLightingTestFailure.CoreRunning, "Glorious CORE schließen, bevor der Beleuchtungstest gestartet wird.", ex); }
        if (running != false) throw Error(FirmwareLightingTestFailure.CoreRunning, "Glorious CORE schließen, bevor der Beleuchtungstest gestartet wird.");
    }
    private static Exception Friendly(Exception ex, CancellationToken callerCancellation) => ex switch
    {
        FirmwareLightingTestException => ex,
        // RAW requests and this service have their own deadlines. Only the caller's
        // cancellation means the user stopped the test; internal timeouts are failures.
        OperationCanceledException when callerCancellation.IsCancellationRequested => ex,
        NotSupportedException => Error(FirmwareLightingTestFailure.FirmwareUpdateRequired, "Für den Beleuchtungstest ist ein Firmware-Update erforderlich.", ex),
        OnboardCommandException { Status: 3 } => Error(FirmwareLightingTestFailure.DeviceBusy, "Das Gerät ist beschäftigt. Tasten loslassen und den Beleuchtungstest erneut starten.", ex),
        _ => Error(FirmwareLightingTestFailure.ConnectionLost, "Der Beleuchtungstest konnte nicht abgeschlossen werden. Bitte die USB-Verbindung prüfen.", ex)
    };
    private static FirmwareLightingTestException Error(FirmwareLightingTestFailure reason, string source, Exception? inner = null) => new(reason, L.T(source), inner);
}
