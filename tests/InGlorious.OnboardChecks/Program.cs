using System.Buffers.Binary;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
void Reject(Action action, string reason) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(reason); }
async Task RejectAsync(Func<Task> action, string reason) { try { await action(); } catch (Exception e) when (e is InvalidDataException or IOException) { checks++; return; } throw new Exception(reason); }

Check(OnboardProtocol.Crc32("123456789"u8) == 0xCBF43926, "IEEE CRC32 reference vector");
await FirmwareVersionChecks.Run(Check);
await ExperimentalBoardChecks.Run(Check, args);
await StartupDeviceChecks.Run(Check);
Check(!OnboardHidTransport.IsAllowedPath(@"\\?\hid#vid_320f&pid_5088&mi_02#x"), "Stock Numpad path excluded");
Check(!OnboardHidTransport.IsAllowedPath(@"\\?\hid#vid_320f&pid_5046&mi_02#x"), "Stock PRO path excluded");
Check(!OnboardHidTransport.IsAllowedPath(@"C:\vid_feed&pid_1c01&fake"), "Non-HID paths excluded");
Check(OnboardHidTransport.IsAllowedPath(@"\\?\hid#vid_feed&pid_1c01&mi_01#x"), "Development identity accepted for descriptor validation");

foreach (var (id, suffix) in new[] { ("pro-iso", " PRO ISO"), ("numpad", " Numpad") })
{
    Check(OnboardIdentity.MatchesNames(id, "InGlorious", "InGlorious" + suffix), "Installed firmware keeps its original USB identity");
    Check(OnboardIdentity.MatchesNames(id, "INGLORIOUS", "INGLORIOUS" + suffix), "Future firmware uses the uppercase USB identity");
    Check(!OnboardIdentity.MatchesNames(id, "InGlorious", "INGLORIOUS" + suffix), "Mixed-generation USB identity is rejected");
    Check(!OnboardIdentity.MatchesNames(id, "INGLORIOUS", "InGlorious" + suffix), "Reverse mixed-generation USB identity is rejected");
    Check(!OnboardIdentity.MatchesNames(id, "inglorious", "inglorious" + suffix), "Unrecognized capitalization is rejected");
    Check(!OnboardIdentity.MatchesNames(id, "INGLORIOUS ", "INGLORIOUS" + suffix), "Altered manufacturer is rejected");
}
Check(!OnboardIdentity.MatchesNames("pro-iso", "INGLORIOUS", "INGLORIOUS Numpad"), "Wrong board name cannot match PRO");
Check(!OnboardIdentity.MatchesNames("numpad", "InGlorious", "InGlorious PRO ISO"), "Wrong board name cannot match Numpad");
Check(!OnboardIdentity.MatchesNames("unknown", "INGLORIOUS", "INGLORIOUS Numpad"), "Unknown board has no valid identity");

