using System.Diagnostics;
using InGlorious.Core;

namespace InGlorious.DeviceDiagnostics;

internal sealed record TransportTimingRecord(int Index, string Command, double ElapsedMs, bool Success);
internal sealed record CommandTimingAggregate(string Command, int Count, int Succeeded, int Failed, double TotalMs, double AverageMs, double MinimumMs, double MaximumMs);

/// <summary>Measures exchange completion only. Payloads, cancellation and disposal pass through unchanged.</summary>
internal sealed class TimedTransport(IOnboardTransport inner) : IOnboardTransport
{
    private readonly List<TransportTimingRecord> _records = [];
    private readonly object _gate = new();

    public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var command = packet.Length > 3 ? packet[3] switch
        {
            OnboardProtocol.Info => "INFO",
            OnboardProtocol.Read => "READ_ACTIVE",
            OnboardProtocol.Write => "WRITE_STAGE",
            OnboardProtocol.Begin => "BEGIN",
            OnboardProtocol.Commit => "COMMIT",
            OnboardProtocol.Abort => "ABORT",
            OnboardProtocol.GetFirmwareVersion => "FIRMWARE_VERSION",
            OnboardProtocol.GetExtensions => "EXTENSIONS",
            _ => "UNKNOWN"
        } : "UNKNOWN";
        var started = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
            var response = await inner.ExchangeAsync(packet, cancellationToken).ConfigureAwait(false);
            success = true;
            return response;
        }
        finally
        {
            var elapsedMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);
            lock (_gate) _records.Add(new(_records.Count + 1, command, elapsedMs, success));
        }
    }

    public IReadOnlyList<TransportTimingRecord> Records { get { lock (_gate) return _records.ToArray(); } }
    public double? CommitDurationMs => Records.LastOrDefault(r => r.Command == "COMMIT")?.ElapsedMs;
    public IReadOnlyList<CommandTimingAggregate> CommandTimingAggregate => Records.GroupBy(r => r.Command).Select(group =>
        new CommandTimingAggregate(group.Key, group.Count(), group.Count(r => r.Success), group.Count(r => !r.Success),
            Math.Round(group.Sum(r => r.ElapsedMs), 3), Math.Round(group.Average(r => r.ElapsedMs), 3),
            group.Min(r => r.ElapsedMs), group.Max(r => r.ElapsedMs))).ToArray();

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
