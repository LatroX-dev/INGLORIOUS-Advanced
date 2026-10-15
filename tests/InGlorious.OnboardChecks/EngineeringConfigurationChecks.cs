using InGlorious.Core;
using InGlorious.DeviceDiagnostics;

internal static class EngineeringConfigurationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        const string id = EngineeringConfigurationPolicy.DeviceId;
        const string path = @"\\?\hid#vid_feed&pid_1c10&mi_01#test";
        static void Reject(Action action, Action<bool, string> check, string reason)
        {
            try { action(); } catch (Exception error) when (error is InvalidDataException or ArgumentException) { check(true, reason); return; }
            throw new Exception(reason);
        }
        async Task RejectAsync(Func<Task> action, string reason)
        {
            try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException) { check(true, reason); return; }
            throw new Exception(reason);
        }
        var model = DeviceCatalog.Get(id);
        check(!model.IsOfflineOnly && !model.IsExperimentalFirmware && model.HasApprovedFirmwareManagement && BoardDescriptorRegistry.ProtocolBoards.Count == 16,
            "The exact hardware-tested PRO retains normal configuration support without an experimental label");
        check(OnboardHidTransport.IsAllowedPath(path), "Exact PRO runtime path is recognized by the normal transport");
        foreach (var invalid in new[] { @"\\?\hid#vid_342d&pid_e3ce#test", @"\\?\hid#vid_342d&pid_dfa0#test", @"\\?\hid#vid_feed&pid_1c11#test", @"C:\vid_feed&pid_1c10" })
            check(!OnboardHidTransport.IsAllowedPath(invalid, true), "Engineering opt-in never permits stock, bootloader, unknown or non-HID paths");
        var baseline = ProjectService.Create(id);
        var initial = OnboardSerializer.Prepare(baseline, true);
        check(OnboardSerializer.Prepare(baseline).Payload.SequenceEqual(initial.Payload) && OnboardSerializer.Decode(initial.Payload).DeviceId == id,
            "Normal PRO codec preserves the explicit board16 identity and existing payload format");
        var noFlagTransport = new EmulatedDevice(id, initial.Payload);
        await using (var ordinary = new OnboardClient(noFlagTransport, id))
            check((await ordinary.GetInfoAsync()).DeviceId == id, "Normal client checks the device-reported PRO identity");
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var ordinary = ProjectService.Create(board.DeviceId);
            var normal = OnboardSerializer.Prepare(ordinary).Payload;
            check(normal.SequenceEqual(OnboardSerializer.Prepare(ordinary, true).Payload) &&
                OnboardSerializer.Prepare(OnboardSerializer.Decode(normal)).Payload.SequenceEqual(normal) &&
                OnboardSerializer.Prepare(OnboardSerializer.Decode(normal, true)).Payload.SequenceEqual(normal),
                "All existing models retain their default codec bytes " + board.DeviceId);
        }
        for (var schema = 1; schema <= 4; schema++)
        {
            var project = ProjectService.Create(id);
            foreach (var layer in project.Layers)
            {
                foreach (var key in layer.Keys.Values) key.Color = "#2064DC";
                layer.SideLedColor = "#918273";
                foreach (var led in model.NonKeyLedIndices) layer.ExtraLedColors[led] = led % 2 == 0 ? "#123456" : "#ABCDEF";
                if (schema >= 2) layer.LightingEffect.Mode = 1;
                if (schema >= 3) layer.LightingEffect.Colors = ["#0077FF", "#AA3355"];
            }
            if (schema == 4) project.Standby = new() { Enabled = true, Mode = 2 };
            var image = OnboardSerializer.Prepare(project, true);
            var decoded = OnboardSerializer.Decode(image.Payload, true);
            check(image.SchemaVersion == schema && image.Payload[6] == 16 && image.UsedBytes == (schema == 1 ? 5656 : 7200) && image.CapacityBytes == 7648,
                "Engineering codec retains exact board16 geometry and schema " + schema);
            check(OnboardSerializer.Prepare(decoded, true).Payload.SequenceEqual(image.Payload) &&
                decoded.Layers.All(layer => model.Keys.Where(key => key.LedIndex.HasValue).All(key => layer.Keys[key.Id].Color == "#2064DC")),
                "Stored blue/custom colors survive decoding instead of adopting new-profile red " + schema);
            var transport = Ready(id, initial.Payload);
            await using var client = new OnboardClient(transport, id, true);
            var before = await client.GetInfoAsync();
            var saved = await client.ApplyAsync(image, expectedGeneration: before.Generation);
            check(saved.ReadbackVerified && transport.Active.SequenceEqual(image.Payload) && saved.Crc32 == OnboardProtocol.Crc32(image.Payload) &&
                transport.Commands.Count(command => command == OnboardProtocol.Commit) == 1 && saved.Generation == before.Generation + 1,
                "Engineering transaction commits once and verifies all bytes/CRC/generation " + schema);
            var captured = await client.ReadAsync();
            var verified = await client.ApplyAsync(new(id, captured, image.MacroEvents, image.KeyCount, image.LedCount, []), expectedGeneration: saved.Generation);
            check(verified.ReadbackVerified && transport.Active.SequenceEqual(captured), "Verify-save persists the exact captured bytes without resetting colors " + schema);
        }
        var stale = Ready(id, initial.Payload); stale.Generation = 7;
        await using (var client = new OnboardClient(stale, id, true))
            await RejectAsync(async () => _ = await client.ApplyAsync(initial, expectedGeneration: 6), "Stale generation blocks staging");
        check(!stale.Commands.Contains(OnboardProtocol.Begin), "Stale capture sends no BEGIN");
        var badRead = Ready(id, initial.Payload); badRead.CorruptRead = true;
        await using (var client = new OnboardClient(badRead, id, true))
            await RejectAsync(async () => _ = await client.ApplyAsync(initial), "Corrupt backup read blocks staging");
        check(!badRead.Commands.Contains(OnboardProtocol.Begin), "Corrupt capture sends no BEGIN");
        var lostCommit = Ready(id, initial.Payload); lostCommit.LoseCommitReply = true;
        await using (var client = new OnboardClient(lostCommit, id, true))
            await RejectAsync(async () => _ = await client.ApplyAsync(initial), "Lost COMMIT acknowledgement remains an uncertain outcome");
        check(lostCommit.Commands.Count(command => command == OnboardProtocol.Commit) == 1 && !lostCommit.Commands.Contains(OnboardProtocol.Abort),
            "Engineering COMMIT is never repeated and uncertain commit sends no cleanup command");
        var wrongBoard = Ready("gmmk3-100-iso", OnboardSerializer.Prepare(ProjectService.Create("gmmk3-100-iso")).Payload);
        await using (var client = new OnboardClient(wrongBoard, id, true))
            await RejectAsync(async () => _ = await client.ApplyAsync(initial), "Generic ISO cannot receive a PRO16 configuration");
        check(!wrongBoard.Commands.Contains(OnboardProtocol.Begin), "Wrong board sends no BEGIN");
        RunArguments(check, Reject);
    }

    private static EmulatedDevice Ready(string id, byte[] initial) => new(id, initial)
    { EffectsSupported = true, TimingSupported = true, ExtraFlags = 32 | 64 | 128 };

    private static void RunArguments(Action<bool, string> check, Action<Action, Action<bool, string>, string> reject)
    {
        const string id = EngineeringConfigurationPolicy.DeviceId;
        var projectPath = Path.GetFullPath("offline-test-profile.json");
        var read = DeviceArguments.Parse(["read", "--engineering", "--device", id, "--out", "new-output"]);
        check(read.Engineering && read.Operation == "read" && read.DeviceId == id, "Explicit engineering read arguments accepted without enabling writes");
        check(DeviceArguments.Parse(["verify-save", "--engineering", "--device", id, "--out", "new-output", "--confirm-write"]).Engineering,
            "Exact captured-byte persistence requires separate confirmation");
        check(DeviceArguments.Parse(["apply", "--engineering", "--device", id, "--project", projectPath, "--out", "new-output", "--confirm-write"]).ProjectPath == projectPath,
            "Engineering apply binds exact model, project and confirmation");
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            check(!DeviceArguments.Parse(["read", board.DeviceId, "--out", "new-output"]).Engineering &&
                !DeviceArguments.Parse(["verify-save", board.DeviceId, "--out", "new-output"]).Engineering &&
                !DeviceArguments.Parse(["apply", board.DeviceId, "--project", projectPath, "--out", "new-output"]).Engineering,
                "Existing CLI syntax remains unchanged " + board.DeviceId);
        }
        foreach (var invalid in new string[][] {
            ["read", "--engineering", "--device", "gmmk3-100-iso", "--out", "new-output"],
            ["verify-save", "--engineering", "--device", id, "--out", "new-output"],
            ["apply", "--engineering", "--device", id, "--project", projectPath, "--out", "new-output"],
            ["read", "--engineering", "--device", id, "--out", "new-output", "--confirm-write"],
            ["read", "--engineering", "--engineering", "--device", id, "--out", "new-output"],
            ["read", "--engineering", "--device", "unknown", "--out", "new-output"],
            ["apply", "--engineering", "--device", id, "--project", "relative.json", "--out", "new-output", "--confirm-write"],
            ["list", "--engineering"], ["apply", "--device", id, "--project", projectPath, "--out", "new-output"] })
            reject(() => DeviceArguments.Parse(invalid), check, "Malformed/missing/mismatched engineering arguments reject before hardware");
        read.ValidateRuntime(new(id, new(0, 11, 2), 0x000B0201));
        check(true, "Reviewed engineering runtime version/build accepted");
        foreach (var invalid in new OnboardFirmwareVersion?[] { null, new(id, new(0, 11, 1), 0x000B0101), new(id, new(0, 11, 2), 1), new(id, new(0, 11, 3), 0x000B0301), new("gmmk3-100-iso", new(0, 11, 2), 0x000B0201) })
            reject(() => read.ValidateRuntime(invalid), check, "Other runtime model/version/build fails before configuration read/staging");
    }
}
