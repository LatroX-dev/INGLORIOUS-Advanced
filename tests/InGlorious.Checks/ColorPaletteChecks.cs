using System.Text;
using InGlorious.Core;

internal static class ColorPaletteChecks
{
    public static int Run(string temporaryDirectory)
    {
        var checks = 0;
        void Require(bool condition) { if (!condition) throw new Exception("Color palette assertion failed."); checks++; }
        void Rejected(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { checks++; return; }
            throw new Exception("Invalid palette was accepted.");
        }
        var directory = Path.Combine(temporaryDirectory, "custom-palette");
        var path = Path.Combine(directory, "custom-colors.json");
        var missing = ColorPaletteStore.Load(path);
        Require(missing.Error is null && missing.Colors.SequenceEqual(ColorPaletteStore.DefaultColors));
        Require(missing.Colors.Count == 16);
        Require(!Directory.Exists(directory));
        Require(ColorPaletteStore.NormalizeColor(" abcDeF ") == "#ABCDEF");
        var saved = ColorPaletteStore.Save(path, [" #aabbcc ", "AABBCC", "#000000", "ffffff"]);
        Require(saved.SequenceEqual(new[] { "#AABBCC", "#000000", "#FFFFFF" }));
        var loaded = ColorPaletteStore.Load(path);
        Require(loaded.Error is null && loaded.Colors.SequenceEqual(saved));
        Require(Directory.GetFiles(directory, "*.tmp").Length == 0);
        var before = File.ReadAllBytes(path);
        foreach (var invalid in new[] { "#FFF", "#11223344", "red", "#GG0000", "", "#12345\n6" })
        {
            Rejected(() => ColorPaletteStore.Save(path, [invalid]));
            Require(File.ReadAllBytes(path).SequenceEqual(before));
        }
        var colors = Enumerable.Range(0, 32).Select(i => $"#{i:X6}").ToArray();
        Require(ColorPaletteStore.Save(path, colors).Count == 32);
        before = File.ReadAllBytes(path);
        Rejected(() => ColorPaletteStore.Save(path, colors.Append("#FFFFFF").ToArray()));
        Require(File.ReadAllBytes(path).SequenceEqual(before));
        Require(ColorPaletteStore.Save(path, colors.Concat(colors).ToArray()).Count == 32);
        ColorPaletteStore.Save(path, Array.Empty<string>());
        Require(ColorPaletteStore.Load(path).Colors.Count == 0);

        // V1 custom entries come first and survive migration; reading does not rewrite the source.
        File.WriteAllText(path, "{\"schemaVersion\":1,\"colors\":[\"#123456\",\"#ffffff\"]}");
        before = File.ReadAllBytes(path);
        var legacy = ColorPaletteStore.Load(path);
        Require(legacy.Error is null && legacy.Colors[0] == "#123456" && legacy.Colors[1] == "#FFFFFF");
        Require(legacy.Colors.Count == 17 && legacy.Colors.Distinct().Count() == legacy.Colors.Count);
        Require(File.ReadAllBytes(path).SequenceEqual(before));
        ColorPaletteStore.Remove(path, "#FFFFFF");
        Require(!ColorPaletteStore.Load(path).Colors.Contains("#FFFFFF"));
        Require(File.ReadAllText(path).Contains("\"schemaVersion\": 2"));
        File.WriteAllText(path, "{\"schemaVersion\":1,\"colors\":[" + string.Join(",", colors.Select(c => "\"" + c + "\"")) + "]}");
        Require(ColorPaletteStore.Load(path).Colors.SequenceEqual(colors)); // Never trim old custom entries for defaults.
        ColorPaletteStore.Save(path, []);
        var viewOne = ColorPaletteStore.Load(path); var viewTwo = ColorPaletteStore.Load(path);
        ColorPaletteStore.Add(path, "#112233"); ColorPaletteStore.Add(path, "#445566");
        Require(viewOne.Colors.Count == 0 && viewTwo.Colors.Count == 0);
        Require(ColorPaletteStore.Load(path).Colors.SequenceEqual(new[] { "#112233", "#445566" }));
        ColorPaletteStore.Remove(path, "#112233");
        Require(ColorPaletteStore.Load(path).Colors.SequenceEqual(new[] { "#445566" }));
        ColorPaletteStore.Remove(path, "#445566");
        Require(ColorPaletteStore.Load(path).Colors.Count == 0); // Deleted defaults do not reappear in V2.
        ColorPaletteStore.Save(path, colors);
        Require(ColorPaletteStore.Add(path, colors[0]).Count == 32);
        before = File.ReadAllBytes(path);
        Rejected(() => ColorPaletteStore.Add(path, "#ABCDEF"));
        Require(File.ReadAllBytes(path).SequenceEqual(before));

        foreach (var broken in new[]
        {
            "{", "null", "{}", "{\"schemaVersion\":3,\"colors\":[]}",
            "{\"schemaVersion\":2,\"schemaVersion\":2,\"colors\":[]}",
            "{\"schemaVersion\":1,\"colors\":null}",
            "{\"schemaVersion\":1,\"colors\":[\"#GG0000\"]}",
            "{\"schemaVersion\":1,\"colors\":[],\"unexpected\":true}",
            new string(' ', 16385)
        })
        {
            File.WriteAllText(path, broken, new UTF8Encoding(false));
            before = File.ReadAllBytes(path);
            var bad = ColorPaletteStore.Load(path);
            Require(bad.Error is not null && bad.Colors.Count == 0);
            Rejected(() => ColorPaletteStore.Save(path, ["#112233"]));
            Rejected(() => ColorPaletteStore.Add(path, "#112233"));
            Rejected(() => ColorPaletteStore.Remove(path, "#112233"));
            Require(File.ReadAllBytes(path).SequenceEqual(before));
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0);
        }
        return checks;
    }
}
