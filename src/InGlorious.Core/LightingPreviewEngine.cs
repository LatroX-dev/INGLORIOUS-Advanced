// SPDX-License-Identifier: GPL-2.0-or-later
// Integer arithmetic port of firmware/inglorious/ig_effects.h (firmware 0.5).
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace InGlorious.Core;

public readonly record struct PreviewRgb(byte R, byte G, byte B)
{
    public static PreviewRgb FromHex(string value) => new(byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber), byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber));
}
public readonly record struct PreviewLed(byte Index, byte X, byte Y, bool Side, string? KeyId);
public readonly record struct PreviewEffect(byte Mode, byte Speed, byte Brightness, byte Target, byte Flags, PreviewRgb[]? Colors = null);
public readonly record struct PreviewHit(uint Time, byte X, byte Y, byte Led);

/// <summary>Pure local rendering; time and simulated physical key presses are supplied by the caller.</summary>
public static class LightingPreviewEngine
{
    public const int MaxHits = 8;
    private static readonly Dictionary<string, PreviewLed[]> Layouts = new(StringComparer.Ordinal);
    public static IReadOnlyList<PreviewLed> Layout(DeviceDefinition device)
    {
        lock (Layouts)
        {
            if (Layouts.TryGetValue(device.Id, out var known)) return Array.AsReadOnly(known);
            var assembly = typeof(DeviceCatalog).Assembly;
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(device.Id + ".json", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var json = JsonDocument.Parse(stream);
            var leds = json.RootElement.GetProperty("rgb_matrix").GetProperty("layout").EnumerateArray().Select((led, index) =>
                new PreviewLed(checked((byte)index), led.GetProperty("x").GetByte(), led.GetProperty("y").GetByte(),
                    (led.GetProperty("flags").GetInt32() & 2) != 0,
                    led.TryGetProperty("matrix", out var matrix) ? $"r{matrix[0]}c{matrix[1]}" : null)).ToArray();
            if (leds.Length != device.LedCount || leds.Any(l => l.KeyId is not null && !device.Keys.Any(k => k.Id == l.KeyId && k.LedIndex == l.Index)))
                throw new InvalidDataException("LED-Koordinaten und Tastenzuordnung stimmen nicht überein.");
            Layouts.Add(device.Id, leds); return Array.AsReadOnly(leds);
        }
    }
    public static PreviewEffect GlobalEffect(LayerProfile layer)
    {
        var e = layer.LightingEffect;
        return new(checked((byte)e.Mode), checked((byte)e.Speed), checked((byte)e.Brightness), checked((byte)e.Target), (byte)((e.Reverse ? 1 : 0) | (layer.SynchronizeSides ? 2 : 0)), e.Colors.Count == 0 ? null : e.Colors.Select(PreviewRgb.FromHex).ToArray());
    }
    public static PreviewEffect SelectedEffect(PerLedEffectSettings e, bool synchronized) =>
        new(checked((byte)e.Mode), checked((byte)e.Speed), checked((byte)e.Brightness), 3, (byte)((e.Reverse ? 1 : 0) | (synchronized ? 2 : 0)), e.Colors.Count == 0 ? null : e.Colors.Select(PreviewRgb.FromHex).ToArray());
    public static LightingPreviewScene CreateScene(DeviceDefinition device, LayerProfile layer, PreviewEffect? draft = null, IReadOnlySet<int>? draftSelection = null, bool colorsOnly = false)
    {
        var layout = Layout(device).ToArray(); var colors = new PreviewRgb[layout.Length]; var effects = new PreviewEffect[layout.Length];
        var global = draft.HasValue && draftSelection is null ? draft.Value : GlobalEffect(layer);
        foreach (var led in layout)
        {
            colors[led.Index] = PreviewRgb.FromHex(led.KeyId is not null ? layer.Keys[led.KeyId].Color : layer.ExtraLedColors.GetValueOrDefault(led.Index, layer.SideLedColor));
            effects[led.Index] = layer.LedEffects.TryGetValue(led.Index, out var own) ? SelectedEffect(own, layer.SynchronizeSides) : global;
            if (draft.HasValue && draftSelection?.Contains(led.Index) == true) effects[led.Index] = draft.Value with { Target = 3 };
            if (colorsOnly) effects[led.Index] = new(0, 128, 255, 3, 0);
        }
        return new(layout, colors, effects);
    }
    public static bool IsReactive(int mode) => mode is 9 or 10 or 11 or 15 or 20;
    public static bool IsAnimated(int mode) => mode is not 0 and not 13;
    private static byte B(int value) => unchecked((byte)value);
    private static byte Scale(byte value, byte scale) => (byte)(value * scale / 255);
    private static byte Triangle(byte phase) => B(phase < 128 ? phase * 2 : (255 - phase) * 2);
    private static byte Hash(uint value)
    {
        unchecked { value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; value ^= value >> 16; return (byte)value; }
    }
    private static PreviewRgb Rainbow(byte hue)
    {
        var fraction = B(hue % 43 * 6);
        return (hue / 43) switch { 0 => new(255, fraction, 0), 1 => new(B(255 - fraction), 255, 0), 2 => new(0, 255, fraction), 3 => new(0, B(255 - fraction), 255), 4 => new(fraction, 0, 255), _ => new(255, 0, B(255 - fraction)) };
    }
    public static PreviewRgb Sequence(byte phase, ReadOnlySpan<PreviewRgb> colors)
    {
        if (colors.Length == 0 || colors.Length > 8) return Rainbow(phase);
        var scaled = phase * colors.Length; var index = scaled >> 8; var next = (index + 1) % colors.Length; var fraction = scaled & 255;
        static byte Mix(byte a, byte b, int fraction) => (byte)((a * (256 - fraction) + b * fraction) >> 8);
        return new(Mix(colors[index].R, colors[next].R, fraction), Mix(colors[index].G, colors[next].G, fraction), Mix(colors[index].B, colors[next].B, fraction));
    }
    public static PreviewRgb Pixel(PreviewEffect effect, PreviewRgb color, PreviewLed led, uint now, ReadOnlySpan<PreviewHit> hits, byte masterBrightness = 255)
    {
        // Target exclusion returns the unmodified base, before the firmware's separate master dimmer.
        if ((effect.Target & (led.Side ? 2 : 1)) == 0) return Dim(color, masterBrightness);
        var x = led.Side && (effect.Flags & 2) != 0 ? (byte)0 : led.X; var y = led.Y;
        var period = 10000u - effect.Speed * 9600u / 255;
        var phase = (byte)(now % period * 256u / period);
        var direction = (effect.Flags & 1) != 0 ? B(-phase) : phase;
        byte level = 255;
        switch (effect.Mode)
        {
            case 0: break;
            case 1: level = Triangle(phase); break;
            case 2: color = Sequence(phase, effect.Colors); break;
            case 3: color = Rainbow(B(x - direction)); break;
            case 4: color = Rainbow(B(y * 4 - direction)); break;
            case 5: level = Triangle(B(x - direction)); break;
            case 6: level = Triangle(B(y * 4 - direction)); break;
            case 7:
                var coordinate = led.Side ? B(y * 4) : x;
                var position = Triangle(phase); if ((effect.Flags & 1) != 0) position = B(255 - position);
                var distance = Math.Abs(coordinate - position); level = distance < 32 ? B(255 - distance * 8) : (byte)0; break;
            case 8:
                var sparkle = Triangle(B(phase + Hash((uint)x * 257 + y))); level = sparkle > 192 ? B((sparkle - 192) * 4) : (byte)0; break;
            case 9: case 10: case 11: case 15: case 20:
                level = 0; var lifetime = 4000u - effect.Speed * 3600u / 255;
                foreach (var hit in hits[..Math.Min(hits.Length, MaxHits)])
                {
                    var age = unchecked(now - hit.Time); if (age >= lifetime) continue;
                    uint contribution;
                    if (effect.Mode == 9)
                    {
                        if (!led.Side && hit.Led != led.Index) continue;
                        contribution = 255 - age * 255 / lifetime;
                    }
                    else
                    {
                        var dx = Math.Abs(x - hit.X); var dy = Math.Abs(y - hit.Y);
                        var octagonal = dx > dy ? dx + dy / 2 : dy + dx / 2;
                        if (effect.Mode == 15)
                        {
                            var fade = 255 - age * 255 / lifetime;
                            contribution = octagonal < 64 ? fade * fade / 255 * (uint)(64 - octagonal) / 64 : 0;
                        }
                        else
                        {
                            if (effect.Mode == 20) octagonal = Math.Min(dx, dy);
                            var ring = age * 320 / lifetime; var delta = Math.Abs(octagonal - (int)ring);
                            contribution = delta < 24 ? (uint)(255 - delta * 11) : 0;
                        }
                    }
                    if (contribution > level) level = (byte)contribution;
                }
                if (effect.Mode == 11) color = Rainbow(B(x + y * 2 + phase));
                break;
            case 12:
                var seed = Hash((uint)x / 12); var rainDistance = B(y * 4 - direction - seed);
                level = rainDistance < 48 ? B(255 - rainDistance * 5) : (byte)0; color = Rainbow(B(seed + phase / 4)); break;
            case 13:
                var gradient = B(x + y * 2); color = Rainbow((effect.Flags & 1) != 0 ? B(-gradient) : gradient); break;
            case 14:
                var glitter = Hash(unchecked((uint)x * 257 + y + now / (period / 16) * 65537)); level = glitter > 216 ? (byte)255 : (byte)80; break;
            case 16:
                var snakeDistance = B(x + y * 3 - direction); level = snakeDistance < 64 ? B(255 - snakeDistance * 3) : (byte)0; break;
            case 17: level = (((x + direction) / 24 + y / 12) & 1) != 0 ? (byte)255 : (byte)32; break;
            case 18:
                level = phase < 48 ? B(phase < 24 ? phase * 10 : (48 - phase) * 10) : phase is >= 72 and < 112 ? B(phase < 92 ? (phase - 72) * 8 : (112 - phase) * 8) : (byte)0; break;
            case 19:
                var heat = Hash(unchecked((uint)(x / 12) * 257 + now / (period / 32) * 65537));
                color = new(255, B(32 + heat / 2), 0); level = B(64 + y * 127 / 64 + heat / 4); break;
            default: return Dim(color, masterBrightness);
        }
        return Dim(Dim(color, Scale(level, effect.Brightness)), masterBrightness);
    }
    private static PreviewRgb Dim(PreviewRgb color, byte level) => new(Scale(color.R, level), Scale(color.G, level), Scale(color.B, level));
}

public sealed class LightingPreviewScene(PreviewLed[] leds, PreviewRgb[] colors, PreviewEffect[] effects)
{
    public IReadOnlyList<PreviewLed> Leds { get; } = Array.AsReadOnly(leds);
    public bool Animated { get; } = effects.Any(e => LightingPreviewEngine.IsAnimated(e.Mode));
    public bool Reactive { get; } = effects.Any(e => LightingPreviewEngine.IsReactive(e.Mode));
    public int LedCount => leds.Length;
    public void Render(uint time, ReadOnlySpan<PreviewHit> hits, Span<PreviewRgb> destination)
    {
        if (destination.Length < leds.Length) throw new ArgumentException("Frame buffer is too short.", nameof(destination));
        for (var i = 0; i < leds.Length; ++i) destination[i] = LightingPreviewEngine.Pixel(effects[i], colors[i], leds[i], time, hits);
    }
}
