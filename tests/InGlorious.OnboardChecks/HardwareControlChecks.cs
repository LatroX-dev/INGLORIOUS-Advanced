using InGlorious.Core;

static class HardwareControlChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException)
            { check(true, label); return; }
            throw new Exception(label);
        }
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "gmmk3-65-ansi", "gmmk3-65-iso", "gmmk3-75-ansi", "gmmk3-75-iso",
            "gmmk3-100-ansi", "gmmk3-100-iso", "gmmk3-pro-100-iso"
        };
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            check(HardwareControlCatalog.HasOsSwitch(board.DeviceId) == expected.Contains(board.DeviceId),
                board.DeviceId + ": hardware controls are declared per exact target");
            var transport = new SwitchTransport(board.DeviceId);
            await using var client = new OnboardClient(transport, board.DeviceId);
            if (!expected.Contains(board.DeviceId))
            {
                check(await client.GetHardwareControlStatusAsync() is null && transport.Commands.Count == 0,
                    board.DeviceId + ": no status queries for a board without an OS switch");
                await Reject(() => client.GetExtensionsAsync(), "A nonexistent OS switch capability is rejected");
                continue;
            }
            foreach (var (position, active, pending) in new[] { (0, 0, 0), (1, 1, 0), (1, 0, 1), (0, 1, 1) })
            {
                transport.Payload = [1, board.BoardId, 1, (byte)position, (byte)active, (byte)pending, 0, 0];
                var state = await client.GetHardwareControlStatusAsync();
                check(state is not null && state.DeviceId == board.DeviceId && state.MacPosition == (position == 1) &&
                    state.MacActive == (active == 1) && state.Pending == (pending == 1),
                    board.DeviceId + ": actual and pending switch states remain distinct");
            }
            check(transport.Commands.Count(command => command == 13) == 1 && transport.Commands.Count(command => command == 11) == 1 &&
                transport.Commands.All(command => command is 11 or 13 or 17), "Hardware switch reads cache support on one connection and never write");
            foreach (var offset in Enumerable.Range(0, 8))
            {
                transport.Payload = [1, board.BoardId, 1, 0, 0, 0, 0, 0]; transport.Payload[offset] = 255;
                await Reject(() => client.GetHardwareControlStatusAsync(), "Invalid hardware switch field is rejected at " + offset);
            }
            transport.Payload = [1, board.BoardId, 1, 1, 0, 0, 0, 0];
            await Reject(() => client.GetHardwareControlStatusAsync(), "Different position and active mode require pending status");
            foreach (var length in new[] { 0, 1, 7, 9 })
            {
                transport.Payload = new byte[length];
                await Reject(() => client.GetHardwareControlStatusAsync(), "Incomplete or oversized hardware switch reply is rejected");
            }
            transport.Payload = [1, board.BoardId, 1, 1, 1, 0, 0, 0];
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); var before = transport.Commands.Count;
            await Reject(() => client.GetHardwareControlStatusAsync(canceled.Token), "Canceled hardware switch read is not sent");
            check(transport.Commands.Count == before && await client.GetHardwareControlStatusAsync() is not null,
                "Canceled hardware switch read releases its client gate");
            foreach (var (patch, flags) in new[] { (8, 128), (9, 0) })
            {
                var old = new SwitchTransport(board.DeviceId) { Patch = (ushort)patch, Flags = (ushort)flags };
                await using var oldClient = new OnboardClient(old, board.DeviceId);
                check(await oldClient.GetHardwareControlStatusAsync() is null && !old.Commands.Contains(17),
                    "Older firmware or missing hardware switch capability is a supported absence");
            }
        }
        check(!HardwareControlCatalog.HasOsSwitch("gmmk3-pro-75-ansi") && !HardwareControlCatalog.HasOsSwitch("unknown"),
            "A family name never grants unverified hardware capabilities");
        try { OnboardProtocol.Packet(OnboardProtocol.ReadHardwareControls, 1, [1]); throw new Exception("Unexpected hardware control command payload"); }
        catch (ArgumentOutOfRangeException) { check(true, "Hardware switch status cannot contain write data or pin addresses"); }
    }

    sealed class SwitchTransport(string id) : IOnboardTransport
    {
        public List<byte> Commands { get; } = [];
        public ushort Patch { get; init; } = 9;
        public ushort Flags { get; init; } = 128;
        public byte[] Payload { get; set; } = [1, BoardDescriptorRegistry.Get(id).BoardId, 1, 0, 0, 0, 0, 0];
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); OnboardProtocol.ValidateRequest(packet); Commands.Add(packet[3]);
            byte[] data;
            if (packet[3] == OnboardProtocol.GetFirmwareVersion)
            {
                data = new byte[16]; data[0] = 1; data[2] = BoardDescriptorRegistry.Get(id).BoardId;
                data[6] = 11; OnboardSerializer.Put16(data, 8, Patch); data[12] = 1;
            }
            else if (packet[3] == OnboardProtocol.GetExtensions)
            { data = new byte[8]; data[0] = 1; OnboardSerializer.Put16(data, 2, Flags); }
            else if (packet[3] == OnboardProtocol.Info)
            {
                var shape = OnboardSerializer.Shape(id);
                data = new byte[24]; data[0] = shape.Board; data[1] = (byte)shape.Rows; data[2] = (byte)shape.Cols;
                data[3] = 4; data[4] = (byte)shape.Leds; data[5] = 32; data[6] = 128;
                data[7] = (byte)(BoardDescriptorRegistry.Get(id).HasSlider ? 3 : 1);
                OnboardSerializer.Put16(data, 8, shape.Size); OnboardSerializer.Put16(data, 10, 768); OnboardSerializer.Put16(data, 22, 1);
            }
            else if (packet[3] == OnboardProtocol.ReadHardwareControls) data = Payload;
            else throw new Exception("Unexpected non-read-only switch command");
            var response = packet.ToArray(); response[6] = checked((byte)data.Length); data.CopyTo(response, 8);
            return Task.FromResult(response);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
