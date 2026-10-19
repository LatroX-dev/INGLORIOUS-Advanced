using System.Buffers.Binary;
using InGlorious.Core;

static class HardwareProfileChecks
{
    const string Id = "gmmk3-pro-100-iso";
    public static async Task Run(Action<bool, string> check)
    {
        async Task Reject(Func<Task> action, string reason)
        { try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or OperationCanceledException) { check(true, reason); return; } throw new Exception(reason); }
        var fake = new Fake(); await using var client = new OnboardClient(fake, Id);
        var status = await client.GetHardwareProfileStatusAsync();
        check(status is { Slot: 1, ActiveSlot: 1 } && status.Generation > uint.MaxValue, "bank status preserves uint64 generation");
        var snapshot = await client.ReadHardwareProfileAsync(2);
        check(snapshot.Binding.Slot == 2 && snapshot.Status.ActiveSlot == 1 && fake.Active == 1 && snapshot.Payload.SequenceEqual(fake.Payload[1]), "inactive bank read never activates it");
        check(fake.Commands.All(c => c is 11 or 18 or 19 or 24), "snapshot uses read-only bank commands");
        var edit = ProjectService.Create(Id); edit.Layers[0].Keys.Values.First().Color = "#FF1234"; var image = OnboardSerializer.Prepare(edit);
        fake.SwitchOnWrite = true;
        var result = await client.ApplyHardwareProfileAsync(image, snapshot.Binding);
        check(result.ReadbackVerified && result.Binding.Slot == 2 && result.Binding.Generation == snapshot.Binding.Generation + 1 &&
            fake.Active == 3 && fake.Payload[1].SequenceEqual(image.Payload), "physical switch during upload cannot retarget the destination");
        check(fake.Commands.Count(c => c == 22) == 1 && !fake.Commands.Any(c => c is 3 or 4 or 5), "bank transfer uses one explicit commit and no legacy writes");
        var writes = fake.Commands.Count(c => c == 20);
        await Reject(() => client.ApplyHardwareProfileAsync(image, snapshot.Binding), "stale profile generation refused");
        check(fake.Commands.Count(c => c == 20) == writes, "conflict is detected before staging");
        await Reject(() => client.ApplyHardwareProfileAsync(image, result.Binding with { InstallationId = Guid.NewGuid() }), "other installation refused");
        await Reject(() => client.ApplyHardwareProfileAsync(image, result.Binding with { Crc32 = 123 }), "same generation with changed CRC refused");
        await Reject(() => client.ApplyHardwareProfileAsync(image, result.Binding with { Generation = ulong.MaxValue }), "generation rollover never wraps");
        var beforeInactive = fake.Commands.Count(c => c == 20);
        await Reject(() => client.ApplyHardwareProfileAsync(image, result.Binding, requireActiveSlot: true), "visible UI transfer cannot begin on an already inactive slot");
        check(fake.Commands.Count(c => c == 20) == beforeInactive, "active-slot guard acts before BEGIN");
        fake.CorruptRead = true;
        await Reject(() => client.ReadHardwareProfileAsync(2), "read bytes require whole-payload CRC"); fake.CorruptRead = false;
        fake.IdentityChanges = true;
        await Reject(() => client.ReadHardwareProfileAsync(2), "installation identity changes during read are rejected"); fake.IdentityChanges = false;
        var lost = new Fake { LoseCommit = true }; await using var lostClient = new OnboardClient(lost, Id);
        var lostBinding = (await lostClient.ReadHardwareProfileAsync(1)).Binding;
        await Reject(() => lostClient.ApplyHardwareProfileAsync(image, lostBinding), "lost commit acknowledgement is uncertain");
        check(lost.Commands.Count(c => c == 22) == 1 && !lost.Commands.Contains(23), "uncertain commit is neither repeated nor aborted");
        var chunk = new Fake { WrongWriteAck = true }; await using var chunkClient = new OnboardClient(chunk, Id);
        var chunkBinding = (await chunkClient.ReadHardwareProfileAsync(1)).Binding;
        await Reject(() => chunkClient.ApplyHardwareProfileAsync(image, chunkBinding), "wrong chunk owner rejected");
        check(chunk.Commands.Count(c => c == 23) == 1 && !chunk.Commands.Contains(22), "only owned staging is aborted before commit");
        var unsupported = new Fake { Capability = false }; await using var old = new OnboardClient(unsupported, Id);
        check(await old.GetHardwareProfileStatusAsync() is null && !unsupported.Commands.Contains(18), "diagnostic RAW18 is never sent without capability");
        await Reject(() => old.ReadHardwareProfileAsync(1), "bank read cannot bypass capability");
        check(!unsupported.Commands.Any(c => c >= 18), "unsupported firmware sees no bank command");
        var alien = new Fake(); await using var other = new OnboardClient(alien, "numpad");
        check(await other.GetHardwareProfileStatusAsync() is null && alien.Commands.Count == 0, "similar device has no inferred bank API");
        foreach (int field in new[] { 0, 1, 2, 3, 4, 6, 7, 20, 22 })
        {
            var bytes = fake.Status(2); bytes[field] = 255;
            await Reject(() => Task.FromResult(OnboardClient.ParseHardwareProfileStatus(bytes, Id, 2)), "malformed profile status field " + field);
        }
        foreach (int size in new[] { 0, 1, 23, 25 })
            await Reject(() => Task.FromResult(OnboardClient.ParseHardwareProfileStatus(new byte[size], Id, 2)), "malformed status size " + size);
        var canceled = new CancellationTokenSource(); canceled.Cancel(); var count = fake.Commands.Count;
        await Reject(() => client.ReadHardwareProfileAsync(2, canceled.Token), "pre-canceled read rejected");
        check(fake.Commands.Count == count, "pre-canceled read performs no device operation");
        var startupFake = new Fake();
        var endpoint = new OnboardDevice(Id, "fixture", "fixture");
        var startup = await StartupDeviceLoader.LoadAsync(_ => Task.FromResult<IReadOnlyList<OnboardDevice>>([endpoint]),
            _ => new OnboardClient(startupFake, Id), deviceIds: [Id]);
        check(startup.Single().ProfileSnapshot?.Binding.Slot == 1 && startup.Single().Loaded && !startupFake.Commands.Contains(2), "startup reads actual explicit hardware profile");
        var held = new Fake { UploadAlreadyOpen = true }; await using var heldClient = new OnboardClient(held, Id);
        await Reject(() => heldClient.ReadHardwareProfileAsync(1), "existing empty upload blocks profile snapshot");
        check(!held.Commands.Contains(19) && !held.Commands.Contains(23), "reader neither steals nor aborts another upload");
        var legacy = new Fake(); await using var legacyClient = new OnboardClient(legacy, Id);
        await Reject(() => legacyClient.ApplyAsync(image), "old implicit write API is blocked on capable firmware");
        check(!legacy.Commands.Any(c => c is 3 or 4 or 5), "blocked implicit API emits no write command");
        await Reject(() => legacyClient.ReconnectUsbAsync(1, 1), "old implicit reconnect API is blocked on capable firmware");
        check(!legacy.Commands.Contains(8), "blocked implicit reconnect sends no USB command");
        var timing = new Fake { InfoPersistent = false }; await using var timingClient = new OnboardClient(timing, Id);
        var timingProject = ProjectService.Create(Id); timingProject.DeviceSettings.UsbPollingIntervalMs = 4;
        timing.Payload[0] = OnboardSerializer.Prepare(timingProject).Payload;
        var timingBinding = (await timingClient.ReadHardwareProfileAsync(1)).Binding;
        var reconnect = await timingClient.ReconnectHardwareProfileUsbAsync(timingBinding);
        check(reconnect is { ActiveIntervalMs: 1, SavedIntervalMs: 4, ReconnectQueued: true } && timing.Reconnects == 1,
            "bank reconnect binds uint64 generation and trusts bank health rather than legacy INFO persistence");
        await Reject(() => timingClient.ReconnectHardwareProfileUsbAsync(timingBinding with { InstallationId = Guid.NewGuid() }), "reconnect refuses different installation");
        await Reject(() => timingClient.ReconnectHardwareProfileUsbAsync(timingBinding with { Generation = timingBinding.Generation + 1 }), "reconnect refuses stale generation");
        timing.Active = 2;
        await Reject(() => timingClient.ReconnectHardwareProfileUsbAsync(timingBinding), "inactive profile cannot reconnect another profile");
        check(timing.Reconnects == 1, "failed reconnect preconditions send no restart");
        timing.Active = 1; timing.SwitchOnReconnect = true;
        await Reject(() => timingClient.ReconnectHardwareProfileUsbAsync(timingBinding), "firmware binding closes final-read to reconnect race");
        check(timing.Reconnects == 1 && timing.Active == 2, "last-moment switch never restarts new profile");
        timing.Active = 1; timing.SwitchOnReconnect = false; timing.LoseReconnect = true;
        int beforeReconnect = timing.Commands.Count(c => c == 8);
        await Reject(() => timingClient.ReconnectHardwareProfileUsbAsync(timingBinding), "missing reconnect ACK remains unconfirmed");
        check(timing.Commands.Count(c => c == 8) == beforeReconnect + 1 && timing.Generations[0] == timingBinding.Generation,
            "uncertain USB restart is not retried and does not alter saved generation");
        var boundPacket = new byte[16]; boundPacket[0] = 1;
        OnboardProtocol.ValidateRequest(OnboardProtocol.Packet(8, 1, boundPacket)); check(true, "exact bound reconnect shape accepted");
        foreach (int reserved in new[] { 1, 2, 3 })
        { var invalid = boundPacket.ToArray(); invalid[reserved] = 1; try { OnboardProtocol.Packet(8, 1, invalid); throw new Exception("reserved accepted"); } catch (ArgumentOutOfRangeException) { check(true, "bound reconnect reserved rejected"); } }
    }

    sealed class Fake : IOnboardTransport
    {
        public List<byte> Commands = [];
        public byte[][] Payload = Enumerable.Range(0, 3).Select(_ => OnboardSerializer.Prepare(ProjectService.Create(Id)).Payload).ToArray();
        public ulong[] Generations = [0x1_00000001, 0x1_00000002, 0x1_00000003];
        public Guid Identity = Guid.NewGuid();
        public byte Active = 1;
        public bool Capability = true, InfoPersistent = true, SwitchOnWrite, LoseCommit, WrongWriteAck, CorruptRead, IdentityChanges, UploadAlreadyOpen, SwitchOnReconnect, LoseReconnect;
        public int Reconnects;
        int identityCalls, received; byte slot; uint token; byte[]? stage;
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); OnboardProtocol.ValidateRequest(packet);
            byte command = packet[3]; Commands.Add(command); var p = packet.AsSpan(8, packet[6]); byte[] result;
            switch (command)
            {
                case 1:
                    var schema = BinaryPrimitives.ReadUInt16LittleEndian(Payload[Active - 1].AsSpan(4)); var shape = OnboardSerializer.Shape(Id, schema);
                    result = new byte[24]; result[0] = 16; result[1] = (byte)shape.Rows; result[2] = (byte)shape.Cols; result[3] = 4;
                    result[4] = (byte)shape.Leds; result[5] = 32; result[6] = 128; result[7] = InfoPersistent ? (byte)249 : (byte)248;
                    P16(result, 8, Payload[Active - 1].Length); P16(result, 10, OnboardSerializer.MacroCapacity(Id, schema));
                    P32(result, 12, (uint)Generations[Active - 1]); P32(result, 16, OnboardProtocol.Crc32(Payload[Active - 1])); P16(result, 22, schema); break;
                case 11: result = [1, 0, 255, Capability ? (byte)1 : (byte)0, 8, 82, 0, 0]; break;
                case 8:
                    if (SwitchOnReconnect) Active = 2;
                    if (p.Length != 16 || p[0] != Active || BinaryPrimitives.ReadUInt64LittleEndian(p[4..]) != Generations[Active - 1] ||
                        BinaryPrimitives.ReadUInt32LittleEndian(p[12..]) != OnboardProtocol.Crc32(Payload[Active - 1])) throw new IOException("reconnect binding");
                    Reconnects++;
                    if (LoseReconnect) throw new IOException("lost reconnect acknowledgement");
                    int saved = OnboardSerializer.Decode(Payload[Active - 1]).DeviceSettings.UsbPollingIntervalMs;
                    result = [1, (byte)saved, (byte)(saved == 1 ? 0 : 1), 0]; break;
                case 18: result = Status(p[0] == 0 ? Active : p[0]); break;
                case 24:
                    result = new byte[20]; result[0] = 1; result[1] = 16;
                    (IdentityChanges && ++identityCalls % 2 == 0 ? Guid.NewGuid() : Identity).TryWriteBytes(result.AsSpan(4)); break;
                case 19:
                    int offset = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
                    if (BinaryPrimitives.ReadUInt64LittleEndian(p[4..]) != Generations[p[0] - 1] || BinaryPrimitives.ReadUInt32LittleEndian(p[12..]) != OnboardProtocol.Crc32(Payload[p[0] - 1])) throw new IOException("stale fixture read");
                    result = new byte[12 + p[1]]; p[..12].CopyTo(result); Payload[p[0] - 1].AsSpan(offset, p[1]).CopyTo(result.AsSpan(12));
                    if (CorruptRead) result[12] ^= 1; break;
                case 20: slot = p[0]; token = BinaryPrimitives.ReadUInt32LittleEndian(p[20..]); stage = new byte[BinaryPrimitives.ReadUInt16LittleEndian(p[2..])]; received = 0; result = p.ToArray(); break;
                case 21:
                    if (BinaryPrimitives.ReadUInt32LittleEndian(p) != token || p[4] != slot) throw new IOException("owner");
                    p[8..].CopyTo(stage!.AsSpan(received)); received += p[5]; result = new byte[8]; P32(result, 0, WrongWriteAck ? token ^ 1 : token); result[4] = slot; P16(result, 6, received);
                    if (SwitchOnWrite) Active = 3; break;
                case 22:
                    if (p[4] != slot || BinaryPrimitives.ReadUInt32LittleEndian(p) != token) throw new IOException("owner");
                    Payload[slot - 1] = stage!; Generations[slot - 1]++; stage = null;
                    if (LoseCommit) throw new IOException("lost acknowledgement");
                    result = new byte[16]; result[0] = slot; P16(result, 2, received); P64(result, 4, Generations[slot - 1]); P32(result, 12, OnboardProtocol.Crc32(Payload[slot - 1])); break;
                case 23: stage = null; result = p.ToArray(); break;
                default: throw new IOException("Unexpected command " + command);
            }
            var response = new byte[32]; response[0] = 73; response[1] = 71; response[2] = 1; response[3] = command; response[4] = packet[4]; response[6] = (byte)result.Length; result.CopyTo(response, 8);
            return Task.FromResult(response);
        }
        public byte[] Status(byte query)
        {
            var result = new byte[24]; result[0] = 1; result[1] = 16; result[2] = 3; result[3] = Active; result[4] = Active;
            result[5] = (byte)(1 | (stage is null && !UploadAlreadyOpen ? 0 : 8)); result[6] = query;
            P64(result, 8, Generations[query - 1]); P32(result, 16, OnboardProtocol.Crc32(Payload[query - 1]));
            P16(result, 20, Payload[query - 1].Length); P16(result, 22, BinaryPrimitives.ReadUInt16LittleEndian(Payload[query - 1].AsSpan(4))); return result;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        static void P16(byte[] b, int i, int x) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i), (ushort)x);
        static void P32(byte[] b, int i, uint x) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i), x);
        static void P64(byte[] b, int i, ulong x) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i), x);
    }
}
