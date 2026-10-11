using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;

namespace InGlorious.Core;

/// <summary>Observed stock USB protocol only. These pure encoders never open a device.
/// Source observations and remaining transport/restore gates: docs/stock-protocol.md.</summary>
public enum StockDevice { ProIso, Numpad }
public enum StockMacroRepeat { Once, WhileHeld, Toggle }
public readonly record struct StockColor(byte R, byte G, byte B, bool Breathing = false);
public sealed record StockKey(string MatrixId, int BindingIndex, int? LedIndex, string DefaultBinding, bool CanRemap = true);
public sealed record StockMacroEvent(string Key, bool Down, int DelayAfterMs = 1);
public sealed record StockBinding(string Code, int Modifiers = 0, int? MacroSlot = null);

/// <summary>Wire facts, not an authorization to transmit or evidence of persistence.</summary>
public sealed class StockPacket
{
    private readonly byte[] bytes;
    public string Purpose { get; }
    public int DelayAfterMs { get; }
    public int Length => bytes.Length;
    public byte[] ToArray() => (byte[])bytes.Clone();
    internal StockPacket(byte[] data, string purpose, int delay) { bytes = (byte[])data.Clone(); Purpose = purpose; DelayAfterMs = delay; }
}

public static class StockProtocol
{
    public const int ProfileCount = 3;
    public const int LayerCount = 3;
    public const int MaxMacroEvents = 82;
    public const int FeatureReportLength = 264;
    public const string PreserveFixedControl = "STOCK_KEEP";

    // Matrix positions are QMK's physical ISO layout. Native indices were joined by
    // physical position and CORE component identity, never by QMK LED ordinal.
    private const string ProMap = "r1c3:0:KC_ESC r2c6:1:KC_F1 r3c6:2:KC_F2 r3c1:3:KC_F3 r3c3:4:KC_F4 r0c7:5:KC_F5 r6c3:6:KC_F6 r7c1:7:KC_F7 r7c6:8:KC_F8 r10c6:9:KC_F9 r10c7:10:KC_F10 r10c3:11:KC_F11 r10c5:12:KC_F12 r9c7:13:KC_PSCR r0c1:14:KC_MUTE r1c6:20:KC_GRV r1c7:21:KC_1 r2c7:22:KC_2 r3c7:23:KC_3 r4c7:24:KC_4 r4c6:25:KC_5 r5c6:26:KC_6 r5c7:27:KC_7 r6c7:28:KC_8 r7c7:29:KC_9 r8c7:30:KC_0 r8c6:31:KC_MINS r6c6:32:KC_EQL r10c1:33:KC_BSPC r6c5:34:KC_DEL r1c1:40:KC_TAB r1c0:41:KC_Q r2c0:42:KC_W r3c0:43:KC_E r4c0:44:KC_R r4c1:45:KC_T r5c1:46:KC_Y r5c0:47:KC_U r6c0:48:KC_I r7c0:49:KC_O r8c0:50:KC_P r8c1:51:KC_LBRC r6c1:52:KC_RBRC r1c5:54:KC_PGUP r2c1:60:KC_CAPS r1c2:61:KC_A r2c2:62:KC_S r3c2:63:KC_D r4c2:64:KC_F r4c3:65:KC_G r5c3:66:KC_H r5c2:67:KC_J r6c2:68:KC_K r7c2:69:KC_L r8c2:70:KC_SCLN r8c3:71:KC_QUOT r8c4:72:KC_NUHS r10c4:73:KC_ENT r2c5:74:KC_PGDN r0c0:80:KC_LSFT r2c3:81:KC_NUBS r1c4:82:KC_Z r2c4:83:KC_X r3c4:84:KC_C r4c4:85:KC_V r4c5:86:KC_B r5c5:87:KC_N r5c4:88:KC_M r6c4:89:KC_COMM r7c4:90:KC_DOT r8c5:91:KC_SLSH r9c1:92:KC_RSFT r3c5:93:KC_UP r7c5:94:KC_END r0c6:100:KC_LCTL r9c0:101:KC_LGUI r9c3:102:KC_LALT r9c4:105:KC_SPC r9c5:108:KC_RALT r9c2:109:STOCK_KEEP r0c4:110:KC_RCTL r0c3:112:KC_LEFT r7c3:113:KC_DOWN r0c5:114:KC_RGHT";
    private const string NumMap = "r0c0:0:KC_NUM r0c1:1:KC_PSLS r0c2:2:KC_PAST r0c3:3:KC_PMNS r1c0:4:KC_P7 r1c1:5:KC_P8 r1c2:6:KC_P9 r1c3:7:KC_PPLS r2c0:8:KC_P4 r2c1:9:KC_P5 r2c2:10:KC_P6 r2c3:11:KC_MUTE r3c0:12:KC_P1 r3c1:13:KC_P2 r3c2:14:KC_P3 r3c3:15:KC_PENT r4c0:16:KC_P0 r4c3:19:KC_PDOT";
    private static readonly int[] NumLeds = [1, 2, 3, 4, 7, 8, 9, 10, 19, 20, 21, -1, 25, 26, 27, 28, 38, -1, -1, 39];
    private static readonly IReadOnlyList<StockKey> ProKeys = BuildKeys(ProMap, false);
    private static readonly IReadOnlyList<StockKey> NumKeys = BuildKeys(NumMap, true);
    private static readonly IReadOnlyDictionary<string, byte> KeyboardUsages = BuildUsages();
    public static IReadOnlyList<StockKey> Keys(StockDevice device) => device switch { StockDevice.ProIso => ProKeys, StockDevice.Numpad => NumKeys, _ => throw new ArgumentOutOfRangeException(nameof(device)) };
    public static IReadOnlyList<int> SideLedIndices(StockDevice device) => Array.AsReadOnly(device switch {
        StockDevice.ProIso => new[] {15,16,17,18,35,36,37,38,55,56,57,58,75,76,77,78},
        StockDevice.Numpad => new[] {0,5,6,11,12,17,18,23,24,29,30,35,36,41},
        _ => throw new ArgumentOutOfRangeException(nameof(device)) });

