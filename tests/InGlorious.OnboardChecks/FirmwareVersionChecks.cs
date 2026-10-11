using System.Buffers.Binary;
using InGlorious.Core;

internal static class FirmwareVersionChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or TimeoutException or OperationCanceledException) { check(true, label); return; }
            throw new Exception(label);
        }
        check(FirmwareRelease.CurrentVersion.ToString() == "0.9.2" && FirmwareRelease.CurrentBuildId == 0x00090201, "central current release/build");
        foreach (var (left, right) in new[] { (new FirmwareSemanticVersion(0, 7, 99), new FirmwareSemanticVersion(0, 8, 0)), (new(0, 8, 0), new FirmwareSemanticVersion(0, 8, 1)), (new(0, 9, 9), new FirmwareSemanticVersion(0, 10, 0)), (new(0, 65535, 65535), new FirmwareSemanticVersion(1, 0, 0)) })
            check(left < right && right > left && left != right && left.CompareTo(left) == 0, "numeric semver compares major, minor and patch without string ordering");
        foreach (var value in new string?[] { null, "", "0", "0.8.x", "0.8.0.1", "-1.8.0", "0.65536.0", " 0.8.0", "0.8.0 " })
            check(!FirmwareSemanticVersion.TryParse(value, out _), "invalid semantic-version text is rejected");
        check(FirmwareSemanticVersion.TryParse("0.7", out var prior) && prior == new FirmwareSemanticVersion(0, 7, 0), "verified previous release labels parse without inventing runtime versions");
        foreach (var id in BoardDescriptorRegistry.ProtocolBoards.Select(board => board.DeviceId))
        {
            var transport = new VersionTransport(id);
            await using (var client = new OnboardClient(transport, id))
            {
                var report = await client.GetFirmwareVersionAsync();
                check(report == new OnboardFirmwareVersion(id, FirmwareRelease.CurrentVersion, FirmwareRelease.CurrentBuildId), id + ": exact RAW13 fields decoded");
                check(transport.Calls == 1 && transport.LastPacket is { } packet && packet[3] == 13 && packet[6] == 0 && packet.AsSpan(8).IndexOfAnyExcept((byte)0) < 0,
                    id + ": one empty read-only request, no configuration or flash command");
                transport.Status = 9;
                check(await client.GetFirmwareVersionAsync() is null, id + ": explicit unsupported command is the only version-unavailable result");
                transport.Status = 0;
                transport.Payload = VersionTransport.Data(id, new(0, 9, 3), 0x00090301);
                var future = await client.GetFirmwareVersionAsync();
                check(future!.Version > FirmwareRelease.CurrentVersion && future.BuildId == 0x00090301, id + ": newer patch remains visible and must not trigger downgrade");
                foreach (var status in new byte[] { 1, 3, 7, 8, 255 })
                {
                    transport.Status = status;
                    await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": non-unsupported status remains a failure");
                }
                transport.Status = 0;
                foreach (var length in new[] { 0, 8, 15, 17, 24 })
                {
                    transport.Payload = new byte[length];
                    await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": non-exact RAW13 response length rejected");
                }
                foreach (var (offset, value) in new[] { (0, 0), (0, 2), (1, 1), (2, id == "pro-iso" ? 2 : 1), (3, 1), (10, 1), (11, 1) })
                {
                    transport.Payload = VersionTransport.Data(id, FirmwareRelease.CurrentVersion, FirmwareRelease.CurrentBuildId);
                    transport.Payload[offset] = (byte)value;
                    await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": malformed format, board, flags or reserved bytes stay unverified");
                }
                transport.Payload = VersionTransport.Data(id, FirmwareRelease.CurrentVersion, FirmwareRelease.CurrentBuildId);
                transport.Payload.AsSpan(12, 4).Clear();
                await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": zero build identifier remains unverified");
                transport.Payload = VersionTransport.Data(id, FirmwareRelease.CurrentVersion, FirmwareRelease.CurrentBuildId);
                transport.Status = 9; transport.WrongSequence = true;
                await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": malformed unsupported reply is not treated as unavailable");
                transport.WrongSequence = false; transport.Status = 0;
                transport.Failure = new TimeoutException("Synthetic timeout");
                await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": timeout never becomes an unavailable/old version");
                transport.Failure = new IOException("Synthetic I/O failure");
                await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": transport failure remains unverified");
                transport.Failure = null;
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                var before = transport.Calls;
                await Reject(async () => _ = await client.GetFirmwareVersionAsync(canceled.Token), id + ": cancellation propagates");
                check(transport.Calls == before, id + ": canceled version request invokes no transport");
            }
            check(transport.Disposals == 1, id + ": version reader owns and disposes its transport exactly once");
        }
        try { _ = OnboardProtocol.Packet(OnboardProtocol.GetFirmwareVersion, 0, [1]); throw new Exception("RAW13 accepted payload"); }
        catch (ArgumentOutOfRangeException) { check(true, "RAW13 request must be empty"); }
    }
    private sealed class VersionTransport(string id) : IOnboardTransport
    {
        public byte[] Payload = Data(id, FirmwareRelease.CurrentVersion, FirmwareRelease.CurrentBuildId);
        public int Calls, Disposals;
        public byte Status;
        public bool WrongSequence;
        public Exception? Failure;
        public byte[]? LastPacket;
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
        {
            ++Calls; LastPacket = packet.ToArray(); ct.ThrowIfCancellationRequested();
            if (packet[3] != OnboardProtocol.GetFirmwareVersion) throw new InvalidOperationException("Unexpected non-version command.");
            if (Failure is not null) return Task.FromException<byte[]>(Failure);
            var response = OnboardProtocol.Packet(OnboardProtocol.Info, (byte)(packet[4] + (WrongSequence ? 1 : 0)), Payload);
            response[3] = OnboardProtocol.GetFirmwareVersion; response[5] = Status;
            return Task.FromResult(response);
        }
        public ValueTask DisposeAsync() { ++Disposals; return ValueTask.CompletedTask; }
        public static byte[] Data(string id, FirmwareSemanticVersion version, uint build)
        {
            var result = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(result, 1);
            result[2] = OnboardSerializer.Shape(id).Board;
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), version.Major);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), version.Minor);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), version.Patch);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), build);
            return result;
        }
    }
}
