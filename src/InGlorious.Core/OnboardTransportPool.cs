using System.IO;

namespace InGlorious.Core;

// The pool can identify a closed/poisoned stream without relying on arbitrary exception text.
internal sealed class OnboardTransportResetException : IOException
{
    public OnboardTransportResetException() : base("The device connection is no longer available.") { }
}

/// <summary>One live RAW-HID stream per endpoint within this process; failed exchanges are never retried.</summary>
internal static class OnboardTransportPool
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Channel> Channels = new(StringComparer.Ordinal);

    public static IOnboardTransport Acquire(string path, string identity, Func<IOnboardTransport> open)
    {
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        lock (Sync)
        {
            if (Channels.TryGetValue(key, out var channel))
            {
                if (channel.Identity != identity) throw new IOException("The connected device identity changed.");
                if (!channel.Poisoned)
                {
                    channel.References++;
                    return new Lease(channel);
                }
                // Never open a second stream while the previous native handle is still closing.
                if (!channel.IsClosed) throw new OnboardTransportResetException();
            }
            var created = new Channel(key, identity, open());
            Channels[key] = created;
            return new Lease(created);
        }
    }
    private static void Retain(Channel channel)
    {
        lock (Sync)
        {
            if (channel.References == 0 || channel.Poisoned) throw new OnboardTransportResetException();
            channel.References++;
        }
    }
    private static async ValueTask Release(Channel channel)
    {
        bool close;
        lock (Sync)
        {
            close = --channel.References == 0;
            if (close) channel.Poisoned = true;
        }
        if (close)
        {
            await channel.Close().ConfigureAwait(false);
            lock (Sync) { if (Channels.GetValueOrDefault(channel.Key) == channel) Channels.Remove(channel.Key); }
        }
    }
    private sealed class Channel(string key, string identity, IOnboardTransport transport)
    {
        public readonly string Key = key, Identity = identity;
        public int References = 1;
        public volatile bool Poisoned;
        private int _closed;
        private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsClosed => _closeCompletion.Task.IsCompletedSuccessfully;
        private readonly SemaphoreSlim _exchange = new(1, 1);
        public async Task<byte[]> Exchange(byte[] packet, CancellationToken cancellation)
        {
            // Requests waiting behind another caller have not touched USB and do not poison its channel when cancelled.
            await _exchange.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (Poisoned) throw new OnboardTransportResetException();
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var response = await transport.ExchangeAsync(packet, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    // Validate the envelope before reusing the stream. A device error is a complete response,
                    // while an unrelated/truncated reply means the next client cannot safely reuse this queue.
                    try { _ = OnboardProtocol.ParseResponse(response, packet[3], packet[4]); }
                    catch (OnboardCommandException) { }
                    return response;
                }
                catch
                {
                    Poisoned = true;
                    try { await Close().ConfigureAwait(false); } catch { /* Preserve the exchange failure. */ }
                    throw;
                }
            }
            finally { _exchange.Release(); }
        }
        public async ValueTask Close()
        {
            Poisoned = true;
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                try { await transport.DisposeAsync().ConfigureAwait(false); _closeCompletion.TrySetResult(); }
                catch (Exception error) { _closeCompletion.TrySetException(error); throw; }
            }
            else await _closeCompletion.Task.ConfigureAwait(false);
        }
    }
    private sealed class Lease(Channel channel) : IOnboardTransport
    {
        private int _closed;
        public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            OnboardProtocol.ValidateRequest(packet);
            Retain(channel);
            try { return await channel.Exchange(packet.ToArray(), cancellationToken).ConfigureAwait(false); }
            finally { await Release(channel).ConfigureAwait(false); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) await Release(channel).ConfigureAwait(false);
        }
    }
}
