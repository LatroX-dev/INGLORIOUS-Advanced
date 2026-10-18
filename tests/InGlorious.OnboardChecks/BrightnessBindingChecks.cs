using System.Buffers.Binary;
using InGlorious.Core;

internal static class BrightnessBindingChecks
{
    private const string UpdateRequired = "Helligkeit per Taste oder Drehregler benötigt ein Firmware-Update. Es wird nichts übertragen.";
    private static readonly FirmwareSemanticVersion SupportedVersion = new(0, 11, 5);

    public static async Task Run(Action<bool, string> check)
    {
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var original = ProjectService.Create(board.DeviceId);
            var model = DeviceCatalog.Get(board.DeviceId);
            var physical = model.Keys.Where(key => !key.IsEncoder).Take(2).ToArray();
            original.Layers[1].Keys[physical[0].Id].Color = "#12AB34";
            original.Layers[2].SideLedColor = "#3C28F0";
            var configured = ProjectService.Clone(original);
            configured.Layers[1].Keys[physical[0].Id].Binding = "RM_VALD";
            configured.Layers[3].Keys[physical[1].Id].Binding = "RM_VALU";
            if (board.HasEncoder)
            {
                configured.Layers[2].Keys["encoder:ccw"].Binding = "RM_VALD";
                configured.Layers[2].Keys["encoder:cw"].Binding = "RM_VALU";
            }
            var image = OnboardSerializer.Prepare(configured);
            var decoded = OnboardSerializer.Decode(image.Payload);
            check(decoded.Layers[1].Keys[physical[0].Id].Binding == "RM_VALD" && decoded.Layers[3].Keys[physical[1].Id].Binding == "RM_VALU",
                board.DeviceId + ": physical brightness bindings survive serialization on separate layers");
            check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(image.Payload), board.DeviceId + ": brightness bindings retain a byte-exact codec roundtrip");
            check(image.SchemaVersion == 1, board.DeviceId + ": brightness bindings alone require no new configuration schema");
            if (board.HasEncoder)
            {
                var encoderOffset = 32 + 4 * board.Rows * board.Cols * 2 + 2 * 4;
                check(BinaryPrimitives.ReadUInt16LittleEndian(image.Payload.AsSpan(encoderOffset)) == 0x784A &&
                    BinaryPrimitives.ReadUInt16LittleEndian(image.Payload.AsSpan(encoderOffset + 2)) == 0x7849 &&
                    decoded.Layers[2].Keys["encoder:ccw"].Binding == "RM_VALD" && decoded.Layers[2].Keys["encoder:cw"].Binding == "RM_VALU",
                    board.DeviceId + ": both encoder directions use the pinned QMK keycodes on the selected layer");
            }
            // Restore only the edited bindings, then compare the entire wire image: RGB,
            // other layers, knob press, macros and settings must remain identical.
            decoded.Layers[1].Keys[physical[0].Id].Binding = original.Layers[1].Keys[physical[0].Id].Binding;
            decoded.Layers[3].Keys[physical[1].Id].Binding = original.Layers[3].Keys[physical[1].Id].Binding;
            if (board.HasEncoder)
            {
                decoded.Layers[2].Keys["encoder:ccw"].Binding = original.Layers[2].Keys["encoder:ccw"].Binding;
                decoded.Layers[2].Keys["encoder:cw"].Binding = original.Layers[2].Keys["encoder:cw"].Binding;
            }
            check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(OnboardSerializer.Prepare(original).Payload),
                board.DeviceId + ": only the chosen bindings change; colors, knob press, settings and other layers remain intact");
        }
        foreach (var id in new[] { "pro-iso", "numpad", "gmmk3-pro-100-iso" })
            await RunTransferChecks(id, check);
        foreach (var unsupported in new[] { "RM_TOGG", "RM_NEXT" })
        {
            var project = ProjectService.Create("numpad"); project.Layers[0].Keys["encoder:cw"].Binding = unsupported;
            try { _ = OnboardSerializer.Prepare(project); throw new Exception("Unsupported RGB action accepted: " + unsupported); }
            catch (InvalidDataException) { check(true, unsupported + ": unrelated unsupported RGB actions remain rejected"); }
        }
        foreach (var brightnessCode in new[] { "RM_VALU", "RM_VALD" })
        {
            var project = ProjectService.Create("numpad");
            project.Macros.Add(new() { Id = "brightness", Name = "Brightness", Steps = [new() { Kind = "down", Key = brightnessCode }, new() { Kind = "up", Key = brightnessCode }] });
            try { _ = OnboardSerializer.Prepare(project); throw new Exception("Brightness code accepted in HID-only macro events"); }
            catch (InvalidDataException) { check(true, brightnessCode + ": direct bindings do not broaden the keyboard-only macro format"); }
        }
    }

    private static async Task RunTransferChecks(string id, Action<bool, string> check)
    {
        var original = OnboardSerializer.Prepare(ProjectService.Create(id));
        var project = ProjectService.Create(id);
        project.Layers[2].Keys["encoder:ccw"].Binding = "RM_VALD";
        project.Layers[2].Keys["encoder:cw"].Binding = "RM_VALU";
        var image = OnboardSerializer.Prepare(project);
        var target = new EmulatedDevice(id, original.Payload);
        await using (var client = new OnboardClient(target, id))
        {
            check((await client.GetFirmwareVersionAsync())?.Version == SupportedVersion, id + ": compatibility preview sees the supporting firmware");
            target.Commands.Clear();
            var result = await client.ApplyAsync(image);
            check(result.ReadbackVerified && target.Active.SequenceEqual(image.Payload) && target.Generation == 1,
                id + ": supported firmware persists and verifies the complete brightness configuration");
            var begin = target.Commands.IndexOf(OnboardProtocol.Begin);
            var query = target.Commands.IndexOf(OnboardProtocol.GetFirmwareVersion);
            check(query >= 0 && query < begin && target.Commands.Count(command => command == OnboardProtocol.GetFirmwareVersion) == 1,
                id + ": upload freshly checks firmware before staging instead of trusting a prior preview");
            check(!target.Commands.Contains(OnboardProtocol.GetExtensions), id + ": brightness requires no guessed extension flag");
            target.FirmwareVersion = new(0, 11, 4); target.Commands.Clear();
            await RejectUpload(client, image, check, id + ": cached supported firmware cannot permit an older current device", true);
            check(target.Commands.Contains(OnboardProtocol.GetFirmwareVersion) && NoWrites(target) && target.Generation == 1 && target.Active.SequenceEqual(image.Payload),
                id + ": fresh downgrade detection preserves the saved configuration without staging, committing or aborting");
        }

        foreach (var failure in new[] { "old", "missing", "malformed", "wrong-board", "busy", "io", "timeout" })
        {
            target = new(id, original.Payload);
            switch (failure)
            {
                case "old": target.FirmwareVersion = new(0, 11, 4); break;
                case "missing": target.FirmwareVersionStatus = 9; break;
                case "malformed": target.FirmwareVersionPayloadOverride = [1, 0, 1]; break;
                case "wrong-board": target.FirmwareVersionPayloadOverride = VersionData(id == "numpad" ? "pro-iso" : "numpad"); break;
                case "busy": target.FirmwareVersionStatus = 3; break;
                case "io": target.FirmwareVersionFailure = new IOException("Synthetic version transport failure"); break;
                case "timeout": target.FirmwareVersionFailure = new TimeoutException("Synthetic version timeout"); break;
            }
            await using var client = new OnboardClient(target, id);
            await RejectUpload(client, image, check, id + ": unsafe brightness upload rejected for " + failure, failure is "old" or "missing");
            check(NoWrites(target) && target.Generation == 0 && target.Active.SequenceEqual(original.Payload),
                id + ": " + failure + " sends no staging or persistent command and preserves every original byte");
        }
        foreach (var version in new[] { SupportedVersion, new FirmwareSemanticVersion(0, 12, 0), new(1, 0, 0) })
        {
            target = new(id, original.Payload) { FirmwareVersion = version };
            await using var client = new OnboardClient(target, id);
            check((await client.ApplyAsync(image)).ReadbackVerified && target.Generation == 1,
                id + ": firmware " + version + " accepts brightness bindings without requiring an exact release match");
        }
        foreach (var code in new[] { "RM_VALU", "RM_VALD" })
        {
            var physicalOnly = ProjectService.Create(id);
            var key = DeviceCatalog.Get(id).Keys.First(key => !key.IsEncoder);
            physicalOnly.Layers[3].Keys[key.Id].Binding = code;
            target = new(id, original.Payload) { FirmwareVersion = new(0, 11, 4) };
            await using var client = new OnboardClient(target, id);
            await RejectUpload(client, OnboardSerializer.Prepare(physicalOnly), check, id + ": a physical " + code + " binding on a higher layer also requires new firmware", true);
            check(NoWrites(target), id + ": compatibility checks include physical keys and all layers");
        }
        // Ordinary existing mappings remain usable without the version command.
        target = new(id, original.Payload) { FirmwareVersionStatus = 9 };
        await using (var client = new OnboardClient(target, id))
        {
            check((await client.ApplyAsync(original)).ReadbackVerified && !target.Commands.Contains(OnboardProtocol.GetFirmwareVersion),
                id + ": existing mappings still transfer to firmware without a version response");
        }
        using var cancellation = new CancellationTokenSource();
        target = new(id, original.Payload) { BeforeFirmwareVersionReply = cancellation.Cancel };
        await using (var client = new OnboardClient(target, id))
        {
            try { await client.ApplyAsync(image, cancellationToken: cancellation.Token); throw new Exception("Canceled brightness transfer accepted"); }
            catch (OperationCanceledException) { check(true, id + ": cancellation during the firmware check remains cancellation"); }
            check(NoWrites(target) && target.Active.SequenceEqual(original.Payload), id + ": cancellation cannot stage, commit or erase a configuration");
            target.BeforeFirmwareVersionReply = null; target.Commands.Clear();
            check((await client.ApplyAsync(image)).ReadbackVerified, id + ": a canceled check releases the client for a subsequent explicit transfer");
        }
    }

    private static async Task RejectUpload(OnboardClient client, OnboardImage image, Action<bool, string> check, string reason, bool requireUpdateMessage = false)
    {
        try { await client.ApplyAsync(image); }
        catch (Exception error) when (error is InvalidDataException or IOException or TimeoutException)
        {
            check(!requireUpdateMessage || error.Message == L.T(UpdateRequired), reason);
            return;
        }
        throw new Exception(reason);
    }
    private static bool NoWrites(EmulatedDevice target) => !target.Commands.Any(command => command is OnboardProtocol.Begin or OnboardProtocol.Write or OnboardProtocol.Commit or OnboardProtocol.Abort);
    private static byte[] VersionData(string id)
    {
        var data = new byte[16]; data[0] = 1; data[2] = OnboardSerializer.Shape(id).Board; data[6] = 11; data[8] = 5; data[12] = 1;
        return data;
    }
}