    private static IReadOnlyList<StockKey> BuildKeys(string map, bool numpad) => Array.AsReadOnly(map.Split(' ').Select(entry => {
        var fields = entry.Split(':'); var index = int.Parse(fields[1], CultureInfo.InvariantCulture);
        var fixedControl = numpad ? index == 11 : index is 14 or 109;
        int? led = numpad ? NumLeds[index] : index;
        if (led < 0 || (!numpad && index == 14)) led = null;
        return new StockKey(fields[0], index, led, fields[2], !fixedControl);
    }).ToArray());

    private static IReadOnlyDictionary<string, byte> BuildUsages()
    {
        var values = new Dictionary<string, byte>(StringComparer.Ordinal) { ["KC_NO"] = 0 };
        for (int i = 0; i < 26; i++) values["KC_" + (char)('A' + i)] = (byte)(4 + i);
        for (int i = 1; i <= 9; i++) values["KC_" + i] = (byte)(29 + i);
        values["KC_0"] = 39;
        var names = "ENT ESC BSPC TAB SPC MINS EQL LBRC RBRC BSLS NUHS SCLN QUOT GRV COMM DOT SLSH CAPS".Split(' ');
        for (int i = 0; i < names.Length; i++) values["KC_" + names[i]] = (byte)(40 + i);
        for (int i = 1; i <= 12; i++) values["KC_F" + i] = (byte)(57 + i);
        names = "PSCR SCRL PAUS INS HOME PGUP DEL END PGDN RGHT LEFT DOWN UP NUM PSLS PAST PMNS PPLS PENT P1 P2 P3 P4 P5 P6 P7 P8 P9 P0 PDOT NUBS APP".Split(' ');
        for (int i = 0; i < names.Length; i++) values["KC_" + names[i]] = (byte)(70 + i);
        for (int i = 13; i <= 24; i++) values["KC_F" + i] = (byte)(104 + i - 13);
        names = "LCTL LSFT LALT LGUI RCTL RSFT RALT RGUI".Split(' ');
        for (int i = 0; i < names.Length; i++) values["KC_" + names[i]] = (byte)(224 + i);
        return new ReadOnlyDictionary<string, byte>(values);
    }

    public static bool TryKeyboardUsage(string code, out byte value) => KeyboardUsages.TryGetValue(code, out value);
    private static byte Usage(string code) => KeyboardUsages.TryGetValue(code, out var value) ? value : throw new InvalidDataException($"Stock USB: unsupported keyboard usage '{code}'.");
    private static void Slot(int profile, int layer)
    {
        if (profile is < 0 or >= ProfileCount || layer is < 0 or >= LayerCount) throw new ArgumentOutOfRangeException(nameof(profile), "Stock has three profiles and three native layers (indices 0–2).");
    }
    private static byte[] Header(int command, int profile, int layer, int length = FeatureReportLength)
    {
        Slot(profile, layer); var bytes = new byte[length]; bytes[0] = 7; bytes[1] = (byte)command; bytes[2] = (byte)(profile + 1); bytes[3] = (byte)(layer + 1); return bytes;
    }
    public static StockPacket SelectProfile(int profile, int layer) => new(Header(1, profile, layer, 64), "Select/commit stock profile and native layer; original length 64", 550);

