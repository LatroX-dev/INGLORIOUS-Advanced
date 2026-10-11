using System.Security.Cryptography;
using System.Text.Json;
using InGlorious.Core;

internal static class ExperimentalBoardChecks
{
    public static async Task Run(Action<bool, string> check, string[] args)
    {
        static void Reject(Action action, Action<bool, string> check, string label)
        { try { action(); } catch (InvalidDataException) { check(true, label); return; } throw new Exception(label); }
        var gmmk3Only = Array.IndexOf(args, "--gmmk3-fixtures") >= 0;
        var exportIndex = Array.IndexOf(args, gmmk3Only ? "--gmmk3-fixtures" : "--port-fixtures");
        var output = exportIndex >= 0 && exportIndex + 1 < args.Length ? Path.GetFullPath(args[exportIndex + 1]) : null;
        if (output is not null) Directory.CreateDirectory(output);
        var manifest = new List<object>();
        foreach (var (id, legacy, extended) in new[] {
            ("gmmk3-65-ansi",5164,6216), ("gmmk3-65-iso",5176,6240),
            ("gmmk3-75-ansi",5356,6600), ("gmmk3-75-iso",5368,6624),
            ("gmmk3-100-ansi",5644,7176), ("gmmk3-100-iso",5656,7200) })
        {
            var shape = BoardDescriptorRegistry.Get(id);
            check(shape.PayloadCapacity == 7648 && OnboardSerializer.Shape(id).Size == legacy, "GMMK3 exact legacy size and larger descriptor capacity: " + id);
            for (var schema = 2; schema <= 4; schema++) check(OnboardSerializer.Shape(id, schema).Size == extended, "GMMK3 exact extended schema size: " + id);
            Reject(() => OnboardSerializer.ValidateShape(shape with { PayloadCapacity = extended - 1 }, 4), check, "Insufficient slot capacity is rejected before encoding");
            Reject(() => OnboardSerializer.ValidateShape(shape with { LegacySize = legacy - 1 }, 1), check, "Incorrect descriptor dimensions cannot truncate a configuration");
            Reject(() => OnboardSerializer.ValidateShape(shape with { Rows = 0 }, 1), check, "Zero-dimensional board rejected");
        }
        check(BoardDescriptorRegistry.ProtocolBoards.Where(board => board.BoardId < 10).All(board => board.PayloadCapacity == 7136), "Existing nine capacities remain unchanged");
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var test = new LightingPatternTransport(board);
            await using var client = new OnboardClient(test, board.DeviceId);
            var status = await client.StartLightingTestAsync();
            check(status.Active && status.Pattern == (board.HasSlider ? 2 : 1) && status.RemainingMs == 21000,
                "RAW12 pattern is keyboard1 or Numpad2, never a candidate board ID: " + board.DeviceId);
            foreach (var wrong in new byte[] { 0, 1, 2, board.BoardId, 255 }.Distinct().Where(value => value != board.LightingTestPattern))
            {
                test.Pattern = wrong;
                var rejected = false;
                try { await client.GetLightingTestStatusAsync(); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "RAW12 rejects a foreign pattern identity: " + board.DeviceId + "/" + wrong);
            }
            var stopped = await client.StopLightingTestAsync();
            check(!stopped.Active && stopped.Pattern == 0 && stopped.RemainingMs == 0, "Stopped test has no active pattern: " + board.DeviceId);
        }
        foreach (var board in BoardDescriptorRegistry.Experimental)
        {
            var model = DeviceCatalog.Get(board.DeviceId);
            check(model.SupportsOnboardConfiguration && model.IsExperimentalFirmware && !model.HasApprovedFirmwareManagement && !model.IsOfflineOnly, "Candidate configuration support is separate from flashing: " + board.DeviceId);
            check(model.HasEncoder == board.HasEncoder && model.HasSlider == board.HasSlider, "Physical control metadata agrees: " + board.DeviceId);
            check(board.LegacySize == 32 + 4 * board.Rows * board.Cols * 2 + 16 + 4 * board.LedCount * 3 + 128 + 768 * 4, "Wire size follows explicit matrix/LED dimensions: " + board.DeviceId);
            var baseline = ProjectService.Create(board.DeviceId);
            var configured = ProjectService.Clone(baseline);
            configured.Macros.Add(new() { Id = "fixture", Name = "Fixture", Steps = [new() { Kind = "down", Key = "KC_LCTL" }, new() { Kind = "down", Key = "KC_C" }, new() { Kind = "delay", DelayMs = 17 }, new() { Kind = "up", Key = "KC_C" }, new() { Kind = "up", Key = "KC_LCTL" }] });
            for (var layer = 0; layer < 4; layer++)
            {
                var profile = configured.Layers[layer];
                foreach (var (key, index) in model.Keys.Select((key, index) => (key, index)))
                {
                    profile.Keys[key.Id].Color = $"#{(layer * 61 + index * 7) % 256:X2}{(index * 13 + 49) % 256:X2}{(255 - layer * 41 + index * 3) % 256:X2}";
                    if (key.IsEncoder) profile.Keys[key.Id].Binding = key.Id == "encoder:ccw" ? "KC_VOLD" : "KC_VOLU";
                }
                var physical = model.Keys.Where(key => !key.IsEncoder).ToArray();
                profile.Keys[physical[0].Id].Binding = "MACRO:fixture";
                profile.Keys[physical[1].Id].Binding = $"TG({layer})";
                profile.Keys[physical[2].Id].Binding = $"MO({(layer + 1) % 4})";
                foreach (var led in model.NonKeyLedIndices) profile.ExtraLedColors[led] = $"#{(led * 5) % 256:X2}{(layer * 61 + 15) % 256:X2}{(led * 11) % 256:X2}";
            }
            var effects = ProjectService.Clone(configured);
            effects.DeviceSettings.UsbPollingIntervalMs = 4; effects.DeviceSettings.DebounceMs = 9;
            effects.Standby = new() { Enabled = true, TimeoutSeconds = 45, Mode = 2, DimBrightnessPercent = 17 };
            for (var layer = 0; layer < 4; layer++)
            {
                effects.Layers[layer].SynchronizeSides = layer % 2 == 0;
                effects.Layers[layer].LightingEffect = new() { Mode = 2, Speed = 111 + layer, Brightness = 203, Target = 3, Colors = ["#0033FF", "#FFD400"] };
                effects.Layers[layer].LedEffects[model.Keys.First(key => key.LedIndex.HasValue).LedIndex!.Value] = new() { Mode = 13, Speed = 177, Brightness = 189, Reverse = true, Colors = ["#E62B45", "#62F6FF"] };
            }
            foreach (var badge in model.BadgeLedIndices)
            {
                effects.Layers[1].ExtraLedColors[badge] = "#14C8E6";
                effects.Layers[1].LedEffects[badge] = new() { Mode = 2, Speed = 77, Brightness = 219, Colors = ["#0033FF", "#FFD400"] };
            }
            foreach (var (name, project, schema) in new[] { ("default", baseline, 1), ("configured", configured, 1), ("v4-configured", effects, 4) })
            {
                var image = OnboardSerializer.Prepare(project);
                var decoded = OnboardSerializer.Decode(image.Payload);
                check(image.SchemaVersion == schema && image.Payload[6] == board.BoardId && image.UsedBytes <= image.CapacityBytes, "Exact candidate board/schema/capacity: " + board.DeviceId + "/" + name);
                check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(image.Payload), "Every configured byte survives candidate encode/decode: " + board.DeviceId + "/" + name);
                check(decoded.Layers.All(layer => layer.Keys.Keys.Order().SequenceEqual(model.Keys.Select(key => key.Id).Order())), "No matrix or encoder keys lost: " + board.DeviceId + "/" + name);
                if (!board.HasEncoder)
                {
                    var at = 32 + 4 * board.Rows * board.Cols * 2;
                    check(image.Payload.AsSpan(at, 16).IndexOfAnyExcept((byte)0) < 0, "GMMK2 reserved encoder bytes remain zero");
                    var bad = image.Payload.ToArray(); bad[at] = 4;
                    Reject(() => OnboardSerializer.Decode(bad), check, "GMMK2 rejects hidden encoder bindings instead of silently dropping them");
                }
                var transport = new EmulatedDevice(board.DeviceId, OnboardSerializer.Prepare(baseline).Payload)
                { EffectsSupported = true, TimingSupported = true, ExtraFlags = 32 | 64 | 128 };
                await using (var client = new OnboardClient(transport, board.DeviceId))
                {
                    var info = await client.GetInfoAsync();
                    check(info.DeviceId == board.DeviceId && info.Persistent, "Experimental INFO handshake is bound to its exact board");
                    var result = await client.ApplyAsync(image);
                    check(result.ReadbackVerified && transport.Active.SequenceEqual(image.Payload) && transport.Commands.Count(command => command == OnboardProtocol.Commit) == 1, "Candidate upload commits once and verifies complete readback: " + board.DeviceId + "/" + name);
                }
                if (output is not null && (gmmk3Only ? board.BoardId >= 10 : board.BoardId < 10))
                {
                    var file = board.DeviceId + "-" + name + ".bin"; File.WriteAllBytes(Path.Combine(output, file), image.Payload);
                    manifest.Add(new { file, deviceId = board.DeviceId, boardId = board.BoardId, board.Rows, board.Cols, board.LedCount, schema, capacity = image.CapacityBytes, bytes = image.UsedBytes, sha256 = Convert.ToHexString(SHA256.HashData(image.Payload)), crc32 = image.Crc32, macroEvents = image.MacroEvents, macroCapacity = image.MacroCapacity });
                }
            }
            var corruptBoard = OnboardSerializer.Prepare(baseline).Payload; corruptBoard[6] = 255;
            Reject(() => OnboardSerializer.Decode(corruptBoard), check, "Unknown board cannot alias a candidate");
            var wrongInfo = new EmulatedDevice("pro-iso", OnboardSerializer.Prepare(ProjectService.Create("pro-iso")).Payload);
            await using var mismatched = new OnboardClient(wrongInfo, board.DeviceId);
            var rejected = false;
            try { await mismatched.ApplyAsync(OnboardSerializer.Prepare(effects)); } catch (InvalidDataException) { rejected = true; }
            check(rejected && !wrongInfo.Commands.Contains(OnboardProtocol.Begin), "A stable or different board cannot receive candidate configuration staging");
        }
        if (output is not null) File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private sealed class LightingPatternTransport(BoardDescriptor board) : IOnboardTransport
    {
        private readonly EmulatedDevice _inner = new(board.DeviceId, OnboardSerializer.Prepare(ProjectService.Create(board.DeviceId)).Payload);
        public byte Pattern = board.LightingTestPattern;
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
        {
            if (packet[3] != OnboardProtocol.LightingTest) return _inner.ExchangeAsync(packet, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] payload = packet[8] == 2 ? [0, 0, 0, 0] : [1, Pattern, 8, 82]; // 21000ms, little-endian.
            var reply = OnboardProtocol.Packet(OnboardProtocol.Info, packet[4], payload);
            reply[3] = OnboardProtocol.LightingTest;
            return Task.FromResult(reply);
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
