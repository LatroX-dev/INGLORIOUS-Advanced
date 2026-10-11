using InGlorious.Core;
using System.Text.Json;

var checks = 0;
void Check(bool condition, string message) { ++checks; if (!condition) throw new InvalidOperationException(message); }
if (args.Length != 1) throw new ArgumentException("Pass the locally generated firmware oracle binary.");
using (var reader = new BinaryReader(File.OpenRead(args[0])))
{
    var hits = new PreviewHit[8]; var vectors = 0;
    while (reader.BaseStream.Position < reader.BaseStream.Length)
    {
        var effect = new PreviewEffect(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        var basis = new PreviewRgb(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        var led = new PreviewLed(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte() != 0, null);
        var time = reader.ReadUInt32(); var count = reader.ReadByte();
        for (var i = 0; i < hits.Length; ++i) hits[i] = new(reader.ReadUInt32(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        var expected = new PreviewRgb(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        var actual = LightingPreviewEngine.Pixel(effect, basis, led, time, hits.AsSpan(0, Math.Min(count, (byte)8)));
        Check(actual == expected, $"Firmware mismatch vector {vectors}, mode {effect.Mode}, actual {actual}, expected {expected}");
        ++vectors;
    }
    Check(vectors == 40320, "Complete independently generated oracle corpus was compared");
}
foreach (var device in DeviceCatalog.All)
{
    var project = ProjectService.Create(device.Id); var layer = project.Layers[0];
    var leds = LightingPreviewEngine.Layout(device); var frame = new PreviewRgb[device.LedCount];
    Check(leds.Count == device.LedCount && leds.Select(l => l.Index).Distinct().Count() == device.LedCount, "Every physical LED has one coordinate");
    Check(leds.Where(l => l.Side).Select(l => (int)l.Index).Order().SequenceEqual(device.LeftSideLedIndices.Concat(device.RightSideLedIndices).Order()), "Underglow flag agrees with side LEDs");
    foreach (var led in leds)
    {
        if (led.KeyId is not null) layer.Keys[led.KeyId].Color = $"#{led.Index:X2}55A9";
        else layer.ExtraLedColors[led.Index] = $"#A8{led.Index:X2}21";
    }
    var original = JsonSerializer.Serialize(project);
    var scene = LightingPreviewEngine.CreateScene(device, layer); scene.Render(0, [], frame);
    foreach (var led in leds) Check(frame[led.Index] == PreviewRgb.FromHex(led.KeyId is not null ? layer.Keys[led.KeyId].Color : layer.ExtraLedColors[led.Index]), "Static mapping preserves exact per-LED RGB");
    Check(JsonSerializer.Serialize(project) == original, "Scene construction never mutates a project");
    var key = leds.First(l => l.KeyId is not null); var another = leds.First(l => l.KeyId is not null && l.Index != key.Index);
    layer.LightingEffect = new() { Mode = 3, Speed = 128, Brightness = 153, Target = 2, Reverse = true };
    layer.LedEffects[key.Index] = new() { Mode = 1, Speed = 255, Brightness = 99 };
    scene = LightingPreviewEngine.CreateScene(device, layer); scene.Render(100, [], frame);
    Check(frame[key.Index] == LightingPreviewEngine.Pixel(new(1,255,99,3,0), PreviewRgb.FromHex(layer.Keys[key.KeyId!].Color), key,100,[]), "Key override applies even when global effect targets sides only");
    Check(frame[another.Index] == PreviewRgb.FromHex(layer.Keys[another.KeyId!].Color), "Excluded keys preserve base brightness and color");
    var beforeDraft = JsonSerializer.Serialize(project);
    scene = LightingPreviewEngine.CreateScene(device, layer, new(2,255,255,3,0), new HashSet<int> { another.Index }); scene.Render(100, [], frame);
    Check(frame[another.Index] == LightingPreviewEngine.Pixel(new(2,255,255,3,0), default, another,100,[]), "Draft override affects exactly its selection");
    Check(JsonSerializer.Serialize(project) == beforeDraft, "Draft preview is not an implicit apply");
    scene = LightingPreviewEngine.CreateScene(device, layer, colorsOnly:true); scene.Render(777, [], frame);
    foreach (var led in leds) Check(frame[led.Index] == PreviewRgb.FromHex(led.KeyId is not null ? layer.Keys[led.KeyId].Color : layer.ExtraLedColors[led.Index]), "Color editing displays base RGB despite active effects");
    var left = leds[device.LeftSideLedIndices[0]]; var right = leds[device.RightSideLedIndices[0]];
    var sync = new PreviewEffect(12,128,255,3,2);
    for (uint time = 0; time < 10000; time += 97) Check(LightingPreviewEngine.Pixel(sync, new(255,255,255), left,time,[]) == LightingPreviewEngine.Pixel(sync, new(255,255,255), right,time,[]), "Side sync mirrors physical X while preserving Y");
    var reactive = new PreviewEffect(9,255,255,3,0); var rgb = new PreviewRgb(255,120,30);
    Check(LightingPreviewEngine.Pixel(reactive,rgb,key,10,[]) == default, "Reactive preview is dark until an explicit simulated key press");
    Check(LightingPreviewEngine.Pixel(reactive,rgb,key,10,[new(10,key.X,key.Y,key.Index)]) == rgb, "Pressed key begins at full base color");
    Check(LightingPreviewEngine.Pixel(reactive,rgb,another,10,[new(10,key.X,key.Y,key.Index)]) == default, "Single-key response does not invent presses for other keys");
    Check(LightingPreviewEngine.Pixel(reactive,rgb,key,410,[new(10,key.X,key.Y,key.Index)]) == default, "Expired hit returns dark");
    Check(LightingPreviewEngine.Pixel(new(0,128,255,3,0),rgb,key,0,[],128) == new PreviewRgb(128,60,15), "Separate master dimming is applied only when explicitly supplied");
}
Console.WriteLine($"PASS {checks} deterministic preview checks, including 40320 byte-exact pixels from the unchanged firmware C implementation; no hardware.");
