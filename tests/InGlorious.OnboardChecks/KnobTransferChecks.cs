using InGlorious.Core;

internal static class KnobTransferChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        await CheckOrderTransfers(check);
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards.Where(board => board.HasEncoder))
        {
            var id = board.DeviceId;
            var initial = OnboardSerializer.Prepare(ProjectService.Create(id));
            var profile = ProjectService.Create(id);
            profile.Layers[2].KnobCycleEnabled = true;
            profile.Layers[2].KnobCycleMacZoom = true;
            profile.Layers[2].KnobCycleMask = 255;
            profile.Layers[2].KnobStep = 5;
            profile.Layers[2].KnobUndoLayout = 2;
            var configured = OnboardSerializer.Prepare(profile);
            var target = Capable(id, initial.Payload);
            await using (var client = new OnboardClient(target, id))
            {
                _ = await client.GetExtensionsAsync(); _ = await client.GetFirmwareVersionAsync(); target.Commands.Clear();
                var result = await client.ApplyAsync(configured);
                check(result.ReadbackVerified && target.Generation == 1 && target.Active.SequenceEqual(configured.Payload),
                    id + ": every encoder board transfers and verifies the complete knob configuration");
                var begin = target.Commands.IndexOf(OnboardProtocol.Begin);
                check(target.Commands.IndexOf(OnboardProtocol.GetFirmwareVersion) is var versionRead && versionRead >= 0 && versionRead < begin &&
                    target.Commands.IndexOf(OnboardProtocol.GetExtensions) is var extensionsRead && extensionsRead >= 0 && extensionsRead < begin,
                    id + ": current firmware and extension support are checked again before staging");
                target.Commands.Clear(); target.ExtensionFlags = 3;
                await Reject(client, configured, check, id + ": cached extension support cannot permit a transfer after capability changes");
                check(NoWrites(target) && target.Generation == 1 && target.Active.SequenceEqual(configured.Payload),
                    id + ": losing support preserves every byte of the last successful configuration");
            }
            foreach (var failure in new[] { "old-version", "missing-version", "wrong-board", "version-busy", "version-io", "version-timeout", "version-malformed", "no-extension", "unknown-extension", "unsupported-extension", "extension-busy", "extension-io", "extension-malformed", "no-standby", "no-effects" })
            {
                target = Capable(id, initial.Payload);
                switch (failure)
                {
                    case "old-version": target.FirmwareVersion = new(0, 11, 5); break;
                    case "missing-version": target.FirmwareVersionStatus = 9; break;
                    case "wrong-board": target.FirmwareVersionPayloadOverride = VersionData(id == "numpad" ? "pro-iso" : "numpad"); break;
                    case "version-busy": target.FirmwareVersionStatus = 3; break;
                    case "version-io": target.FirmwareVersionFailure = new IOException("Synthetic version failure"); break;
                    case "version-timeout": target.FirmwareVersionFailure = new TimeoutException("Synthetic timeout"); break;
                    case "version-malformed": target.FirmwareVersionPayloadOverride = [1, 0, 1]; break;
                    case "no-extension": target.ExtensionFlags = 3; break;
                    case "unknown-extension": target.ExtensionFlags = 43; break;
                    case "unsupported-extension": target.ExtensionStatus = 9; break;
                    case "extension-busy": target.ExtensionStatus = 3; break;
                    case "extension-io": target.ExtensionThrows = true; break;
                    case "extension-malformed": target.ExtensionPayloadOverride = [1, 0, 11, 0]; break;
                    case "no-standby": target.ExtraFlags = 96; break;
                    case "no-effects": target.EffectsSupported = false; target.TimingSupported = false; break;
                }
                await using var client = new OnboardClient(target, id);
                await Reject(client, configured, check, id + ": " + failure + " rejects the full knob configuration");
                check(NoWrites(target) && target.Generation == 0 && target.Active.SequenceEqual(initial.Payload),
                    id + ": " + failure + " performs no staging, persistence or abort operation");
            }
            // Step and function selection still need the new format when long press is off.
            var stepOnly = ProjectService.Create(id); stepOnly.Layers[1].KnobStep = 2;
            target = Capable(id, initial.Payload); target.ExtensionFlags = 3;
            await using (var client = new OnboardClient(target, id))
            {
                await Reject(client, OnboardSerializer.Prepare(stepOnly), check, id + ": step-only changes still require the new configuration capability");
                check(NoWrites(target), id + ": disabling long press cannot bypass the knob settings capability gate");
            }
            using var cancellation = new CancellationTokenSource();
            target = Capable(id, initial.Payload); target.BeforeFirmwareVersionReply = cancellation.Cancel;
            await using (var client = new OnboardClient(target, id))
            {
                try { await client.ApplyAsync(configured, cancellationToken: cancellation.Token); throw new Exception("Canceled knob upload accepted"); }
                catch (OperationCanceledException) { check(NoWrites(target), id + ": cancellation during capability checks prevents writes"); }
                target.BeforeFirmwareVersionReply = null;
                check((await client.ApplyAsync(configured)).ReadbackVerified, id + ": cancellation releases the transfer lock for a later explicit retry");
            }
        }

        foreach (var code in new[] { "MS_WHLU", "MS_WHLD", "MS_WHLL", "MS_WHLR", "IG_ZOOM_IN", "IG_ZOOM_OUT", "IG_ZOOM_IN_MAC", "IG_ZOOM_OUT_MAC", "IG_TAB_PREV", "IG_TAB_NEXT", "IG_UNDO", "IG_REDO", "IG_UNDO_MAC", "IG_REDO_MAC" })
        {
            const string id = "numpad";
            var initial = OnboardSerializer.Prepare(ProjectService.Create(id));
            var profile = ProjectService.Create(id);
            profile.Layers[3].Keys[DeviceCatalog.Get(id).Keys.First(key => !key.IsEncoder).Id].Binding = code;
            var image = OnboardSerializer.Prepare(profile);
            check(image.SchemaVersion == 1, code + ": a direct action does not silently enable cycling");
            var target = Capable(id, initial.Payload); target.FirmwareVersion = new(0, 11, 5);
            await using (var client = new OnboardClient(target, id))
            {
                await Reject(client, image, check, code + ": physical actions on higher layers still require supporting firmware");
                check(NoWrites(target) && target.Active.SequenceEqual(initial.Payload), code + ": rejected extended action preserves original configuration");
            }
            foreach (var version in new[] { new FirmwareSemanticVersion(0, 11, 6), new(1, 0, 0) })
            {
                target = Capable(id, initial.Payload); target.FirmwareVersion = version; target.ExtensionFlags = 3;
                await using var client = new OnboardClient(target, id);
                check((await client.ApplyAsync(image)).ReadbackVerified && !target.Commands.Contains(OnboardProtocol.GetExtensions),
                    code + ": direct actions require the firmware version but no unrelated cycle capability");
            }
        }
    }

    private static async Task CheckOrderTransfers(Action<bool, string> check)
    {
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards.Where(board => board.HasEncoder))
        {
            var id = board.DeviceId;
            var initial = OnboardSerializer.Prepare(ProjectService.Create(id));
            var project = ProjectService.Create(id);
            project.Layers[2].KnobCycleOrder = [128, 64, 32, 16, 8, 4, 2, 1];
            var image = OnboardSerializer.Prepare(project);
            check(image.SchemaVersion == 6 && project.Layers.All(layer => !layer.KnobCycleEnabled),
                id + ": saved ordering requires schema 6 even while long press is disabled");
            foreach (var version in new[] { new FirmwareSemanticVersion(0, 11, 7), new(1, 0, 0) })
            {
                var target = Capable(id, initial.Payload); target.FirmwareVersion = version; target.ExtensionFlags = 27;
                await using var client = new OnboardClient(target, id);
                _ = await client.GetExtensionsAsync(); _ = await client.GetFirmwareVersionAsync(); target.Commands.Clear();
                check((await client.ApplyAsync(image)).ReadbackVerified && target.Active.SequenceEqual(image.Payload),
                    id + ": supported firmware persists the complete reordered configuration");
                var begin = target.Commands.IndexOf(OnboardProtocol.Begin);
                check(target.Commands.IndexOf(OnboardProtocol.GetExtensions) is var extensions && extensions >= 0 && extensions < begin &&
                    target.Commands.IndexOf(OnboardProtocol.GetFirmwareVersion) is var firmware && firmware >= 0 && firmware < begin,
                    id + ": reordered transfer rechecks both firmware and capabilities before its first write");
                target.Commands.Clear(); target.ExtensionFlags = 11;
                await Reject(client, image, check, id + ": cached order support cannot bypass a missing current capability");
                check(NoWrites(target) && target.Active.SequenceEqual(image.Payload) && target.Generation == 1,
                    id + ": rejected repeat leaves the verified configuration unchanged");
            }
            foreach (var failure in new[] { "old-version", "no-order", "no-cycle", "unknown-extension", "missing-version" })
            {
                var target = Capable(id, initial.Payload); target.FirmwareVersion = new(0, 11, 7); target.ExtensionFlags = 27;
                switch (failure)
                {
                    case "old-version": target.FirmwareVersion = new(0, 11, 6); break;
                    case "no-order": target.ExtensionFlags = 11; break;
                    case "no-cycle": target.ExtensionFlags = 19; break;
                    case "unknown-extension": target.ExtensionFlags = 43; break;
                    case "missing-version": target.FirmwareVersionStatus = 9; break;
                }
                await using var client = new OnboardClient(target, id);
                await Reject(client, image, check, id + ": " + failure + " prevents a reordered transfer");
                check(NoWrites(target) && target.Generation == 0 && target.Active.SequenceEqual(initial.Payload),
                    id + ": order gating preserves the original without staging or aborting");
            }
        }
    }

    private static EmulatedDevice Capable(string id, byte[] initial) => new(id, initial.ToArray())
    { FirmwareVersion = new(0, 11, 6), EffectsSupported = true, TimingSupported = true, ExtraFlags = 224, ExtensionFlags = 11 };
    private static bool NoWrites(EmulatedDevice target) => !target.Commands.Any(command => command is OnboardProtocol.Begin or OnboardProtocol.Write or OnboardProtocol.Commit or OnboardProtocol.Abort);
    private static async Task Reject(OnboardClient client, OnboardImage image, Action<bool, string> check, string reason)
    {
        try { await client.ApplyAsync(image); }
        catch (Exception error) when (error is InvalidDataException or IOException or TimeoutException or NotSupportedException)
        { check(true, reason); return; }
        throw new Exception(reason);
    }
    private static byte[] VersionData(string id)
    {
        var data = new byte[16]; data[0] = 1; data[2] = OnboardSerializer.Shape(id).Board; data[6] = 11; data[8] = 6; data[12] = 1;
        return data;
    }
}
