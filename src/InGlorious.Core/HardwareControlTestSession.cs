using System.IO;

namespace InGlorious.Core;

/// <summary>Explicitly started, read-only hardware-control test. No profile or firmware writes.</summary>
public sealed class HardwareControlTestSession : IAsyncDisposable
{
    private readonly OnboardClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public OnboardHardwareControlStatus Status { get; private set; }
    private HardwareControlTestSession(OnboardClient client, OnboardHardwareControlStatus status)
    { _client = client; Status = status; }

    public static async Task<HardwareControlTestSession?> TryStartAsync(OnboardDevice device, CancellationToken token)
    {
        if (!HardwareControlCatalog.HasOsSwitch(device.DeviceId)) return null;
        if (DeviceSetupService.IsGloriousCoreRunning() is not false)
            throw new IOException(L.T("Schließe Glorious CORE, bevor du fortfährst."));
        var client = OnboardClient.Open(device);
        try
        {
            var status = await client.GetHardwareControlStatusAsync(token);
            if (status is not null) return new(client, status);
            await client.DisposeAsync();
            return null;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<OnboardHardwareControlStatus> PollAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Status = await _client.GetHardwareControlStatusAsync(token)
                ?? throw new IOException("Hardware-control capability disappeared during the test.");
            return Status;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Wait for the real exchange and transport cleanup, not a detached cancellation wrapper.
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _client.DisposeAsync();
        }
        finally { _gate.Release(); }
    }
}
