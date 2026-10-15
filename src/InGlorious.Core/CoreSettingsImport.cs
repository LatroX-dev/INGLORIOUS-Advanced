using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace InGlorious.Core;

public sealed record CoreImportSelection(CoreImportPreview Preview, bool IncludeColors, bool IncludeBindings)
{
    public AppProject ApplyToCopy(AppProject current) => Preview.ApplyToCopy(current, IncludeColors, IncludeBindings);
}

/// <summary>Detached, reviewed values only; never retains CORE instance IDs, paths, profiles or macros.</summary>
public sealed class CoreImportPreview
{
    public string DeviceId { get; }
    public int ActiveCoreLayer { get; }
    public int BrightnessPercent { get; }
    public IReadOnlyDictionary<string, string> Colors { get; }
    public IReadOnlyDictionary<string, string> Bindings { get; }
    public int OmittedColors { get; }
    public int OmittedBindings { get; }
    public bool CanImport => Colors.Count + Bindings.Count > 0;
    internal CoreImportPreview(string id, int layer, int brightness, Dictionary<string, string> colors, Dictionary<string, string> bindings, int omittedColors, int omittedBindings)
    { DeviceId = id; ActiveCoreLayer = layer; BrightnessPercent = brightness; Colors = new ReadOnlyDictionary<string, string>(colors); Bindings = new ReadOnlyDictionary<string, string>(bindings); OmittedColors = omittedColors; OmittedBindings = omittedBindings; }

    public string DescribeChanges(bool includeColors, bool includeBindings)
    {
        var keys = DeviceCatalog.Get(DeviceId).Keys.ToDictionary(k => k.Id, k => k.DisplayLabel);
        var lines = new List<string>();
        if (includeColors) { lines.Add(L.T("Einzelfarben und Helligkeit (statisch)") + $" · {BrightnessPercent}%"); lines.AddRange(Colors.Select(pair => keys[pair.Key] + ": " + pair.Value)); }
        if (includeBindings) { lines.Add(L.T("Einfache Tastenbelegungen")); lines.AddRange(Bindings.Select(pair => keys[pair.Key] + ": " + BindingCatalog.KeyLabel(pair.Value))); }
        return string.Join("\n", lines);
    }

    public AppProject ApplyToCopy(AppProject current, bool includeColors = true, bool includeBindings = true)
    {
        if (current.DeviceId != DeviceId) throw new InvalidDataException(L.T("Die CORE-Daten gehören zu einem anderen Modell."));
        if ((!includeColors || Colors.Count == 0) && (!includeBindings || Bindings.Count == 0)) throw new InvalidDataException(L.T("Keine passenden CORE-Einstellungen gefunden."));
        OnboardSerializer.ValidateEditorProject(current);
        var copy = ProjectService.Clone(current); copy.Name = current.Name + " – CORE";
        var layer = copy.Layers[0];
        if (includeColors)
        {
            var device = DeviceCatalog.Get(DeviceId);
            foreach (var (keyId, color) in Colors)
            {
                layer.Keys[keyId].Color = color;
                var led = device.Keys.Single(k => k.Id == keyId).LedIndex;
                if (led.HasValue) layer.LedEffects[led.Value] = new() { Mode = 0, Brightness = (int)Math.Round(BrightnessPercent * 255d / 100, MidpointRounding.AwayFromZero) };
            }
        }
        if (includeBindings) foreach (var (keyId, binding) in Bindings) layer.Keys[keyId].Binding = binding;
        OnboardSerializer.ValidateEditorProject(copy);
        return copy;
    }
}

/// <summary>Read-only CORE cache import. No CORE process, USB, profile save or automatic transfer.</summary>
public static class CoreSettingsImport
{
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public static string? DefaultFolder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glorious Core", "datastore") : null;

