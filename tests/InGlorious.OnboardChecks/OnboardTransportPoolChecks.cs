using InGlorious.Core;

static class OnboardTransportPoolChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        static string Path() => "synthetic-endpoint-" + Guid.NewGuid().ToString("N");
        static byte[] Request(byte sequence) => OnboardProtocol.Packet(OnboardProtocol.Info, sequence, []);
        static async Task Reject(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { return; }
            throw new Exception("Expected transport failure.");
        }
        var path = Path(); var opens = 0; var transport = new Probe();
        var a = OnboardTransportPool.Acquire(path, "board", () => { opens++; return transport; });
        var b = OnboardTransportPool.Acquire(path, "board", () => { opens++; return new Probe(); });
        var requests = Enumerable.Range(1, 40).Select(async index =>
        {
            var response = await (index % 2 == 0 ? a : b).ExchangeAsync(Request((byte)index), default);
            if (response[4] != index) throw new Exception("A concurrent request received another caller's response.");
        });
        await Task.WhenAll(requests);
        check(opens == 1 && transport.Calls == 40 && transport.MaxActive == 1, "Concurrent clients share one stream and exchange complete request/response pairs serially");
        await Reject(() => Task.Run(() => OnboardTransportPool.Acquire(path, "different board", () => new Probe())));
        check(opens == 1, "Different identity cannot reuse an active channel");
        await a.DisposeAsync(); check(transport.Disposals == 0, "Disposing one client keeps the other client's stream open");
        await b.DisposeAsync(); await b.DisposeAsync(); check(transport.Disposals == 1, "Last lease closes exactly once");
        await Reject(() => a.ExchangeAsync(Request(1), default));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new Probe { BeforeReply = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        path = Path();
        a = OnboardTransportPool.Acquire(path, "board", () => blocking);
        b = OnboardTransportPool.Acquire(path, "board", () => throw new Exception("Duplicate stream"));
        var first = a.ExchangeAsync(Request(2), default); await entered.Task;
        using (var cancellation = new CancellationTokenSource())
        {
            var queued = b.ExchangeAsync(Request(3), cancellation.Token); cancellation.Cancel(); await Reject(() => queued);
        }
        check(blocking.Calls == 1 && blocking.Disposals == 0, "Cancellation while queued does not poison or interrupt the active request");
        await a.DisposeAsync(); await b.DisposeAsync();
        check(blocking.Disposals == 0, "In-flight request retains its stream after clients are disposed");
        release.SetResult(); await first;
        check(blocking.Disposals == 1, "Finishing the final in-flight request releases its stream");

        foreach (var fail in new[] { "io", "cancel", "envelope" })
        {
            path = Path(); var broken = new Probe { Failure = fail };
            a = OnboardTransportPool.Acquire(path, "board", () => broken);
            b = OnboardTransportPool.Acquire(path, "board", () => throw new Exception("Duplicate stream"));
            await Reject(() => a.ExchangeAsync(Request(4), default));
            await Reject(() => b.ExchangeAsync(Request(5), default));
            check(broken.Disposals == 1 && broken.Calls == 1, "Failed " + fail + " exchange poisons all existing leases without retrying");
            var recovered = new Probe();
            await using (var fresh = OnboardTransportPool.Acquire(path, "board", () => recovered))
                await fresh.ExchangeAsync(Request(6), default);
            check(recovered.Calls == 1 && recovered.Disposals == 1, "Explicit fresh open recovers after " + fail + " without replaying the failed request");
            await a.DisposeAsync(); await b.DisposeAsync();
        }
        using (var lateCancellation = new CancellationTokenSource())
        {
            path = Path(); var lateReply = new Probe { BeforeReply = _ => { lateCancellation.Cancel(); return Task.CompletedTask; } };
            await using var lease = OnboardTransportPool.Acquire(path, "board", () => lateReply);
            await Reject(() => lease.ExchangeAsync(Request(9), lateCancellation.Token));
            await Reject(() => lease.ExchangeAsync(Request(10), default));
            check(lateReply.Calls == 1 && lateReply.Disposals == 1, "Cancellation arriving with a complete response closes the channel before another request can reuse it");
        }
        path = Path(); var errorReply = new Probe { Failure = "device error" };
        await using (var lease = OnboardTransportPool.Acquire(path, "board", () => errorReply))
        {
            await lease.ExchangeAsync(Request(7), default); await lease.ExchangeAsync(Request(8), default);
            check(errorReply.Disposals == 0 && errorReply.Calls == 2, "Complete device-error replies do not poison the queue");
        }
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowClose = new Probe { BeforeDispose = async () => { disposalEntered.SetResult(); await closing.Task; } };
        path = Path(); a = OnboardTransportPool.Acquire(path, "board", () => slowClose);
        var close = a.DisposeAsync().AsTask(); await disposalEntered.Task;
        var attemptedOpen = false;
        await Reject(() => Task.Run(() => OnboardTransportPool.Acquire(path, "board", () => { attemptedOpen = true; return new Probe(); })));
        check(!attemptedOpen, "A new native stream cannot overlap a previous stream that is still closing");
        closing.SetResult(); await close;
        await using (var reopened = OnboardTransportPool.Acquire(path, "board", () => new Probe()))
            await reopened.ExchangeAsync(Request(9), default);
    }
    sealed class Probe : IOnboardTransport
    {
        public int Calls, Disposals, MaxActive;
        private int _active;
        public string? Failure { get; init; }
        public Func<CancellationToken, Task>? BeforeReply { get; init; }
        public Func<Task>? BeforeDispose { get; init; }
        public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
        {
            Calls++; var active = Interlocked.Increment(ref _active); MaxActive = Math.Max(MaxActive, active);
            try
            {
                if (BeforeReply is not null) await BeforeReply(ct); else await Task.Delay(1, ct);
                if (Failure == "io") throw new IOException("Synthetic interrupted response");
                if (Failure == "cancel") throw new OperationCanceledException("Synthetic active cancellation");
                var response = packet.ToArray();
                if (Failure == "envelope") response[4]++;
                if (Failure == "device error") response[5] = 9;
                return response;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public async ValueTask DisposeAsync() { Disposals++; if (BeforeDispose is not null) await BeforeDispose(); }
    }
}
