using System.Security.Cryptography;
using System.Text.Json;
using InGlorious.Core;

internal static class KnobPressFixtureExport
{
    public static void Write(string directory)
    {
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.Combine(root, "src", "InGlorious.Core")) || Directory.Exists(directory))
            throw new InvalidOperationException("Use a new fixture directory inside the project.");
        Directory.CreateDirectory(directory);
        var entries = new List<object>();
        int[] ranks = [40319, 1, 19873, 0];
        string[] pressBindings = ["KC_MUTE", "KC_NO", "KC_MPLY", "KC_TRNS", "IG_ZOOM_IN_MAC", "MS_WHLL", "IG_TAB_NEXT", "IG_UNDO"];
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards.Where(board => board.HasEncoder))
        {
            var project = ProjectService.Create(board.DeviceId);
            for (var index = 0; index < 4; index++)
            {
                var layer = project.Layers[index];
                KnobPresets.Apply(layer, "volume");
                layer.KnobCycleEnabled = index < 2;
                layer.KnobCycleMask = 255;
                layer.KnobPressByFunction = index is 0 or 2;
                layer.KnobCycleOrder = KnobPresets.DecodeOrder(ranks[index]).ToList();
                for (var bitIndex = 0; bitIndex < 8; bitIndex++)
                    KnobPresets.SetPressBinding(layer, 1 << bitIndex, pressBindings[bitIndex]);
            }
            var image = OnboardSerializer.Prepare(project);
            if (image.SchemaVersion != 7 || !OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.SequenceEqual(image.Payload))
                throw new InvalidOperationException("Schema-7 fixture does not round-trip exactly.");
            var file = board.DeviceId + "-knob-press.bin";
            File.WriteAllBytes(Path.Combine(directory, file), image.Payload);
            entries.Add(new
            {
                deviceId = board.DeviceId, boardId = board.BoardId, file,
                bytes = image.Payload.Length, schema = image.SchemaVersion,
                sha256 = Convert.ToHexString(SHA256.HashData(image.Payload)),
                layers = project.Layers.Select((layer, index) => new
                {
                    index, cycle = layer.KnobCycleEnabled, mac = layer.KnobCycleMacZoom,
                    mask = layer.KnobCycleMask, step = layer.KnobStep, undoLayout = layer.KnobUndoLayout,
                    order = layer.KnobCycleOrder, orderRank = ranks[index],
                    pressByFunction = layer.KnobPressByFunction,
                    presses = Enumerable.Range(0, 8).Select(bitIndex => new
                    { bit = 1 << bitIndex, binding = pressBindings[bitIndex] }).ToArray()
                }).ToArray()
            });
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(
            new { schema = 7, synthetic = true, hardwareAccess = false, fixtures = entries }, ProjectService.JsonOptions));
        Console.WriteLine($"PASS exported {entries.Count} synthetic schema-7 knob-press fixtures; no hardware or user data.");
    }
}
