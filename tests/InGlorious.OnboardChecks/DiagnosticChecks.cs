using System.Buffers.Binary;
using InGlorious.Core;

static class DiagnosticChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        const string id = "gmmk3-pro-100-iso";
        var transport = new DiagnosticTransport(id);
        await using var client = new OnboardClient(transport, id);
        var extensions = await client.GetExtensionsAsync();
        check(extensions.SupportsDeviceTest && extensions.SupportsScreensaver && extensions.SupportsLightingTest, "Device test is an independent advertised extension");
        var started = await client.DeviceTestAsync(1);
        check(started.Active && started.SeenRows.Length == 14 && started.RemainingMs == 10000, "Device test start validates exact geometry and watchdog");
        transport.Rows[0] = 0x81; transport.Rows[13] = 0x40; transport.Clockwise = 2; transport.Counterclockwise = 3;
        var state = await client.DeviceTestAsync(0);
        check(state.WasPressed("r0c0") && state.WasPressed("r0c7") && state.WasPressed("r13c6") && !state.WasPressed("r0c1"), "Cumulative matrix bits map physical keys without relying on OS text");
        check(!state.WasPressed("r14c0") && !state.WasPressed("r-1c0") && !state.WasPressed("r0c8") && !state.WasPressed("encoder:cw") && state.Clockwise == 2 && state.Counterclockwise == 3, "Encoder counts and matrix bounds remain separate");
        var refreshed = await client.DeviceTestAsync(3);
        check(refreshed.Active && refreshed.WasPressed("r13c6") && refreshed.Clockwise == 2 && refreshed.Counterclockwise == 3, "Refresh-only action keeps cumulative observations");
        await client.SetDeviceTestLedAsync(1, 0, 255, 255, 255);
        await client.SetDeviceTestLedAsync(2, 125, 255, 0, 0);
        await client.SetDeviceTestLedAsync(0);
        var stopped = await client.DeviceTestAsync(2);
        check(!stopped.Active && stopped.RemainingMs == 0, "Stop is acknowledged as inactive");
        var expiredRejected = false;
        try { await client.DeviceTestAsync(3); } catch (OnboardCommandException error) when (error.Status == 8) { expiredRejected = true; }
        check(expiredRejected && !(await client.DeviceTestAsync(0)).Active, "Expired refresh is a conflict and cannot restart the test");
        check(!transport.Commands.Any(x => x is OnboardProtocol.Begin or OnboardProtocol.Write or OnboardProtocol.Commit or OnboardProtocol.Abort), "All diagnostic actions remain volatile without profile-write commands");

        foreach (var fault in new[] { "length", "format", "active", "rows", "columns", "watchdog", "inactive-watchdog", "no-start", "no-stop" })
        {
            transport.Fault = fault;
            var rejected = false;
            try { await client.DeviceTestAsync(fault == "no-stop" ? (byte)2 : (byte)1); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed diagnostic response rejected: " + fault);
        }
        foreach (var fault in new[] { "led-length", "led-echo", "led-reserved", "led-watchdog" })
        {
            transport.Fault = fault; var rejected = false;
            try { await client.SetDeviceTestLedAsync(2, 1, 20, 40, 60); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed LED acknowledgement rejected: " + fault);
        }
        transport.Fault = "";
        foreach (var invalid in new (byte Mode, byte Index, byte Red)[] { (3,0,0),(2,126,0),(1,1,0),(0,0,1) })
        {
            var previous = transport.Commands.Count; var rejected = false;
            try { await client.SetDeviceTestLedAsync(invalid.Mode, invalid.Index, invalid.Red); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected && transport.Commands.Count == previous, "Invalid LED request is rejected before any hardware command");
        }
        foreach (var issue in new[] { "unsupported", "staging", "volatile" })
        {
            transport.Commands.Clear(); transport.Inner.ExtensionFlags = issue == "unsupported" ? (ushort)3 : (ushort)7;
            transport.Inner.ReportedStaging = issue == "staging" ? 10 : 0; transport.Inner.Persistent = issue != "volatile";
            var rejected = false;
            try { await client.DeviceTestAsync(1); }
            catch (Exception e) when (e is NotSupportedException or IOException) { rejected = true; }
            check(rejected && !transport.Commands.Contains(OnboardProtocol.DeviceTest), "Diagnostic start requires supported and idle persistent firmware: " + issue);
        }
        foreach (var (command, payload) in new (byte, byte[])[] { (14,[0]),(14,[1]),(14,[2]),(14,[3]),(15,[0,0,0,0,0]),(15,[1,0,255,255,255]),(15,[2,125,255,0,0]) })
        {
            var packet = OnboardProtocol.Packet(command, 4, payload); OnboardProtocol.ValidateRequest(packet);
            check(true, "Both transports accept canonical diagnostic request");
            packet[7] = 1; var rejected = false;
            try { OnboardProtocol.ValidateRequest(packet); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Transport rejects nonzero diagnostic packet reserves");
        }
    }

    private sealed class DiagnosticTransport(string id) : IOnboardTransport
    {
        public readonly EmulatedDevice Inner = new(id, OnboardSerializer.Prepare(ProjectService.Create(id)).Payload) { ExtensionFlags = 7 };
        public readonly List<byte> Commands = [];
        public readonly byte[] Rows = new byte[14];
        public ushort Clockwise, Counterclockwise;
        public string Fault = "";
        private bool _active;
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
        {
            OnboardProtocol.ValidateRequest(packet); Commands.Add(packet[3]);
            if (packet[3] is not (OnboardProtocol.DeviceTest or OnboardProtocol.DeviceTestLed)) return Inner.ExchangeAsync(packet, ct);
            byte[] data;
            if (packet[3] == OnboardProtocol.DeviceTest)
            {
                if (packet[8] == 3 && !_active)
                { var conflict = OnboardProtocol.Packet(OnboardProtocol.Info,packet[4],[]); conflict[3] = packet[3]; conflict[5] = 8; return Task.FromResult(conflict); }
                if (packet[8] is 1 or 2) _active = packet[8] == 1;
                data = new byte[24]; data[0] = 1; data[1] = (byte)(_active ? 1 : 0); data[2] = 14; data[3] = 8;
                Rows.CopyTo(data,4); Put(data,18,Clockwise); Put(data,20,Counterclockwise); Put(data,22,_active ? 10000 : 0);
                switch (Fault)
                {
                    case "length": data = data[..^1]; break;
                    case "format": data[0] = 2; break;
                    case "active": data[1] = 2; break;
                    case "rows": data[2] = 13; break;
                    case "columns": data[3] = 7; break;
                    case "watchdog": Put(data,22,10001); break;
                    case "inactive-watchdog": data[1] = 0; break;
                    case "no-start": data[1] = 0; Put(data,22,0); break;
                    case "no-stop": data[1] = 1; Put(data,22,10000); break;
                }
            }
            else
            {
                data = new byte[8]; packet.AsSpan(8,5).CopyTo(data); Put(data,6,9000);
                switch (Fault)
                {
                    case "led-length": data = data[..^1]; break;
                    case "led-echo": data[2] ^= 1; break;
                    case "led-reserved": data[5] = 1; break;
                    case "led-watchdog": Put(data,6,0); break;
                }
            }
            var response = OnboardProtocol.Packet(OnboardProtocol.Info,packet[4],data); response[3] = packet[3]; return Task.FromResult(response);
        }
        private static void Put(byte[] bytes,int offset,int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),(ushort)value);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