foreach (var id in new[] { "pro-iso", "numpad" })
{
    var project = ProjectService.Create(id);
    var shape = OnboardSerializer.Shape(id);
    var effectShape = OnboardSerializer.Shape(id, 2);
    var image = OnboardSerializer.Prepare(project);
    Check(image.Payload.Length == (id == "pro-iso" ? 5140 : 3780), "Exact wire blob length");
    Check(image.Payload[6] == shape.Board && image.Payload[8] == shape.Rows && image.Payload[9] == shape.Cols, "Board dimensions");
    Check(image.LedCount == (id == "pro-iso" ? 99 : 31), "LED dimensions");
    Check(OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.SequenceEqual(image.Payload), "Default byte-exact codec roundtrip");
    project.Layers[2].SideLedColor = "#123456";
    var extraLed = DeviceCatalog.Get(id).NonKeyLedIndices.First();
    project.Layers[2].ExtraLedColors[extraLed] = "#FEDCBA";
    project.Macros.Add(new() { Id = "copy", Name = "Copy", Steps = [new() { Kind = "down", Key = "KC_LCTL" }, new() { Kind = "down", Key = "KC_C" }, new() { Kind = "delay", DelayMs = 25 }, new() { Kind = "up", Key = "KC_C" }, new() { Kind = "up", Key = "KC_LCTL" }] });
    var ordinaryKey = DeviceCatalog.Get(id).Keys.First(k => !k.IsEncoder);
    project.Layers[3].Keys[ordinaryKey.Id].Binding = "MACRO:copy";
    if (id == "numpad") project.Slider = new() { Mode = 2, Minimum = 13, Maximum = 1000, MidiChannel = 4, MidiController = 75 };
    image = OnboardSerializer.Prepare(project);
    Check(image.MacroEvents == 5, "Macro event count includes delay");
    Check(OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.SequenceEqual(image.Payload), "Color, macro, layer and slider wire roundtrip");

    project.Layers[2].SynchronizeSides = true;
    Check(OnboardSerializer.Prepare(project).Payload.SequenceEqual(image.Payload), "Side-sync preference alone preserves schema 1 and all original bytes");
    project.Layers[0].LightingEffect = new() { Mode = 1, Speed = 0, Brightness = 0, Target = 1, Reverse = true };
    project.Layers[1].LightingEffect = new() { Mode = 3, Speed = 255, Brightness = 255, Target = 2 };
    project.Layers[2].LightingEffect = new() { Mode = 10, Speed = 128, Brightness = 173, Target = 3 };
    project.Layers[3].LightingEffect = new() { Mode = 12, Speed = 1, Brightness = 255, Target = 3 };
    var effectImage = OnboardSerializer.Prepare(project);
    Check(effectImage.SchemaVersion == 2 && effectImage.RequiresEffects && effectImage.Payload.Length == (id == "pro-iso" ? 6360 : 4184), "Effects use the fixed schema 2 extension");
    Check(effectImage.Payload.AsSpan(0, 4).SequenceEqual(image.Payload.AsSpan(0, 4)) && effectImage.Payload.AsSpan(6, 18).SequenceEqual(image.Payload.AsSpan(6, 18)) && effectImage.Payload.AsSpan(26, shape.Size - 26).SequenceEqual(image.Payload.AsSpan(26)), "Adding effects preserves every legacy RGB, key, encoder, slider and macro byte");
    Check(effectImage.Payload.AsSpan(26, 6).IndexOfAnyExcept((byte)0) < 0 && effectImage.Payload[17] == 0 && effectImage.Payload[24] == 1 && effectImage.Payload[25] == 5, "Schema 2 uses default timing and keeps remaining header reserves zero");
    Check(effectImage.Payload.AsSpan(shape.Size, 8).SequenceEqual(new byte[] { 1, 0, 0, 1, 1, 0, 0, 0 }), "Effect parameters and direction have exact wire offsets");
    Check(effectImage.Payload[shape.Size + 2 * 8 + 4] == 2, "Side synchronization uses only flag bit 1");
    var effectDecoded = OnboardSerializer.Decode(effectImage.Payload);
    Check(effectDecoded.Layers[2].SynchronizeSides && effectDecoded.Layers[0].LightingEffect.Reverse && effectDecoded.Layers[2].LightingEffect.Brightness == 173, "All effect parameters and side preference decode");
    Check(OnboardSerializer.Prepare(effectDecoded).Payload.SequenceEqual(effectImage.Payload), "Schema 2 byte-exact codec roundtrip");
    for (var mode = 0; mode <= 20; mode++)
    {
        var modeProject = ProjectService.Clone(project);
        modeProject.Layers[0].LightingEffect.Mode = mode;
        var modeImage = OnboardSerializer.Prepare(modeProject);
        Check(OnboardSerializer.Prepare(OnboardSerializer.Decode(modeImage.Payload)).Payload.SequenceEqual(modeImage.Payload), $"Mode {mode} codec roundtrip");
    }
    foreach (var (offset, value) in new[] { (0, 21), (3, 0), (3, 4), (4, 4), (5, 1), (6, 1), (7, 1) })
    {
        var invalid = effectImage.Payload.ToArray(); invalid[shape.Size + offset] = (byte)value;
        Reject(() => OnboardSerializer.Decode(invalid), "Unknown mode, target, flags and reserved effect bytes are rejected");
    }
    var invalidEventTail = effectImage.Payload.ToArray(); invalidEventTail[shape.Size - 1] = 1;
    Reject(() => OnboardSerializer.Decode(invalidEventTail), "Schema 2 still checks unused macro pool bytes before its extension");
    Reject(() => OnboardSerializer.Decode(effectImage.Payload[..^1]), "Truncated effect section rejected");
    Reject(() => OnboardSerializer.Decode([.. effectImage.Payload, 0]), "Oversized effect section rejected");
    var invalidEffectProject = ProjectService.Clone(project); invalidEffectProject.Layers[0].LightingEffect.Speed = 256;
    Reject(() => OnboardSerializer.Prepare(invalidEffectProject), "Effect speed overflow cannot truncate to one byte");
    invalidEffectProject = ProjectService.Clone(project); invalidEffectProject.Layers[0].LightingEffect.Brightness = -1;
    Reject(() => OnboardSerializer.Prepare(invalidEffectProject), "Negative effect brightness rejected");
    var overrideOffset = shape.Size + 32;
    Check(Enumerable.Range(0, 4 * shape.Leds).All(i => effectImage.Payload.AsSpan(overrideOffset + i * 3, 3).SequenceEqual(new byte[] { 255, 0, 0 })), "All absent overrides use canonical inherited records");
    for (var mode = 0; mode <= 20; mode++)
    {
        var individual = ProjectService.Clone(project);
        individual.Layers[0].LightingEffect.Target = 2;
        individual.Layers[0].LedEffects[0] = new() { Mode = mode, Reverse = true, Speed = 0, Brightness = 255 };
        individual.Layers[3].LedEffects[shape.Leds - 1] = new() { Mode = mode, Speed = 255, Brightness = 0 };
        var encoded = OnboardSerializer.Prepare(individual);
        Check(encoded.Payload.AsSpan(overrideOffset, 3).SequenceEqual(new byte[] { (byte)(mode | 128), 0, 255 }), "Individual mode and direction packed without reserved bits");
        var decoded = OnboardSerializer.Decode(encoded.Payload);
        Check(decoded.Layers[0].LedEffects[0].Reverse && decoded.Layers[3].LedEffects[shape.Leds - 1].Mode == mode && decoded.Layers[0].LightingEffect.Target == 2, "Per-LED effects survive independently of global target");
        Check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(encoded.Payload), "Individual effects byte-exact roundtrip");
    }
    foreach (var invalidRecord in new byte[][] { [255, 1, 0], [255, 0, 1], [21, 0, 0], [149, 0, 0], [32, 0, 0], [64, 0, 0], [160, 0, 0], [192, 0, 0] })
    {
        var invalid = effectImage.Payload.ToArray(); invalidRecord.CopyTo(invalid, overrideOffset);
        Reject(() => OnboardSerializer.Decode(invalid), "Malformed individual effect or inherited record rejected");
    }
    project.Layers[0].LedEffects[0] = new() { Mode = 20, Speed = 255, Brightness = 128, Reverse = true };
    project.Layers[3].LedEffects[shape.Leds - 1] = new() { Mode = 0, Speed = 0, Brightness = 0 };
    effectImage = OnboardSerializer.Prepare(project);
    foreach (var layer in project.Layers) layer.LightingEffect = new();
    Check(OnboardSerializer.Prepare(project).SchemaVersion == 2, "An individual static or dynamic override alone requires schema 2");
    foreach (var layer in project.Layers) layer.LedEffects.Clear();
    Check(OnboardSerializer.Prepare(project).Payload.SequenceEqual(image.Payload), "Returning effects to defaults restores schema 1 without changing original data");
    foreach (var interval in new[] { 1, 2, 4, 8 }) foreach (var debounce in new[] { 0, 5, 40 })
    {
        project.DeviceSettings = new() { UsbPollingIntervalMs = interval, DebounceMs = debounce };
        var timing = OnboardSerializer.Prepare(project);
        Check(!timing.RequiresEffects && timing.RequiresTiming == (interval != 1 || debounce != 5), "Timing capability is independent of effects");
        var decoded = OnboardSerializer.Decode(timing.Payload);
        Check(decoded.DeviceSettings.UsbPollingIntervalMs == interval && decoded.DeviceSettings.DebounceMs == debounce && OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(timing.Payload), "Timing settings byte-exact roundtrip");
    }
    project.DeviceSettings = new() { UsbPollingIntervalMs = 8, DebounceMs = 40 };
    var timingImage = OnboardSerializer.Prepare(project);
    project.DeviceSettings = new();
    foreach (var (offset, value) in new[] { (24, 0), (24, 3), (24, 16), (25, 41), (26, 1), (31, 1) })
    {
        var invalid = effectImage.Payload.ToArray(); invalid[offset] = (byte)value;
        Reject(() => OnboardSerializer.Decode(invalid), "Invalid timing or reserved header bytes rejected");
    }

    var payload = image.Payload.ToArray(); payload[24] = 1;
    Reject(() => OnboardSerializer.Decode(payload), "Reserved header data rejected");
    payload = image.Payload.ToArray(); payload[^1] = 1;
    Reject(() => OnboardSerializer.Decode(payload), "Unused macro pool data rejected");
    project.Layers[0].Keys["encoder:cw"].Binding = "MO(1)";
    Reject(() => OnboardSerializer.Prepare(project), "Momentary layer on rotary impulse rejected");
    project.Layers[0].Keys["encoder:cw"].Binding = "KC_VOLU";
    project.Macros[0].Steps[0].Key = "KC_MPLY"; project.Macros[0].Steps[^1].Key = "KC_MPLY";
    Reject(() => OnboardSerializer.Prepare(project), "Consumer media excluded from keyboard macro events");

    // Firmware input fixtures are shared with the native firmware test runner, never sent to USB.
    var fixtureDir = Path.Combine(Paths.WorkspaceRoot, "artifacts", "protocol-fixtures");
    Directory.CreateDirectory(fixtureDir);
    File.WriteAllBytes(Path.Combine(fixtureDir, id + "-configured.bin"), image.Payload);
    File.WriteAllBytes(Path.Combine(fixtureDir, id + "-effects.bin"), effectImage.Payload);
    File.WriteAllBytes(Path.Combine(fixtureDir, id + "-timing.bin"), timingImage.Payload);
    var schema2Default = OnboardSerializer.Prepare(ProjectService.Create(id));
    var schema2DefaultBytes = new byte[effectShape.Size]; schema2Default.Payload.CopyTo(schema2DefaultBytes, 0); schema2DefaultBytes[4] = 2; schema2DefaultBytes[24] = 1; schema2DefaultBytes[25] = 5;
    for (var layer = 0; layer < 4; layer++) { var offset = shape.Size + layer * 8; schema2DefaultBytes[offset + 1] = 128; schema2DefaultBytes[offset + 2] = 255; schema2DefaultBytes[offset + 3] = 3; }
    for (var i = 0; i < 4 * shape.Leds; i++) schema2DefaultBytes[overrideOffset + i * 3] = 255;
    Check(OnboardSerializer.Prepare(OnboardSerializer.Decode(schema2DefaultBytes)).Payload.SequenceEqual(schema2Default.Payload), "Default schema 2 decodes without changes and re-encodes as compatible schema 1");
    File.WriteAllBytes(Path.Combine(fixtureDir, id + "-v2-default.bin"), schema2DefaultBytes);
    var initial = OnboardSerializer.Prepare(ProjectService.Create(id));
    var legacyDefaultsTransport = new EmulatedDevice(id, initial.Payload);
    await using (var legacyDefaultsClient = new OnboardClient(legacyDefaultsTransport, id))
    {
        var explicitSchema2Defaults = new OnboardImage(id, schema2DefaultBytes, 0, initial.KeyCount, shape.Leds, []);
        await RejectAsync(() => legacyDefaultsClient.ApplyAsync(explicitSchema2Defaults), "Even schema 2 with all defaults requires format support before BEGIN");
        Check(legacyDefaultsTransport.Commands.SequenceEqual(new byte[] { OnboardProtocol.Info }), "Schema 2 defaults on legacy firmware send only INFO");
    }
    File.WriteAllBytes(Path.Combine(fixtureDir, id + "-default.bin"), initial.Payload);

    var transport = new EmulatedDevice(id, initial.Payload);
    await using (var client = new OnboardClient(transport, id))
    {
        var info = await client.GetInfoAsync();
        Check(info.Generation == 0 && info.Persistent, "Handshake capabilities");
        Check((await client.ReadAsync()).SequenceEqual(initial.Payload), "Stable read before upload");
        var result = await client.ApplyAsync(image);
        Check(result.ReadbackVerified && result.Generation == 1 && result.Crc32 == image.Crc32 && transport.Active.SequenceEqual(image.Payload), "Complete transfer, commit and exact readback");
        Check(transport.Commands.Count(c => c == OnboardProtocol.Commit) == 1, "One commit for a successful transfer");
    }
    transport = new EmulatedDevice(id, initial.Payload) { BadWriteAck = true };
    await using (var client = new OnboardClient(transport, id))
    {
        await RejectAsync(() => client.ApplyAsync(image), "Bad offset acknowledgment aborts");
        Check(!transport.Commands.Contains(OnboardProtocol.Commit) && transport.Commands.Last() == OnboardProtocol.Abort && transport.Active.SequenceEqual(initial.Payload), "Failed staging never commits");
    }
    transport = new EmulatedDevice(id, initial.Payload) { LoseCommitReply = true };
    await using (var client = new OnboardClient(transport, id))
    {
        await RejectAsync(() => client.ApplyAsync(image), "Unknown commit outcome reported");
        Check(transport.Commands.Count(c => c == OnboardProtocol.Commit) == 1 && transport.Active.SequenceEqual(image.Payload), "Uncertain commit is not repeated or undone blindly");
    }
    transport = new EmulatedDevice(id, initial.Payload) { WrongSequence = true };
    await using (var client = new OnboardClient(transport, id)) await RejectAsync(() => client.GetInfoAsync(), "Stale sequence response rejected");
    transport = new EmulatedDevice(id, initial.Payload) { Persistent = false };
    await using (var client = new OnboardClient(transport, id))
    {
        await RejectAsync(() => client.ApplyAsync(image), "Missing persistence blocks transfer");
        Check(!transport.Commands.Contains(OnboardProtocol.Begin), "No staging when persistence unavailable");
    }
    transport = new EmulatedDevice(id, initial.Payload) { CorruptRead = true };
    await using (var client = new OnboardClient(transport, id)) await RejectAsync(() => client.ReadAsync(), "Read corruption detected by CRC");
    transport = new EmulatedDevice(id, initial.Payload);
    await using (var client = new OnboardClient(transport, id))
    {
        await RejectAsync(() => client.ApplyAsync(image, expectedGeneration: 999), "Changed generation since UI backup rejected");
        Check(!transport.Commands.Contains(OnboardProtocol.Begin), "Stale backup cannot start an upload");
    }
    transport = new EmulatedDevice(id, initial.Payload) { Generation = uint.MaxValue };
    await using (var client = new OnboardClient(transport, id)) Check((await client.ApplyAsync(image)).Generation == 1, "Generation rollover skips the factory-default generation zero");

    transport = new EmulatedDevice(id, initial.Payload);
    await using (var client = new OnboardClient(transport, id))
    {
        await RejectAsync(() => client.ApplyAsync(effectImage), "Legacy firmware rejects effects before staging");
        Check(transport.Commands.SequenceEqual(new byte[] { OnboardProtocol.Info }) && transport.Active.SequenceEqual(initial.Payload), "Missing effect capability sends INFO only, no BEGIN, WRITE, COMMIT or changed active bytes");
    }
    transport = new EmulatedDevice(id, image.Payload) { EffectsSupported = true };
    await using (var client = new OnboardClient(transport, id))
    {
        var before = await client.GetInfoAsync();
        Check(before.SupportsEffects && before.SchemaVersion == 1 && before.BlobSize == shape.Size, "New firmware reports legacy active schema and size accurately");
        Check((await client.ReadAsync()).SequenceEqual(image.Payload), "Legacy active bytes remain exact on effects-capable firmware");
        var upgraded = await client.ApplyAsync(effectImage, expectedGeneration: before.Generation);
        var after = await client.GetInfoAsync();
        Check(upgraded.ReadbackVerified && after.SchemaVersion == 2 && after.BlobSize == effectShape.Size && transport.Active.SequenceEqual(effectImage.Payload), "Legacy-to-effects upload verifies new active schema, size and every byte");
        var restored = await client.ApplyAsync(image, expectedGeneration: after.Generation);
        var restoredInfo = await client.GetInfoAsync();
        Check(restored.ReadbackVerified && restoredInfo.SchemaVersion == 1 && restoredInfo.BlobSize == shape.Size && transport.Active.SequenceEqual(image.Payload), "Effects firmware accepts legacy configuration restoration without RGB or macro loss");
    }
    foreach (var malformedInfo in new[]
    {
        new EmulatedDevice(id, effectImage.Payload), // Schema 2 without capability.
        new EmulatedDevice(id, initial.Payload) { EffectsSupported = true, ReportedSchema = 2 },
        new EmulatedDevice(id, initial.Payload) { EffectsSupported = true, ReportedSchema = 3 },
        new EmulatedDevice(id, initial.Payload) { ExtraFlags = 32 },
        new EmulatedDevice(id, initial.Payload) { EffectsSupported = true, ReportedStaging = effectShape.Size + 1 }
    })
    {
        await using var client = new OnboardClient(malformedInfo, id);
        await RejectAsync(() => client.GetInfoAsync(), "Inconsistent schema, capacity or unknown capability flags fail the handshake");
        Check(malformedInfo.Commands.SequenceEqual(new byte[] { OnboardProtocol.Info }), "Malformed handshake never starts a configuration write");
    }
    transport = new EmulatedDevice(id, image.Payload) { EffectsSupported = true, ReportedStaging = effectShape.Size };
    await using (var client = new OnboardClient(transport, id)) Check((await client.GetInfoAsync()).StagingBytes == effectShape.Size, "A complete schema 2 staging buffer can coexist with a legacy active configuration");
    transport = new EmulatedDevice(id, effectImage.Payload) { EffectsSupported = true, ReportedSchema = 1, ReportedSize = shape.Size };
    await using (var client = new OnboardClient(transport, id)) await RejectAsync(() => client.ReadAsync(), "Active blob size inconsistency cannot produce a truncated successful read");
    transport = new EmulatedDevice(id, initial.Payload) { EffectsSupported = true };
    await using (var client = new OnboardClient(transport, id))
    {
        await RejectAsync(() => client.ApplyAsync(timingImage), "Missing timing capability rejects before staging");
        Check(transport.Commands.SequenceEqual(new byte[] { OnboardProtocol.Info }), "Timing-incompatible firmware receives INFO only");
        transport.Commands.Clear();
        await RejectAsync(() => client.ReconnectUsbAsync(0, initial.Crc32), "Legacy firmware cannot receive USB reconnect command");
        Check(!transport.Commands.Contains(OnboardProtocol.ReconnectUsb), "No reconnect sent without timing capability");
    }
    transport = new EmulatedDevice(id, initial.Payload) { TimingSupported = true };
    await using (var client = new OnboardClient(transport, id))
    {
        var result = await client.ApplyAsync(timingImage);
        Check(result.ReadbackVerified && !transport.Commands.Contains(OnboardProtocol.ReconnectUsb), "Apply verifies timing persistence without implicit disconnect");
        transport.Commands.Clear();
        var reconnect = await client.ReconnectUsbAsync(result.Generation, result.Crc32);
        Check(reconnect.ActiveIntervalMs == 1 && reconnect.SavedIntervalMs == 8 && reconnect.ReconnectQueued, "Reconnect acknowledgment validates old and saved polling interval");
        Check(transport.Commands.Last() == OnboardProtocol.ReconnectUsb && transport.Commands.Count(c => c == OnboardProtocol.Read) == (timingImage.Payload.Length + 19) / 20 && transport.Commands.Count(c => c == OnboardProtocol.ReconnectUsb) == 1, "USB reconnect occurs once only after complete readback");
        transport.Commands.Clear();
        await RejectAsync(() => client.ReconnectUsbAsync(result.Generation + 1, result.Crc32), "Changed generation prevents USB reconnect");
        Check(transport.Commands.SequenceEqual(new byte[] { OnboardProtocol.Info }), "Stale reconnect expectation sends INFO only");
        transport.Commands.Clear(); transport.ReportedStaging = 1;
        await RejectAsync(() => client.ReconnectUsbAsync(result.Generation, result.Crc32), "Staging in progress prevents USB reconnect");
        Check(!transport.Commands.Contains(OnboardProtocol.ReconnectUsb), "Active staging never disconnects");
    }
    transport = new EmulatedDevice(id, initial.Payload) { TimingSupported = true };
    await using (var client = new OnboardClient(transport, id))
    {
        Check(!(await client.ReconnectUsbAsync(0, initial.Crc32)).ReconnectQueued, "Equal active and saved polling interval does not disconnect");
        transport.BadReconnectAck = true;
        await RejectAsync(() => client.ReconnectUsbAsync(0, initial.Crc32), "Malformed reconnect reply cannot confirm success");
        Check(transport.Commands.Count(c => c == OnboardProtocol.ReconnectUsb) == 2, "Malformed reconnect reply is not retried");
    }
}
foreach (var id in new[] { "pro-iso", "numpad" })
{
    var original = ProjectService.Create(id);
    var originalImage = OnboardSerializer.Prepare(original);
    Check(original.Standby.IsDefault && originalImage.SchemaVersion == 1, "Old/default profiles retain legacy config and disabled standby");
    foreach (var enabled in new[] { false, true })
    foreach (var mode in new[] { 0, 1, 2 })
    foreach (var timeout in new[] { 10, 37, 300, 3600 })
    foreach (var percent in new[] { 1, 10, 37, 100 })
    {
        var p = ProjectService.Clone(original);
        p.Standby = new() { Enabled = enabled, Mode = mode, TimeoutSeconds = timeout, DimBrightnessPercent = percent };
        var encoded = OnboardSerializer.Prepare(p);
        if (p.Standby.IsDefault) { Check(encoded.SchemaVersion == 1, "Default standby does not force a firmware upgrade"); continue; }
        Check(encoded.SchemaVersion == 4 && encoded.RequiresStandby && !encoded.RequiresColorSequences, "Standby schema supports an empty color pool");
        Check(encoded.RequiresScreensaver == (mode == 2), "Screensaver format capability is required even when standby is disabled");
        Check(encoded.Payload.Length == OnboardSerializer.Shape(id, 3).Size && encoded.MacroCapacity == OnboardSerializer.MacroCapacity(id, 3), "Standby fits existing slots and reports exact available macro capacity");
        var decoded = OnboardSerializer.Decode(encoded.Payload);
        Check(decoded.Standby.Enabled == enabled && decoded.Standby.Mode == mode && decoded.Standby.TimeoutSeconds == timeout && decoded.Standby.DimBrightnessPercent == percent, "All standby settings round-trip without rounding");
        Check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(encoded.Payload), "Standby binary round-trip is exact");
    }
    var project = ProjectService.Clone(original);
    project.Standby.Enabled = true; project.Standby.Mode = 1; project.Standby.TimeoutSeconds = 37; project.Standby.DimBrightnessPercent = 17;
    var standbyImage = OnboardSerializer.Prepare(project);
    foreach (var (offset, value) in new[] { (26, 2), (27, 0), (28, 255), (29, 3), (30, 0), (30, 101), (31, 1) })
    {
        var invalid = standbyImage.Payload.ToArray(); invalid[offset] = (byte)value;
        Reject(() => OnboardSerializer.Decode(invalid), "Invalid standby header fails before any use");
    }
    project.Layers[1].LightingEffect.Colors = ["#FF2233", "#112233"];
    project.Layers[1].LightingEffect.Mode = 2;
    var combined = OnboardSerializer.Prepare(project);
    Check(combined.RequiresStandby && combined.RequiresColorSequences && OnboardSerializer.Prepare(OnboardSerializer.Decode(combined.Payload)).Payload.SequenceEqual(combined.Payload), "Standby and custom color sequences coexist losslessly");
    Directory.CreateDirectory("artifacts/onboard-fixtures");
    File.WriteAllBytes($"artifacts/onboard-fixtures/{id}-v4-standby.bin", standbyImage.Payload);
    File.WriteAllBytes($"artifacts/onboard-fixtures/{id}-v4-sequences.bin", combined.Payload);
    var old = new EmulatedDevice(id, originalImage.Payload) { EffectsSupported = true, TimingSupported = true, ExtraFlags = 96 };
    await using (var client = new OnboardClient(old, id))
    {
        await RejectAsync(() => client.ApplyAsync(standbyImage), "Firmware 0.5 without standby cannot accept schema4");
        Check(old.Commands.SequenceEqual(new byte[] { OnboardProtocol.Info }) && old.Active.SequenceEqual(originalImage.Payload), "Missing standby capability sends INFO only and preserves all settings");
    }
    var current = new EmulatedDevice(id, originalImage.Payload) { EffectsSupported = true, TimingSupported = true, ExtraFlags = 224 };
    await using (var client = new OnboardClient(current, id))
    {
        Check((await client.GetInfoAsync()).SupportsStandby, "Standby capability is independent of active legacy schema");
        var result = await client.ApplyAsync(combined);
        Check(result.ReadbackVerified && current.Active.SequenceEqual(combined.Payload), "Schema4 upload commits once and reads back exactly");
        Check((await client.ReadAsync()).SequenceEqual(combined.Payload), "Stored schema4 reads back after reopening workflow");
        var disabled = await client.ApplyAsync(originalImage);
        Check(disabled.ReadbackVerified && current.Active.SequenceEqual(originalImage.Payload), "Disabling standby accepts original legacy profile without altering other features");
        Check(!current.Commands.Contains(OnboardProtocol.GetExtensions), "Existing off/dim standby profiles do not require a screensaver query");
    }
    foreach (var enabled in new[] { false, true })
    {
        var screensaver = ProjectService.Clone(project);
        screensaver.Standby.Mode = 2; screensaver.Standby.Enabled = enabled;
        var image = OnboardSerializer.Prepare(screensaver);
        Check(image.RequiresScreensaver, "Enabled and disabled screensaver selections require the byte-29 capability");
        var cloned = ProjectService.Clone(screensaver);
        Check(cloned.Standby.Mode == 2 && cloned.Standby.Enabled == enabled && OnboardSerializer.Prepare(cloned).Payload.SequenceEqual(image.Payload), "Screensaver clone preserves the entire profile");
        var isolated = Path.Combine(Path.GetTempPath(), "inglorious-screensaver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        try
        {
            var profilePath = Path.Combine(isolated, "profile.inglorious.json");
            ProjectService.Save(profilePath, screensaver);
            var loaded = ProjectService.Load(profilePath);
            Check(loaded.Standby.Mode == 2 && loaded.Standby.Enabled == enabled && OnboardSerializer.Prepare(loaded).Payload.SequenceEqual(image.Payload), "Saved and imported screensaver JSON preserves bindings, macros, layers and lighting exactly");
        }
        finally { Directory.Delete(isolated, true); }
        File.WriteAllBytes($"artifacts/onboard-fixtures/{id}-v4-screensaver{(enabled ? "" : "-disabled")}.bin", image.Payload);

        var target = new EmulatedDevice(id, originalImage.Payload) { EffectsSupported = true, TimingSupported = true, ExtraFlags = 224 };
        await using (var client = new OnboardClient(target, id))
        {
            var extensions = await client.GetExtensionsAsync();
            Check(extensions.SupportsScreensaver && extensions.SupportsLightingTest && extensions.LightingTestDurationMs == 21000, "Firmware 0.9 announces independent screensaver and lighting-test capabilities");
            target.Commands.Clear();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var result = await client.ApplyAsync(image);
                Check(result.ReadbackVerified && target.Active.SequenceEqual(image.Payload), "Screensaver upload commits and reads back the entire configuration");
            }
            Check(target.Commands.Count(c => c == OnboardProtocol.GetExtensions) == 2 && target.Commands.Count(c => c == OnboardProtocol.Begin) == 2, "Every upload freshly queries extensions instead of reusing a cached capability");
            Check(target.Commands.Select((c, i) => (c, i)).Where(x => x.c == OnboardProtocol.Begin).All(x => x.i > 0 && target.Commands[x.i - 1] == OnboardProtocol.GetExtensions), "Fresh capability query immediately precedes every BEGIN and all configuration WRITE commands");
            target.ExtensionFlags = 1; target.Commands.Clear();
            try { await client.ApplyAsync(image); throw new Exception("Stale capability incorrectly accepted"); }
            catch (InvalidDataException ex) { Check(ex.Message == L.T("Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen."), "Loss of previously advertised capability gives an actionable update message"); }
            Check(target.Commands.Contains(OnboardProtocol.GetExtensions) && !target.Commands.Any(c => c is OnboardProtocol.Begin or OnboardProtocol.Write or OnboardProtocol.Commit or OnboardProtocol.Abort) && target.Generation == 2, "Old cached capability cannot start or abort a new upload");
        }
        foreach (var failure in new[] { "missing", "unsupported", "busy", "io", "unknown-flag", "malformed", "no-standby" })
        {
            target = new EmulatedDevice(id, originalImage.Payload) { EffectsSupported = true, TimingSupported = true, ExtraFlags = 224 };
            switch (failure)
            {
                case "missing": target.ExtensionFlags = 1; break;
                case "unsupported": target.ExtensionStatus = 9; break;
                case "busy": target.ExtensionStatus = 3; break;
                case "io": target.ExtensionThrows = true; break;
                case "unknown-flag": target.ExtensionFlags = 7; break;
                case "malformed": target.ExtensionPayloadOverride = [1, 0, 3, 0]; break;
                case "no-standby": target.ExtraFlags = 96; break;
            }
            await using var client = new OnboardClient(target, id);
            try { await client.ApplyAsync(image); throw new Exception("Unsafe screensaver upload accepted: " + failure); }
            catch (InvalidDataException ex) { Check(ex.Message == L.T("Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen."), "Every unavailable screensaver contract fails with the fixed update message: " + failure); }
            Check(!target.Commands.Any(c => c is OnboardProtocol.Begin or OnboardProtocol.Write or OnboardProtocol.Commit or OnboardProtocol.Abort) && target.Active.SequenceEqual(originalImage.Payload) && target.Generation == 0, "Rejected screensaver upload sends no persistent or staging command: " + failure);
        }
    }
    var invalidCapability = new EmulatedDevice(id, originalImage.Payload) { ExtraFlags = 128 };
    await using (var client = new OnboardClient(invalidCapability, id)) await RejectAsync(() => client.GetInfoAsync(), "Standby capability without required earlier formats is rejected");
    project.Standby = null!;
    Reject(() => OnboardSerializer.Prepare(project), "Null standby settings from imported profiles are rejected");
}
var tooLarge = ProjectService.Create("numpad");
for (var i = 0; i < 7; i++) tooLarge.Macros.Add(new() { Id = "m" + i, Name = "Macro " + i, Steps = Enumerable.Range(0, 128).Select(n => new MacroStep { Kind = n % 2 == 0 ? "down" : "up", Key = "KC_A" }).ToList() });
Reject(() => OnboardSerializer.Prepare(tooLarge), "Shared macro capacity rejects without truncation");
if (args.Contains("--inventory")) Console.WriteLine("Actual INGLORIOUS descriptor matches: " + OnboardClient.FindDevices().Count + " (no reports sent)");
if (args.Contains("--slow-commit"))
{
    var image = OnboardSerializer.Prepare(ProjectService.Create("numpad"));
    var delayed = new EmulatedDevice("numpad", image.Payload) { CommitDelayMs = 23_000 };
    await using var client = new OnboardClient(delayed, "numpad");
    var result = await client.ApplyAsync(image);
    Check(result.ReadbackVerified && result.Generation == 1 && delayed.Commands.Count(c => c == OnboardProtocol.Commit) == 1, "Slow internal persistence beyond the old 20-second limit still completes once and verifies readback");
    Console.WriteLine("PASS delayed COMMIT: 23 seconds, one COMMIT, complete readback; emulated transport only.");
}
Console.WriteLine($"PASS {checks} onboard checks. Emulated transport only; no physical firmware claim.");

