using InGlorious.Core;

internal static class BootloaderEntryChecks
{
    private static readonly OnboardFirmwareVersion Expected = new("numpad", new(0, 11, 12), 0x000B0C01);
    internal static async Task Run(Action<bool, string> check)
    {
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            { check(true, label); return; }
            throw new Exception(label);
        }
        var transport = new EntryTransport();
        await using (var client = new OnboardClient(transport, "numpad"))
        {
            check(await client.CanEnterBootloaderAsync(Expected), "Numpad advertised entry requires exact current identity");
            check(transport.Packets.Count == 0, "Capability discovery cannot arm or enter bootloader");
            await Reject(() => client.EnterBootloaderAsync(Expected, false), "Confirmation required before any entry command");
            check(transport.Packets.Count == 0, "Unconfirmed update never sends prepare");
            await client.EnterBootloaderAsync(Expected, true);
            check(transport.Packets.Select(p => p[3]).SequenceEqual(new byte[] {25,26}), "One acknowledged prepare followed by one commit");
            check(transport.Packets[0].AsSpan(8,24).SequenceEqual(transport.Packets[1].AsSpan(8,24)), "Commit bound to exact identity and unpredictable preparation nonce");
            check(transport.Packets[0].AsSpan(24,8).IndexOfAnyExcept((byte)0) >= 0, "Preparation uses nonzero random nonce");
            await Reject(() => client.EnterBootloaderAsync(Expected, true), "Consumed transition cannot be retried on same session");
            check(transport.Packets.Count == 2, "No duplicate commit after success");
        }
        foreach (var mode in new[] {"old-version", "changed-build", "no-capability", "not-persistent", "staging", "empty-staging"})
        {
            transport = new EntryTransport();
            switch (mode)
            {
                case "old-version": transport.Inner.FirmwareVersionPayloadOverride![8] = 11; break;
                case "changed-build": transport.Inner.FirmwareVersionPayloadOverride![12] ^= 1; break;
                case "no-capability": transport.Inner.ExtensionFlags = 3; break;
                case "not-persistent": transport.Inner.Persistent = false; break;
                case "staging": transport.Inner.ReportedStaging = 5; break;
                case "empty-staging": transport.Inner.ExtraFlags = 4; break;
            }
            await using var client = new OnboardClient(transport, "numpad");
            await Reject(() => client.EnterBootloaderAsync(Expected, true), mode + " blocks entry");
            check(transport.Packets.Count == 0, mode + " sends no mutation");
        }
        foreach (var mode in new[] {"wrong-prepare", "prepare-timeout", "config-change", "busy-commit", "wrong-commit", "commit-timeout"})
        {
            transport = new EntryTransport { Failure = mode };
            await using var client = new OnboardClient(transport, "numpad");
            await Reject(() => client.EnterBootloaderAsync(Expected, true), mode + " remains a failure");
            var commands = transport.Packets.Select(p => p[3]).ToArray();
            check(commands.Count(c => c == 26) <= 1, mode + " does not repeat commit");
            if (mode == "config-change") check(commands.SequenceEqual(new byte[] {25,27}), "Verified preparation cancels when configuration changes");
            else if (mode is "wrong-prepare" or "prepare-timeout") check(commands.SequenceEqual(new byte[] {25}), "Unacknowledged preparation expires without commit");
            else
            {
                check(commands.SequenceEqual(new byte[] {25,26}), "Commit error never sends cancel or further mutation");
                await Reject(() => client.EnterBootloaderAsync(Expected, true), "Ambiguous entry cannot be retried");
                check(transport.Packets.Count == 2, "Ambiguous entry remains single-use");
            }
        }
        transport = new EntryTransport();
        await using (var client = new OnboardClient(transport, "numpad"))
        {
            await Reject(() => client.EnterBootloaderAsync(Expected, true, expectedConfigurationCrc: OnboardProtocol.Crc32(transport.Inner.Active) ^ 1), "Snapshot must match the configuration before preparation");
            check(transport.Packets.Count == 0, "Configuration changed since backup sends no preparation");
        }
        transport = new EntryTransport();
        await using (var client = new OnboardClient(transport, "pro-iso"))
        {
            check(!await client.CanEnterBootloaderAsync(Expected), "Another device identity cannot reuse Numpad entry");
            await Reject(() => client.EnterBootloaderAsync(Expected, true), "Cannot reuse Numpad entry on another model");
            check(transport.Inner.Commands.Count == 0 && transport.Packets.Count == 0, "Wrong-model entry performs no IO");
        }
        var valid = transport.Inner.FirmwareVersionPayloadOverride!.Concat(new byte[] {1,2,3,4,5,6,7,8}).ToArray();
        foreach (var command in new byte[] {25,26,27})
        {
            OnboardProtocol.ValidateRequest(OnboardProtocol.Packet(command,1,valid));
            foreach (var mutate in new Action<byte[]>[] {p => p[2]=255, p => p[3]=1, p => p[10]=1, p => p.AsSpan(16).Clear()})
            {
                var invalid=valid.ToArray(); mutate(invalid);
                try { OnboardProtocol.Packet(command,1,invalid); throw new Exception("Unsafe bootloader packet admitted"); }
                catch (ArgumentOutOfRangeException) { check(true,"Wire request rejects wrong board, reserved fields and empty nonce"); }
            }
        }
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            check(FirmwareChangelog.Between(board.DeviceId, new(0,11,11), new(0,11,12)).Count == 1, "Supported model receives update-entry release note");
            if (board.BoardId == 16) continue;
            var boardTransport = new EntryTransport(board.DeviceId);
            await using var boardClient = new OnboardClient(boardTransport, board.DeviceId);
            var version = Expected with { DeviceId = board.DeviceId };
            check(await boardClient.CanEnterBootloaderAsync(version), board.DeviceId + " advertises explicit entry capability");
            await boardClient.EnterBootloaderAsync(version, true);
            check(boardTransport.Packets.Select(p => p[3]).SequenceEqual(new byte[] {25,26}) && boardTransport.Packets.All(p => p[10] == board.BoardId), board.DeviceId + " keeps exact model identity in one-shot entry");
        }
        check(!OnboardClient.SupportsAppBootloaderDevice("gmmk3-pro-he-100-iso") && !OnboardClient.SupportsAppBootloaderDevice("unknown"), "Unknown and unsupported hardware never inherits entry capability");
        foreach (var mode in new[] { "valid", "missing-binding", "stale-binding", "switch-after-prepare", "high-generation-change", "identity-change", "upload-open" })
        {
            var bank = new BankEntryTransport();
            bank.Inner.InfoPersistent = false; // Bank health, not the legacy slot, authorizes this model.
            await using var client = new OnboardClient(bank, "gmmk3-pro-100-iso");
            var snapshot = await client.ReadHardwareProfileAsync(1);
            var binding = snapshot.Binding;
            var version = Expected with { DeviceId = "gmmk3-pro-100-iso" };
            if (mode == "stale-binding") binding = binding with { Generation = binding.Generation + (1UL << 32) };
            if (mode == "upload-open") bank.Inner.UploadAlreadyOpen = true;
            bank.Failure = mode;
            Task Enter() => client.EnterBootloaderAsync(version, true, expectedConfigurationCrc: OnboardProtocol.Crc32(snapshot.Payload),
                expectedHardwareProfile: mode == "missing-binding" ? null : binding);
            if (mode == "valid")
            {
                await Enter();
                check(bank.Packets.Select(p => p[3]).SequenceEqual(new byte[] {25,26}), "Profile bank uses exact installation/slot/full-generation binding without legacy storage");
            }
            else
            {
                await Reject(Enter, mode + " rejects changed or missing profile binding");
                check(!bank.Packets.Any(p => p[3] == 26), mode + " never sends commit");
                if (mode is "switch-after-prepare" or "high-generation-change" or "identity-change")
                    check(bank.Packets.Select(p => p[3]).SequenceEqual(new byte[] {25,27}), mode + " cancels the prepared request");
                else check(bank.Packets.Count == 0, mode + " never arms entry");
            }
        }
        foreach (var version in new FirmwareSemanticVersion?[] { null, new(0,11,12), new(0,11,13) })
            check(FirmwareChangelog.Between("numpad", version, new(0,11,12)).Count == 0, "Missing/current/newer version does not receive pending entry notes");
    }

    private sealed class EntryTransport : IOnboardTransport
    {
        public readonly EmulatedDevice Inner;
        public EntryTransport(string id = "numpad")
        {
            Inner = new(id, OnboardSerializer.Prepare(ProjectService.Create(id)).Payload)
            {
                ExtensionFlags = 515,
                FirmwareVersionPayloadOverride = VersionPayload(id)
            };
        }
        public List<byte[]> Packets { get; } = [];
        public string Failure = "";
        public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); OnboardProtocol.ValidateRequest(packet);
            var command = packet[3];
            if (command is <25 or >27) return await Inner.ExchangeAsync(packet,ct);
            Packets.Add(packet.ToArray());
            if (Failure == "prepare-timeout" && command == 25 || Failure == "commit-timeout" && command == 26) throw new IOException("Lost reply");
            var reply = packet.ToArray();
            if (Failure == "wrong-prepare" && command == 25 || Failure == "wrong-commit" && command == 26) reply[31] ^= 1;
            if (Failure == "busy-commit" && command == 26) reply[5] = 3;
            if (Failure == "config-change" && command == 25) Inner.Generation++;
            return reply;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] VersionPayload(string id) => [1,0,BoardDescriptorRegistry.Get(id).BoardId,0,0,0,11,0,12,0,0,0,1,12,11,0];

    private sealed class BankEntryTransport : IOnboardTransport
    {
        public readonly HardwareProfileChecks.Fake Inner = new();
        public List<byte[]> Packets { get; } = [];
        public string Failure = "";
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); OnboardProtocol.ValidateRequest(packet);
            byte[] data;
            if (packet[3] is >= 25 and <= 27)
            {
                Packets.Add(packet.ToArray()); data = packet.AsSpan(8,24).ToArray();
                if (packet[3] == 25)
                {
                    if (Failure == "switch-after-prepare") Inner.Active = 2;
                    if (Failure == "high-generation-change") Inner.Generations[0] += 1UL << 32;
                    if (Failure == "identity-change") Inner.Identity = Guid.NewGuid();
                }
            }
            else if (packet[3] == 13) data = VersionPayload("gmmk3-pro-100-iso");
            else if (packet[3] == 11) data = [1,0,255,3,8,82,0,0];
            else return Inner.ExchangeAsync(packet,ct);
            var result = new byte[32]; packet.AsSpan(0,5).CopyTo(result); result[6] = (byte)data.Length; data.CopyTo(result,8);
            return Task.FromResult(result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
