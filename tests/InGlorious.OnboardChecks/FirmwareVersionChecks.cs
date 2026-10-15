using System.Buffers.Binary;
using InGlorious.Core;

internal static class FirmwareVersionChecks
{
    private static readonly FirmwareSemanticVersion TestVersion = new(0, 11, 1);
    private const uint TestBuildId = 0x000B0101;
    public static async Task Run(Action<bool, string> check)
    {
        RunChangelogChecks(check);
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or TimeoutException or OperationCanceledException) { check(true, label); return; }
            throw new Exception(label);
        }
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
                check(report == new OnboardFirmwareVersion(id, TestVersion, TestBuildId), id + ": exact RAW13 fields decoded");
                check(transport.Calls == 1 && transport.LastPacket is { } packet && packet[3] == 13 && packet[6] == 0 && packet.AsSpan(8).IndexOfAnyExcept((byte)0) < 0,
                    id + ": one empty read-only request, no configuration or flash command");
                transport.Status = 9;
                check(await client.GetFirmwareVersionAsync() is null, id + ": explicit unsupported command is the only version-unavailable result");
                transport.Status = 0;
                transport.Payload = VersionTransport.Data(id, new(0, 11, 2), 0x000B0201);
                var future = await client.GetFirmwareVersionAsync();
                check(future!.Version > TestVersion && future.BuildId == 0x000B0201, id + ": newer patch remains visible and must not trigger downgrade");
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
                    transport.Payload = VersionTransport.Data(id, TestVersion, TestBuildId);
                    transport.Payload[offset] = (byte)value;
                    await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": malformed format, board, flags or reserved bytes stay unverified");
                }
                transport.Payload = VersionTransport.Data(id, TestVersion, TestBuildId);
                transport.Payload.AsSpan(12, 4).Clear();
                await Reject(async () => _ = await client.GetFirmwareVersionAsync(), id + ": zero build identifier remains unverified");
                transport.Payload = VersionTransport.Data(id, TestVersion, TestBuildId);
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
    private static void RunChangelogChecks(Action<bool, string> check)
    {
        foreach (var deviceId in new[] { "pro-iso", "numpad" })
        {
            var v07 = new FirmwareSemanticVersion(0, 7, 0);
            var v08 = new FirmwareSemanticVersion(0, 8, 0);
            var v09 = new FirmwareSemanticVersion(0, 9, 0);
            var v091 = new FirmwareSemanticVersion(0, 9, 1);
            var v092 = new FirmwareSemanticVersion(0, 9, 2);
            var current = new FirmwareSemanticVersion(0, 11, 1);
            var notes = FirmwareChangelog.Between(deviceId, v07, v09);
            check(notes.Select(n => n.Version).SequenceEqual(new[] { v09, v08 }), "Changelog includes skipped releases in descending firmware-version order");
            check(notes[0].Changes.Count == 2 && notes[1].Changes.Count == 1, "Each firmware release has only its concise documented changes");
            check(notes[0].Changes.SequenceEqual(new[] { "New lighting test after firmware updates.", "Optional screensaver." }), "Both supported devices show the exact short update notes");
            check(notes[1].Changes.SequenceEqual(new[] { "Installed firmware version shown in the app." }), "Previous release retains its short customer-facing version note");
            check(FirmwareChangelog.Between(deviceId, v08, v09).Select(n => n.Version).SequenceEqual(new[] { v09 }), "Installed version is an exclusive lower bound");
            check(FirmwareChangelog.Between(deviceId, v07, v08).Select(n => n.Version).SequenceEqual(new[] { v08 }), "Target version is an inclusive upper bound");
            check(FirmwareChangelog.Between(deviceId, v07, new(0, 8, 99)).Select(n => n.Version).SequenceEqual(new[] { v08 }), "A target below0.9 cannot include0.9 notes");
            var currentNotes = FirmwareChangelog.Between(deviceId, v07, current);
            var expectedVersions = deviceId == "numpad" ? new[] { current, v092, v091, v09, v08 } : new[] { current, v092, v09, v08 };
            check(currentNotes.Select(n => n.Version).SequenceEqual(expectedVersions), "Both devices receive current shared fixes; only Numpad receives its earlier slider fix");
            check(FirmwareChangelog.Between(deviceId, new(0, 11, 0), current).Single().Changes.SequenceEqual(new[] { "Fixed reactive lighting reappearing after long periods without input." }),
                "The current update shows its actual fix instead of being hidden by the historical local firmware version");
            var patchNotes = FirmwareChangelog.Between(deviceId, v09, v091);
            var colorNotes = FirmwareChangelog.Between(deviceId, v091, v092);
            check(colorNotes.Count == 1 && colorNotes[0].Version == v092 && colorNotes[0].Changes.SequenceEqual(new[] { "Improved color output for less washed-out colors." }),
                "Both devices receive exactly the short color-output note from0.9.1 to0.9.2");
            check(deviceId == "numpad" ? patchNotes.Count == 1 && patchNotes[0].Version == v091
                && patchNotes[0].Changes.SequenceEqual(new[] { "Fixed the Numpad lighting test stopping too early.", "Reduced unintended wake-ups from standby." }) : patchNotes.Count == 0,
                "Numpad patch note is exact; a PRO patch update invents no device-specific fix");
            var redDefaults = new FirmwareSemanticVersion(0, 11, 3);
            var lightingFix = new FirmwareSemanticVersion(0, 11, 4);
            check(FirmwareChangelog.Between(deviceId, v07, new(65535, 65535, 65535)).All(n => n.Version <= lightingFix)
                && FirmwareChangelog.Between(deviceId, v07, new(65535, 65535, 65535)).Select(n => n.Version).SequenceEqual(new[] { lightingFix, redDefaults }.Concat(expectedVersions)), "A future target cannot expose notes beyond the current documented firmware");
            check(FirmwareChangelog.Between(deviceId, current, redDefaults).Single().Changes.SequenceEqual(new[] { "Red default lighting when no saved colors are available." }), "New default-color note does not invent per-key diagnostics for other boards");
            foreach (var installed in new FirmwareSemanticVersion?[] { null, default(FirmwareSemanticVersion), current, new(0, 11, 2), new(1, 0, 0) })
                check(FirmwareChangelog.Between(deviceId, installed, current).Count == 0, "Missing/default/current/newer firmware has no proposed update changelog");
            check(FirmwareChangelog.Between(deviceId, v08, v08).Count == 0 && FirmwareChangelog.Between(deviceId, v09, v08).Count == 0, "Equal or downgrade targets never show update notes");
            check(FirmwareChangelog.Between(deviceId, new(0, 4, 0), v07).Count == 0, "No features are invented for uncatalogued releases");
            var englishNotes = currentNotes.SelectMany(n => n.Changes).ToArray();
            L.SetLanguage("ja");
            check(FirmwareChangelog.Between(deviceId, v07, current).SelectMany(n => n.Changes).SequenceEqual(englishNotes)
                && englishNotes[0] == "Fixed reactive lighting reappearing after long periods without input."
                && englishNotes[^1] == "Installed firmware version shown in the app.", "Changelog remains English independently of the app language");
            L.SetLanguage("de");
            check(((ICollection<FirmwareReleaseNotes>)notes).IsReadOnly && ((ICollection<string>)notes[0].Changes).IsReadOnly, "Catalog results and their changes cannot mutate shared release notes");
        }
        var previousRelease = new FirmwareSemanticVersion(0, 11, 3);
        var latestRelease = new FirmwareSemanticVersion(0, 11, 4);
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var notes = FirmwareChangelog.Between(board.DeviceId, previousRelease, latestRelease);
            check(notes.Count == 1 && notes[0].Version == latestRelease && notes[0].Changes.SequenceEqual(new[] { "Fixed lighting tests being blocked when no key is pressed." }),
                board.DeviceId + ": all included firmware targets receive only the new lighting-test fix when upgrading from0.11.3");
            check(FirmwareChangelog.Between(board.DeviceId, new(0, 11, 2), previousRelease).All(note => note.Version < latestRelease),
                board.DeviceId + ": a target before the fix never offers the new note");
            foreach (var installed in new FirmwareSemanticVersion?[] { null, default(FirmwareSemanticVersion), latestRelease, new(0, 11, 5) })
                check(FirmwareChangelog.Between(board.DeviceId, installed, latestRelease).Count == 0,
                    board.DeviceId + ": missing, current or newer firmware has no pending lighting-test fix");
            check(FirmwareChangelog.Between(board.DeviceId, previousRelease, new(99, 0, 0)).Select(note => note.Version).SequenceEqual(new[] { latestRelease }),
                board.DeviceId + ": future target cannot invent changes after the documented lighting-test fix");
            check(FirmwareChangelog.Between(board.DeviceId, latestRelease, new(99, 0, 0)).Count == 0,
                board.DeviceId + ": undocumented future updates have no guessed notes");
        }
        foreach (var unknownDevice in new string?[] { null, "", "PRO-ISO", "pro", "GMMK PRO", "other", "numpad " })
            check(FirmwareChangelog.Between(unknownDevice!, new(0, 7, 0), latestRelease).Count == 0, "Unknown model cannot receive another device's changelog");
    }
    private sealed class VersionTransport(string id) : IOnboardTransport
    {
        public byte[] Payload = Data(id, TestVersion, TestBuildId);
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