sealed class EmulatedDevice(string id, byte[] initial) : IOnboardTransport
{
    public byte[] Active = initial.ToArray();
    private byte[]? _stage;
    private uint _generation;
    public uint Generation { get => _generation; set => _generation = value; }
    public List<byte> Commands { get; } = [];
    public bool BadWriteAck, LoseCommitReply, WrongSequence, CorruptRead;
    public bool Persistent = true;
    public bool EffectsSupported, TimingSupported, BadReconnectAck;
    public int ActiveIntervalMs = 1;
    public byte ExtraFlags;
    public ushort ExtensionFlags = 3;
    public byte ExtensionStatus;
    public bool ExtensionThrows;
    public byte[]? ExtensionPayloadOverride;
    public int? ReportedSchema, ReportedSize, ReportedStaging;
    public int CommitDelayMs;
    public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var command = packet[3]; Commands.Add(command);
        var p = packet.AsSpan(8, packet[6]); byte[] response;
        switch (command)
        {
            case OnboardProtocol.Info:
                var s = OnboardSerializer.Shape(id);
                response = new byte[24]; response[0] = s.Board; response[1] = (byte)s.Rows; response[2] = (byte)s.Cols; response[3] = 4; response[4] = (byte)s.Leds; response[5] = 32; response[6] = 128;
                response[7] = (byte)((Persistent ? 1 : 0) | (id == "numpad" ? 2 : 0) | (EffectsSupported ? 8 : 0) | (TimingSupported ? 16 : 0) | (ReportedStaging > 0 ? 4 : 0) | ExtraFlags);
                Put16(response, 8, ReportedSize ?? Active.Length); Put16(response, 10, OnboardSerializer.MacroCapacity(id, Get16(Active, 4))); Put32(response, 12, _generation); Put32(response, 16, OnboardProtocol.Crc32(Active)); Put16(response, 20, ReportedStaging ?? 0); Put16(response, 22, ReportedSchema ?? Get16(Active, 4)); break;
            case OnboardProtocol.Read:
                var offset = Get16(p, 0); var count = p[2]; response = new byte[4 + count]; Put16(response, 0, offset); response[2] = count; Active.AsSpan(offset, count).CopyTo(response.AsSpan(4)); if (CorruptRead) response[4] ^= 1; break;
            case OnboardProtocol.Begin:
                _stage = new byte[Get16(p, 0)]; response = new byte[2]; break;
            case OnboardProtocol.Write:
                offset = Get16(p, 0); count = p[2]; p[4..].CopyTo(_stage!.AsSpan(offset)); response = new byte[2]; Put16(response, 0, offset + count + (BadWriteAck ? 1 : 0)); break;
            case OnboardProtocol.Commit:
                Active = _stage!; _stage = null; _generation = _generation == uint.MaxValue ? 1 : _generation + 1; if (LoseCommitReply) throw new IOException("Simulated disconnected device after persistence");
                response = new byte[8]; Put32(response, 0, _generation); Put32(response, 4, OnboardProtocol.Crc32(Active)); break;
            case OnboardProtocol.Abort: _stage = null; response = []; break;
            case OnboardProtocol.GetExtensions:
                if (ExtensionThrows) throw new IOException("Simulated extension query failure");
                response = ExtensionPayloadOverride ?? new byte[8];
                if (ExtensionPayloadOverride is null) { Put16(response, 0, 1); Put16(response, 2, ExtensionFlags); Put16(response, 4, (ExtensionFlags & 1) != 0 ? 21000 : 0); }
                break;
            case OnboardProtocol.ReconnectUsb:
                var savedInterval = OnboardSerializer.Decode(Active).DeviceSettings.UsbPollingIntervalMs;
                response = [(byte)ActiveIntervalMs, (byte)savedInterval, (byte)(ActiveIntervalMs != savedInterval ? 1 : 0), (byte)(BadReconnectAck ? 1 : 0)]; break;
            default: throw new Exception("Unexpected command");
        }
        var reply = OnboardProtocol.Packet(command is OnboardProtocol.ReconnectUsb or OnboardProtocol.GetExtensions ? OnboardProtocol.Info : command, WrongSequence ? (byte)(packet[4] + 1) : packet[4], response);
        reply[3] = command;
        if (command == OnboardProtocol.GetExtensions) reply[5] = ExtensionStatus;
        return command == OnboardProtocol.Commit && CommitDelayMs > 0 ? DelayedReply(reply, ct) : Task.FromResult(reply);
    }
    private async Task<byte[]> DelayedReply(byte[] reply, CancellationToken ct) { await Task.Delay(CommitDelayMs, ct); return reply; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static int Get16(ReadOnlySpan<byte> b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b[i..]);
    private static void Put16(byte[] b, int i, int value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i), (ushort)value);
    private static void Put32(byte[] b, int i, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i), value);
}