    public static CoreImportPreview ReadFolder(string folder, string deviceId)
    {
        // Keep both source handles stable while taking the snapshot; never repair or rewrite the cache.
        using var devices = File.Open(Path.Combine(folder, "Devices.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var profiles = File.Open(Path.Combine(folder, "DeviceProfiles.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(Read(devices), Read(profiles), deviceId);
    }

    public static CoreImportPreview Parse(ReadOnlyMemory<byte> devicesJson, ReadOnlyMemory<byte> profilesJson, string deviceId)
    {
        if (devicesJson.Length > MaximumFileBytes || profilesJson.Length > MaximumFileBytes) throw Invalid();
        var device = DeviceCatalog.Get(deviceId);
        try
        {
            using var devices = JsonDocument.Parse(WithoutBom(devicesJson), new JsonDocumentOptions { MaxDepth = 32 });
            using var profiles = JsonDocument.Parse(WithoutBom(profilesJson), new JsonDocumentOptions { MaxDepth = 32 });
            CheckUniqueProperties(devices.RootElement); CheckUniqueProperties(profiles.RootElement);
            var rows = Array(devices.RootElement, "deviceInstances");
            var ids = CoreProductIds(deviceId);
            var matching = rows.Where(row => ids.Contains(String(row, "productId"))).ToArray();
            if (matching.Length == 0) throw new InvalidDataException(L.T("Keine passenden CORE-Einstellungen gefunden."));
            if (matching.Length != 1) throw Ambiguous();
            var selected = matching[0]; var instance = String(selected, "instanceId"); var profileId = String(selected, "currentDeviceProfileId");
            if (instance.Length == 0 || profileId.Length == 0 || rows.Count(row => String(row, "instanceId") == instance) != 1) throw Ambiguous();
            var assigned = Array(selected, "iterableProfileIds").Select(item => item.GetString() ?? "").ToArray();
            if (assigned.Any(string.IsNullOrEmpty) || assigned.Distinct(StringComparer.Ordinal).Count() != assigned.Length || assigned.Count(id => id == profileId) != 1) throw Ambiguous();
            // A shared profile assignment cannot prove which physical device's cached settings it represents.
            if (rows.Where(row => String(row, "instanceId") != instance).Any(row => Array(row, "iterableProfileIds").Any(item => item.GetString() == profileId))) throw Ambiguous();
            var profileRows = Array(profiles.RootElement, "profiles").Where(row => String(row, "id") == profileId).ToArray();
            if (profileRows.Length != 1) throw Ambiguous();
            var profile = profileRows[0]; var active = Integer(profile, "activeLayerIndex", 0, 2);
            var layer = profile.GetProperty("layers").GetProperty(active.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Integer(layer, "order", 0, 2) != active) throw Invalid();
            var colorRecord = profile.GetProperty("perKeyLightingRecord");
            // The wired handler sends presetLightingRecord.brightness as the global hardware brightness.
            // perKeyLightingRecord.brightness is not used by that path and must not be substituted.
            var brightness = Integer(profile.GetProperty("presetLightingRecord"), "brightness", 0, 100);
            var colors = new Dictionary<string, string>(StringComparer.Ordinal); var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            var seenColorNames = new HashSet<string>(StringComparer.Ordinal); var seenBindingNames = new HashSet<string>(StringComparer.Ordinal);
            int omittedColors = 0, omittedBindings = 0;
            foreach (var row in Array(colorRecord, "keyLightingRecords"))
            {
                var name = String(row, "keyIdentifier"); if (!seenColorNames.Add(name)) throw Invalid();
                var key = FindKey(device, name);
                var rgba = Array(row, "color").Select(x => x.GetDouble()).ToArray();
                if (rgba.Length != 4 || rgba.Any(x => !double.IsFinite(x)) || rgba.Take(3).Any(x => x < 0 || x > 255) || rgba[3] < 0 || rgba[3] > 100) throw Invalid();
                if (row.GetProperty("breathing").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                if (key?.LedIndex is null || row.GetProperty("breathing").GetBoolean()) { omittedColors++; continue; }
                // CORE RGBA alpha is a percentage; its wired RGB payload rounds premultiplied channels.
                var rgb = rgba.Take(3).Select(c => (int)Math.Round(c * rgba[3] / 100, MidpointRounding.AwayFromZero)).ToArray();
                if (!colors.TryAdd(key.Id, $"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}")) throw Invalid();
            }
            foreach (var row in Array(layer.GetProperty("keybindingData"), "boundKeys"))
            {
                var name = String(row, "componentIdentifier"); if (!seenBindingNames.Add(name)) throw Invalid();
                var key = FindKey(device, name); var target = row.GetProperty("target");
                var binding = String(target, "bindingType") == "Keystroke" && String(target, "modifier") == "None"
                    ? BasicBinding(String(target, "keyIdentifier"), deviceId.EndsWith("-iso", StringComparison.Ordinal)) : null;
                if (key is null || binding is null || !BindingCatalog.IsBasic(binding)) { omittedBindings++; continue; }
                if (!bindings.TryAdd(key.Id, binding)) throw Invalid();
            }
            return new(deviceId, active, brightness, colors, bindings, omittedColors, omittedBindings);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { throw Invalid(); }
    }

    public static IReadOnlySet<string> CoreProductIds(string deviceId)
    {
        DeviceCatalog.Get(deviceId);
        return StockDeviceCatalog.All.Where(row => row.CandidateDeviceIds.Contains(deviceId) ||
                deviceId == "gmmk3-pro-100-iso" && row.VendorId == 0x342D && row.ProductId == 0xE3CE)
            .Select(row => row.CoreCatalogName.Replace(" ", "", StringComparison.Ordinal) + (row.ProductId is 0x5092 or 0x5093 ? "ALT" : ""))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static KeyDefinition? FindKey(DeviceDefinition device, string name)
    {
        if (name == "RotaryEncoder") return device.Keys.SingleOrDefault(k => k.Label == "Knob");
        var code = name == "Fn" || name == "NumLock" && device.Id == "numpad" ? "MO(1)" : BasicBinding(name, device.Id.EndsWith("-iso", StringComparison.Ordinal));
        if (code is null) return null;
        var keys = device.Keys.Where(key => key.DefaultBinding == code && !key.IsEncoder).ToArray();
        return keys.Length == 1 ? keys[0] : null;
    }

    private static string? BasicBinding(string name, bool iso)
    {
        if (name.Length == 4 && name.StartsWith("Key", StringComparison.Ordinal) && name[3] is >= 'A' and <= 'Z') return "KC_" + name[3];
        if (name.Length == 6 && name.StartsWith("Digit", StringComparison.Ordinal) && char.IsAsciiDigit(name[5])) return "KC_" + name[5];
        if (name.Length == 7 && name.StartsWith("Numpad", StringComparison.Ordinal) && char.IsAsciiDigit(name[6])) return "KC_P" + name[6];
        if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 24 && name == "F" + function) return "KC_" + name;
        return name switch
        {
            "Escape" => "KC_ESC", "PrintScreen" => "KC_PSCR", "ScrollLock" => "KC_SCRL", "Pause" => "KC_PAUS", "Backquote" => "KC_GRV",
            "Minus" => "KC_MINS", "Equal" => "KC_EQL", "Backspace" => "KC_BSPC", "Tab" => "KC_TAB", "CapsLock" => "KC_CAPS",
            "BracketLeft" => "KC_LBRC", "BracketRight" => "KC_RBRC", "Semicolon" => "KC_SCLN", "Quote" => "KC_QUOT", "Enter" => "KC_ENT",
            "Hashtag" when iso => "KC_NUHS", "BackslashLeft" when iso => "KC_NUBS", "Backslash" => iso ? "KC_NUBS" : "KC_BSLS",
            "Comma" => "KC_COMM", "Period" => "KC_DOT", "Slash" => "KC_SLSH", "Space" => "KC_SPC", "Menu" => "KC_APP",
            "ControlLeft" => "KC_LCTL", "ControlRight" => "KC_RCTL", "ShiftLeft" => "KC_LSFT", "ShiftRight" => "KC_RSFT",
            "AltLeft" => "KC_LALT", "AltRight" => "KC_RALT", "MetaLeft" => "KC_LGUI", "MetaRight" => "KC_RGUI",
            "Insert" => "KC_INS", "Delete" => "KC_DEL", "Home" => "KC_HOME", "End" => "KC_END", "PageUp" => "KC_PGUP", "PageDown" => "KC_PGDN",
            "ArrowUp" => "KC_UP", "ArrowDown" => "KC_DOWN", "ArrowLeft" => "KC_LEFT", "ArrowRight" => "KC_RGHT",
            "NumLock" => "KC_NUM", "NumpadDivide" => "KC_PSLS", "NumpadMultiply" => "KC_PAST", "NumpadSubtract" => "KC_PMNS", "NumpadAdd" => "KC_PPLS", "NumpadEnter" => "KC_PENT", "NumpadDecimal" => "KC_PDOT",
            _ => null
        };
    }

    private static byte[] Read(FileStream file) { if (file.Length > MaximumFileBytes) throw Invalid(); var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); if (file.ReadByte() != -1) throw Invalid(); return bytes; }
    private static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> bytes) => bytes.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes;
    private static string String(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
    private static JsonElement[] Array(JsonElement value, string name) { var result = value.GetProperty(name).EnumerateArray().ToArray(); if (result.Length > 4096) throw Invalid(); return result; }
    private static int Integer(JsonElement value, string name, int min, int max) { var result = value.GetProperty(name).GetInt32(); if (result < min || result > max) throw Invalid(); return result; }
    private static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw Invalid(); CheckUniqueProperties(p.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckUniqueProperties(item);
    }
    private static InvalidDataException Invalid() => new(L.T("Die CORE-Dateien sind unvollständig oder ungültig. Wähle den datastore-Ordner mit Devices.json und DeviceProfiles.json."));
    private static InvalidDataException Ambiguous() => new(L.T("Die CORE-Einstellungen können diesem Modell nicht eindeutig zugeordnet werden."));
}