    /// <summary>Complete scene: every mapped LED key and every side LED are explicit.
    /// Partial updates are rejected because firmware packets replace whole pages.</summary>
    public static IReadOnlyList<StockPacket> EncodeLighting(StockDevice device, int profile, int layer,
        IReadOnlyDictionary<string, StockColor> keyColors, IReadOnlyDictionary<int, StockColor> sideColors)
    {
        Slot(profile, layer); ArgumentNullException.ThrowIfNull(keyColors); ArgumentNullException.ThrowIfNull(sideColors);
        var litKeys = Keys(device).Where(k => k.LedIndex.HasValue).ToArray();
        if (keyColors.Count != litKeys.Length || litKeys.Any(k => !keyColors.ContainsKey(k.MatrixId))) throw new InvalidDataException("A complete physical LED scene is required; encoder LEDs and unknown matrix IDs are not supported.");
        var side = SideLedIndices(device);
        if (sideColors.Count != side.Count || side.Any(i => !sideColors.ContainsKey(i))) throw new InvalidDataException("Explicit colors for all side LEDs are required; existing colors cannot be read back.");
        var payload = new byte[600];
        void Put(int index, StockColor color) { var at = index * 5; payload[at] = color.Breathing ? (byte)254 : (byte)255; payload[at + 1] = (byte)index; payload[at + 2] = color.R; payload[at + 3] = color.G; payload[at + 4] = color.B; }
        foreach (var key in litKeys) Put(key.LedIndex!.Value, keyColors[key.MatrixId]);
        foreach (var led in side) Put(led, sideColors[led]);
        var packets = new List<StockPacket>();
        for (int page = 0; page < 3; page++) { var bytes = Header(6, profile, layer); bytes[4] = device == StockDevice.ProIso ? (byte)83 : (byte)42; bytes[5] = (byte)page; bytes[6] = 20; Array.Copy(payload, page * 200, bytes, 8, 200); packets.Add(new(bytes, $"Complete per-key RGB page {page}", 100)); }
        packets.Add(SelectProfile(profile, layer)); return packets.AsReadOnly();
    }

