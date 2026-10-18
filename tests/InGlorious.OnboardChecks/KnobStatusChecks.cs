using InGlorious.Core;

static class KnobStatusChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
            { check(true, label); return; }
            throw new Exception(label);
        }
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var transport = new StatusTransport(board.DeviceId);
            await using var client = new OnboardClient(transport, board.DeviceId);
            if (!DeviceCatalog.Get(board.DeviceId).HasEncoder)
            {
                check(await client.GetKnobStatusAsync() is null && transport.Commands.Count == 0, "Encoderless status does not send unsupported commands");
                continue;
            }
            foreach (var preset in KnobPresets.All)
            {
                transport.Payload = Payload(preset);
                var status = await client.GetKnobStatusAsync();
                check(status is { Layer: 3, CycleEnabled: true, SignalActive: false, ChangeCounter: 11, Generation: 7, ActiveCrc: 123,
                    CounterClockwiseLayer: 0, ClockwiseLayer: 2 } && status.PresetId == preset.Id && status.SourceLabel == preset.SourceLabel,
                    board.DeviceId + ": actual resolved status reports " + preset.Id + " independently from editor configuration");
            }
            check(transport.Commands.Count(command => command == 13) == 1 && transport.Commands.Count(command => command == 11) == 1 &&
                transport.Commands.All(command => command is 11 or 13 or 16), "Repeated status requests cache support on the same connection and never write");
            transport.Payload = Payload(null);
            check(await client.GetKnobStatusAsync() is { PresetId: null, ActivePresetBit: 0, SourceLabel: "Eigene Belegung" }, "Mixed manual directions remain individual");
            foreach (var offset in new[] { 0, 1, 2, 3, 20, 21, 22, 23 })
            {
                transport.Payload = Payload(KnobPresets.All[0]); transport.Payload[offset] = 255;
                await Reject(() => client.GetKnobStatusAsync(), "Invalid status field is rejected at offset " + offset);
            }
            transport.Payload = Payload(KnobPresets.All[2]); transport.Payload[22] = 2;
            await Reject(() => client.GetKnobStatusAsync(), "Non-scalable preset cannot claim multiple steps");
            foreach (var length in new[] { 0, 1, 23 })
            {
                transport.Payload = new byte[length];
                await Reject(() => client.GetKnobStatusAsync(), "Incomplete status response is rejected");
            }
            transport.Payload = Payload(KnobPresets.All[0]);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var count = transport.Commands.Count;
            await Reject(() => client.GetKnobStatusAsync(cancelled.Token), "Cancelled status poll is not sent");
            check(transport.Commands.Count == count && await client.GetKnobStatusAsync() is not null, "Cancellation preserves client lock reuse");
            foreach (var (patch, flags) in new[] { (7, 59), (8, 27), (8, 3) })
            {
                var old = new StatusTransport(board.DeviceId) { Patch = (ushort)patch, Flags = (ushort)flags };
                await using var oldClient = new OnboardClient(old, board.DeviceId);
                check(await oldClient.GetKnobStatusAsync() is null && !old.Commands.Contains(16), "Older or missing capability returns unsupported without guessing");
            }
            foreach (var flags in new ushort[] { 32, 40, 64, 65535 })
            {
                var bad = new StatusTransport(board.DeviceId) { Flags = flags };
                await using var badClient = new OnboardClient(bad, board.DeviceId);
                await Reject(() => badClient.GetKnobStatusAsync(), "Malformed extension dependencies reject status lookup");
                check(!bad.Commands.Contains(16), "Malformed capabilities never send status command");
            }
        }
        check(OnboardProtocol.Packet(16, 31, []).AsSpan(6).IndexOfAnyExcept((byte)0) < 0, "Status request is exactly empty");
        try { _ = OnboardProtocol.Packet(16, 0, [0]); throw new Exception("Nonempty status request accepted"); }
        catch (ArgumentOutOfRangeException) { check(true, "Status cannot contain actions or configuration bytes"); }
    }
    static byte[] Payload(KnobPreset? preset)
    {
        var result = new byte[24]; result[0] = 1; result[1] = 3; result[3] = 1; result[21] = 2; result[22] = 1;
        result[2] = (byte)(preset?.Id switch { "volume" => 1, "brightness" => 2, "tracks" => 4, "scroll" => 8,
            "zoom" or "zoom-mac" => 16, "hscroll" => 32, "tabs" => 64, "undo" or "undo-mac" => 128, _ => 0 });
        OnboardSerializer.Put16(result, 4, preset is null ? 4 : OnboardSerializer.EncodeBinding(preset.CounterClockwise, []));
        OnboardSerializer.Put16(result, 6, preset is null ? 5 : OnboardSerializer.EncodeBinding(preset.Clockwise, []));
        result[8] = 11; result[12] = 7; result[16] = 123; return result;
    }
    sealed class StatusTransport(string id) : IOnboardTransport
    {
        public List<byte> Commands { get; } = [];
        public ushort Patch { get; init; } = 8;
        public ushort Flags { get; init; } = 59;
        public byte[] Payload { get; set; } = KnobStatusChecks.Payload(KnobPresets.All[0]);
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); OnboardProtocol.ValidateRequest(packet); Commands.Add(packet[3]);
            byte[] data;
            if (packet[3] == 13)
            {
                data = new byte[16]; data[0] = 1; data[2] = BoardDescriptorRegistry.Get(id).BoardId;
                data[6] = 11; OnboardSerializer.Put16(data, 8, Patch); data[12] = 1;
            }
            else if (packet[3] == 11)
            { data = new byte[8]; data[0] = 1; OnboardSerializer.Put16(data, 2, Flags); OnboardSerializer.Put16(data, 4, 21000); }
            else if (packet[3] == 16) data = Payload;
            else throw new Exception("Unexpected non-read-only status command");
            var response = packet.ToArray(); response[6] = checked((byte)data.Length); data.CopyTo(response, 8);
            return Task.FromResult(response);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
