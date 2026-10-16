using System.IO;

namespace InGlorious.Core;

/// <summary>User-started, volatile device checks. No staging, commits or profile writes.</summary>
public sealed class DeviceDiagnosticSession : IAsyncDisposable
{
    private readonly OnboardClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public OnboardFirmwareVersion? Version { get; }
    private DeviceDiagnosticSession(OnboardClient client, OnboardFirmwareVersion? version) { _client = client; Version = version; }

    public static async Task<DeviceDiagnosticSession?> TryStartAsync(OnboardDevice device, CancellationToken token)
    {
        if (DeviceSetupService.IsGloriousCoreRunning() is not false)
            throw new IOException(L.T("Schließe Glorious CORE, bevor du fortfährst."));
        var client = OnboardClient.Open(device);
        try
        {
            var version = await client.GetFirmwareVersionAsync(token);
            // Unsupported diagnostics are a valid capability result, not a connection failure.
            // A local typing test must never send diagnostic commands to those devices.
            if (await client.TryStartDeviceTestAsync(token) is null)
            {
                await client.DisposeAsync();
                return null;
            }
            return new(client, version);
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public static async Task<DeviceDiagnosticSession> StartAsync(OnboardDevice device, CancellationToken token) =>
        await TryStartAsync(device, token) ?? throw new NotSupportedException(L.T("Für den vollständigen Gerätetest ist ein Firmware-Update erforderlich."));

    public async Task<OnboardDeviceTestStatus> PollAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Firmware action 3 renews only a live session and cannot silently restart an expired test.
            return await _client.DeviceTestAsync(3, token);
        }
        finally { _gate.Release(); }
    }

    public async Task SetLedAsync(int? led, byte red, byte green, byte blue, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _client.SetDeviceTestLedAsync(led.HasValue ? (byte)2 : (byte)1, checked((byte)(led ?? 0)), red, green, blue, token);
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreLightingAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { if (!_disposed) await _client.SetDeviceTestLedAsync(0, token: token); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _client.DeviceTestAsync(2, timeout.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
            finally { await _client.DisposeAsync(); }
        }
        finally { _gate.Release(); }
    }
}
