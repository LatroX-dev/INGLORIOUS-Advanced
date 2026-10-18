using System.Text.Json;
using InGlorious.Core;

internal static class KnobFixtureExport
{
    public static void Write(string directory, bool ordered = false)
    {
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.Combine(root, "src", "InGlorious.Core")) || Directory.Exists(directory))
            throw new InvalidOperationException("Use a new fixture directory inside the project.");
        Directory.CreateDirectory(directory);
        var entries = new List<object>();
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards.Where(board => board.HasEncoder))
        foreach (var preset in KnobPresets.All)
        {
            var project = ProjectService.Create(board.DeviceId);
            var bit = preset.Id switch
            {
                "volume" => 1, "brightness" => 2, "tracks" => 4, "scroll" => 8,
                "zoom" or "zoom-mac" => 16, "hscroll" => 32, "tabs" => 64,
                "undo" or "undo-mac" => 128, _ => throw new InvalidOperationException("Unknown fixture preset.")
            };
            int[] masks = [31, 255, bit, 224], steps = [1, 2, 3, 5], layouts = [0, 1, 2, 0];
            for (var index = 0; index < 4; index++)
            {
                var layer = project.Layers[index];
                layer.KnobCycleEnabled = index < 3;
                KnobPresets.Apply(layer, preset.Id);
                layer.KnobCycleMask = masks[index];
                layer.KnobStep = steps[index];
                layer.KnobUndoLayout = layouts[index];
                if (ordered) layer.KnobCycleOrder = KnobPresets.DecodeOrder(new[] { 40319, 1, 19873, 0 }[index]).ToList();
            }
            var image = OnboardSerializer.Prepare(project);
            if (image.SchemaVersion != (ordered ? 6 : 5) || !OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.SequenceEqual(image.Payload))
                throw new InvalidOperationException("Fixture does not round-trip exactly.");
            var file = board.DeviceId + "-" + preset.Id + ".bin";
            File.WriteAllBytes(Path.Combine(directory, file), image.Payload);
            entries.Add(new
            {
                deviceId = board.DeviceId, boardId = board.BoardId, preset = preset.Id,
                file, bytes = image.Payload.Length, schema = image.SchemaVersion,
                counterClockwise = preset.CounterClockwise, clockwise = preset.Clockwise,
                layers = project.Layers.Select((layer, index) => new
                {
                    index, cycle = layer.KnobCycleEnabled, mac = layer.KnobCycleMacZoom,
                    mask = layer.KnobCycleMask, step = layer.KnobStep, undoLayout = layer.KnobUndoLayout,
                    order = layer.KnobCycleOrder, orderRank = KnobPresets.EncodeOrder(layer.KnobCycleOrder)
                }).ToArray()
            });
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { schema = ordered ? 6 : 5, synthetic = true, hardwareAccess = false, fixtures = entries }, ProjectService.JsonOptions));
        Console.WriteLine($"PASS exported {entries.Count} synthetic schema-{(ordered ? 6 : 5)} knob fixtures; no hardware or user data.");
    }
}