    /// <summary>Explicit complete replacement of physical bindings. Fixed controls must
    /// use STOCK_KEEP; arbitrary Fn, knob and QMK layer semantics are not available.</summary>
    public static StockPacket EncodeKeyBindings(StockDevice device, int profile, int layer, IReadOnlyDictionary<string, StockBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings); var keys = Keys(device);
        if (bindings.Count != keys.Count || keys.Any(k => !bindings.ContainsKey(k.MatrixId))) throw new InvalidDataException("All physical bindings must be supplied explicitly; no partial/default-reset update is allowed.");
        var data = Header(3, profile, layer); var count = device == StockDevice.ProIso ? 120 : 20;
        if (device == StockDevice.ProIso) { Array.Fill(data, (byte)255, 8, count * 2); data[8 + 53] = 1; data[8 + count + 53] = 49; }
        foreach (var key in keys)
        {
            var binding = bindings[key.MatrixId] ?? throw new InvalidDataException("Null binding.");
            byte type; byte value;
            if (!key.CanRemap)
            {
                if (binding.Code != PreserveFixedControl || binding.Modifiers != 0 || binding.MacroSlot.HasValue) throw new InvalidDataException($"{key.MatrixId}: stock firmware remapping of this fixed control is not established; use STOCK_KEEP.");
                type = key.DefaultBinding == PreserveFixedControl ? (byte)1 : (byte)3;
                value = key.DefaultBinding == PreserveFixedControl ? (byte)160 : (byte)2;
            }
            else if (binding.Code == "MACRO")
            {
                if (binding.MacroSlot is not >= 1 or > 255 || binding.Modifiers != 0) throw new InvalidDataException("Macro requires explicit wire slot 1–255 and no modifiers; device slot capacity/allocation remain unverified.");
                type = 5; value = (byte)binding.MacroSlot.Value;
            }
            else
            {
                if (binding.MacroSlot.HasValue || binding.Modifiers is < 0 or > 15) throw new InvalidDataException("Unsupported binding modifiers or unexpected macro slot.");
                value = Usage(binding.Code); type = binding.Modifiers == 0 ? (byte)1 : (byte)(224 + binding.Modifiers);
                if (value == 0 && binding.Modifiers != 0) throw new InvalidDataException("Disabled key cannot have modifiers.");
            }
            data[8 + key.BindingIndex] = type; data[8 + count + key.BindingIndex] = value;
        }
        return new(data, "Complete stock physical key binding replacement", 150);
    }

    /// <summary>Only encodes a supplied slot. It does not allocate/erase slots and does
    /// not claim the device can store 255 macros.</summary>
    public static StockPacket EncodeMacro(int wireSlot, IReadOnlyList<StockMacroEvent> events)
    {
        if (wireSlot is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(wireSlot));
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count is < 2 or > MaxMacroEvents) throw new InvalidDataException("A complete stock macro requires 2–82 key events; events are never truncated.");
        var held = new HashSet<byte>(); var bytes = new byte[FeatureReportLength]; bytes[0] = 7; bytes[1] = 5; bytes[2] = (byte)wireSlot;
        for (int i = 0; i < events.Count; i++)
        {
            var item = events[i] ?? throw new InvalidDataException("Null macro event."); var key = Usage(item.Key);
            if (key == 0 || item.DelayAfterMs is < 1 or > 32767) throw new InvalidDataException("Macro needs a keyboard key and an inter-event delay of 1–32767 ms.");
            if (item.Down ? !held.Add(key) : !held.Remove(key)) throw new InvalidDataException("Macro key-down/key-up sequence is unbalanced.");
            if (i == events.Count - 1 && item.DelayAfterMs != 1) throw new InvalidDataException("Stock's final event has a fixed 1 ms delay; trailing delay cannot be represented faithfully.");
            var at = 8 + i * 3; bytes[at] = (byte)((item.DelayAfterMs >> 8) | (item.Down ? 128 : 0)); bytes[at + 1] = (byte)(item.DelayAfterMs & 255); bytes[at + 2] = key;
        }
        if (held.Count != 0) throw new InvalidDataException("Macro leaves keys held.");
        return new(bytes, $"Stock macro data for explicitly supplied slot {wireSlot}; allocation unverified", 150);
    }

    public static StockPacket EncodeMacroModes(StockDevice device, int profile, int layer, IReadOnlyDictionary<string, StockMacroRepeat> modes)
    {
        ArgumentNullException.ThrowIfNull(modes); var keys = Keys(device); var bytes = Header(4, profile, layer);
        foreach (var pair in modes)
        {
            var key = keys.SingleOrDefault(k => k.MatrixId == pair.Key);
            if (key == null || !key.CanRemap || !key.LedIndex.HasValue) throw new InvalidDataException("Unknown or fixed macro key.");
            // Observed CORE uses lighting indices here even for Numpad, whose function
            // map differs. Preserve that wire fact; do not substitute key.BindingIndex.
            ushort mode = pair.Value switch { StockMacroRepeat.Once => 1, StockMacroRepeat.WhileHeld => ushort.MaxValue, StockMacroRepeat.Toggle => 0, _ => throw new InvalidDataException("Unknown macro repeat mode.") };
            var at = 8 + key.LedIndex.Value * 2; bytes[at] = (byte)(mode >> 8); bytes[at + 1] = (byte)(mode & 255);
        }
        return new(bytes, "Complete macro playback mode table; uses native LED indices", 150);
    }

    /// <summary>Additional stock-specific validation for existing QMK-style drafts.
    /// A passing result alone does not mean a project is executable.</summary>
    public static IReadOnlyList<string> ValidateDraft(AppProject project)
    {
        var errors = new List<string>();
        if (project == null) return ["Profile is null."];
        StockDevice device;
        if (project.DeviceId == "pro-iso") device = StockDevice.ProIso; else if (project.DeviceId == "numpad") device = StockDevice.Numpad; else return ["Unknown stock device."];
        if (project.Layers == null || project.Layers.Count != LayerCount) errors.Add("Stock requires exactly three explicitly defined native layers; a four-layer draft cannot be truncated.");
        if (project.Layers != null) foreach (var layer in project.Layers)
        {
            if (layer?.Keys == null) { errors.Add("Missing layer keys."); continue; }
            foreach (var pair in layer.Keys)
            {
                var key = Keys(device).SingleOrDefault(k => k.MatrixId == pair.Key);
                var code = pair.Value?.Binding;
                if (key == null) { errors.Add("Virtual/unknown controls have no verified stock mapping: " + pair.Key); continue; }
                if (!key.CanRemap) { if (code != PreserveFixedControl) errors.Add("Fixed stock control must be explicitly preserved: " + pair.Key); continue; }
                if (code == null || !KeyboardUsages.ContainsKey(code)) errors.Add("Unsupported stock binding or unresolved macro allocation: " + code);
            }
            if (Keys(device).Any(k => !layer.Keys.ContainsKey(k.MatrixId))) errors.Add("Missing physical key bindings.");
        }
        if (project.Macros == null) errors.Add("Missing macro list.");
        else foreach (var macro in project.Macros)
        {
            if (macro?.Steps == null) { errors.Add("Missing macro steps."); continue; }
            var eventCount = macro.Steps.Count(s => s?.Kind is "down" or "up");
            if (eventCount > MaxMacroEvents) errors.Add($"Macro {macro.Name}: {eventCount} events exceed the stock maximum of 82; no truncation.");
            errors.Add("Macro timeline conversion and existing device slot allocation must be explicit before transfer.");
        }
        return errors.Distinct().ToArray();
    }
}
