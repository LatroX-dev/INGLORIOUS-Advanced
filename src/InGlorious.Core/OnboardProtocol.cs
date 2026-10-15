using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace InGlorious.Core;

public sealed record OnboardImage(string DeviceId, byte[] Payload, int MacroEvents, int KeyCount, int LedCount, IReadOnlyList<string> Warnings)
{
    public int UsedBytes => Payload.Length;
    public int CapacityBytes => BoardDescriptorRegistry.Get(DeviceId).PayloadCapacity;
    public int MacroCapacity => OnboardSerializer.MacroCapacity(DeviceId, SchemaVersion);
    public uint Crc32 => OnboardProtocol.Crc32(Payload);
    public int SchemaVersion => Payload.Length >= 6 ? OnboardSerializer.Get16(Payload, 4) : 0;
    public bool RequiresEffects => SchemaVersion >= 2 && OnboardSerializer.HasEffects(Payload);
    public bool RequiresTiming => SchemaVersion >= 2 && Payload.Length >= 26 && (Payload[24] != 1 || Payload[25] != 5);
    public bool RequiresColorSequences => SchemaVersion == 3 || SchemaVersion == 4 && Payload[OnboardSerializer.SequenceOffset(DeviceId)] != 0;
    public bool RequiresStandby => SchemaVersion == 4;
    public bool RequiresScreensaver => SchemaVersion == 4 && Payload.Length > 29 && Payload[29] == 2;
}

public static class OnboardSerializer
{
    private static readonly Dictionary<string, ushort> Codes = BuildCodes();
    private static readonly Dictionary<ushort, string> Names = Codes.ToDictionary(kv => kv.Value, kv => kv.Key);
    public static (byte Board, int Rows, int Cols, int Leds, int Size) Shape(string id, int schemaVersion = 1)
    {
        var shape = BoardDescriptorRegistry.Get(id);
        return (shape.BoardId, shape.Rows, shape.Cols, shape.LedCount, ValidateShape(shape, schemaVersion));
    }
    internal static int ValidateShape(BoardDescriptor shape, int schemaVersion)
    {
        if (schemaVersion is not 1 and not 2 and not 3 and not 4) throw new InvalidDataException("Unbekanntes Geräte-Konfigurationsformat.");
        if (shape.BoardId == 0 || shape.Rows is < 1 or > 255 || shape.Cols is < 1 or > 255 || shape.LedCount is < 1 or > 255 || shape.PayloadCapacity is < 1 or > ushort.MaxValue)
            throw new InvalidDataException("Ungültige Konfigurationsabmessungen.");
        var legacy = 3248 + 8 * shape.Rows * shape.Cols + 12 * shape.LedCount;
        var size = legacy + (schemaVersion >= 2 ? 32 + 12 * shape.LedCount : 0);
        if (shape.LegacySize != legacy || size > shape.PayloadCapacity) throw new InvalidDataException("Konfiguration überschreitet den Speicher dieses Geräts.");
        return size;
    }
    public static int SequenceStorageBytes(string deviceId) => (200 + 2 * Shape(deviceId).Leds + 3) & ~3;
    public static int MacroCapacity(string deviceId, int schemaVersion) => 768 - (schemaVersion >= 3 ? SequenceStorageBytes(deviceId) / 4 : 0);
    public static int SequenceOffset(string deviceId) => Shape(deviceId).Size - SequenceStorageBytes(deviceId);

    private static bool DefaultEffect(LightingEffectSettings effect) =>
        effect.Mode == 0 && effect.Speed == 128 && effect.Brightness == 255 && effect.Target == 3 && !effect.Reverse && effect.Colors.Count == 0;

    /// <summary>Validates an editable profile. Success does not grant protocol or firmware support to an editor-only model.</summary>
    public static void ValidateEditorProject(AppProject project)
    {
        if (BoardDescriptorRegistry.TryGet(project.DeviceId, out var board) && !board.IsEngineeringOnly) { _ = Prepare(project); return; }
        var errors = ProjectService.Validate(project);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
        var device = DeviceCatalog.Get(project.DeviceId);
        foreach (var layer in project.Layers)
        foreach (var key in device.Keys)
        {
            var code = EncodeBinding(layer.Keys[key.Id].Binding, project.Macros);
            if (key.IsEncoder && code is >= 0x5220 and <= 0x5223)
                throw new InvalidDataException("Ein Drehgeber kann keine Ebene gedrückt halten. Verwende Umschalten oder eine normale Taste.");
        }
        foreach (var macro in project.Macros)
        foreach (var step in macro.Steps.Where(step => step.Kind != "delay"))
        {
            var code = EncodeBinding(step.Key, project.Macros);
            if (!(code is >= 4 and <= 0xA4 or >= 0xE0 and <= 0xE7))
                throw new InvalidDataException($"Makro „{macro.Name}“: {step.Key} ist kein unterstützter Tastaturcode. Medienaktionen sind als direkte Belegung möglich.");
        }
    }

    internal static bool HasEffects(byte[] data)
    {
        var shape = Shape(BoardDescriptorRegistry.GetByBoardId(data[6]).DeviceId);
        for (var layer = 0; layer < 4; layer++)
        {
            var offset = shape.Size + layer * 8;
            if (data[offset] != 0 || data[offset + 1] != 128 || data[offset + 2] != 255 || data[offset + 3] != 3 || (data[offset + 4] & 1) != 0) return true;
            for (var led = 0; led < shape.Leds; led++)
                if (data[shape.Size + 32 + (layer * shape.Leds + led) * 3] != 255) return true;
        }
        return false;
    }

    public static OnboardImage Prepare(AppProject project, bool allowEngineering = false)
    {
        if (DeviceCatalog.Get(project.DeviceId).IsOfflineOnly && !EngineeringConfigurationPolicy.IsEnabled(project.DeviceId, allowEngineering))
            throw new InvalidDataException(L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar."));
        var errors = ProjectService.Validate(project);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
        // Side synchronization alone remains an editor preference, as it was in schema 1.
        var hasEffects = project.Layers.Any(layer => !DefaultEffect(layer.LightingEffect) || layer.LedEffects.Count != 0);
        var hasTiming = project.DeviceSettings.UsbPollingIntervalMs != 1 || project.DeviceSettings.DebounceMs != 5;
        var hasColors = project.Layers.Any(l => l.LightingEffect.Colors.Count != 0 || l.LedEffects.Values.Any(e => e.Colors.Count != 0));
        var schemaVersion = !project.Standby.IsDefault ? 4 : hasColors ? 3 : hasEffects || hasTiming ? 2 : 1;
        var s = Shape(project.DeviceId, schemaVersion);
        var device = DeviceCatalog.Get(project.DeviceId);
        var used = project.Macros.Sum(m => m.Steps.Count);
        var macroCapacity = MacroCapacity(project.DeviceId, schemaVersion);
        if (used > macroCapacity) throw new InvalidDataException(L.F($"Die Makros benötigen {used} Schritte. Mit dieser Konfiguration stehen {macroCapacity} zur Verfügung; es wird nichts gekürzt."));
        var data = new byte[s.Size];
        "IGCF"u8.CopyTo(data);
        Put16(data, 4, schemaVersion); data[6] = s.Board; data[7] = 4;
        data[8] = (byte)s.Rows; data[9] = (byte)s.Cols; data[10] = (byte)s.Leds; data[11] = 32;
        Put16(data, 12, macroCapacity); Put16(data, 14, used);
        data[16] = (byte)project.Slider.Mode;
        Put16(data, 18, project.Slider.Minimum); Put16(data, 20, project.Slider.Maximum);
        data[22] = (byte)project.Slider.MidiChannel; data[23] = (byte)project.Slider.MidiController;
        if (schemaVersion >= 2) { data[24] = (byte)project.DeviceSettings.UsbPollingIntervalMs; data[25] = (byte)project.DeviceSettings.DebounceMs; }
        if (schemaVersion == 4)
        {
            data[26] = (byte)(project.Standby.Enabled ? 1 : 0);
            Put16(data, 27, project.Standby.TimeoutSeconds);
            data[29] = (byte)project.Standby.Mode;
            data[30] = (byte)project.Standby.DimBrightnessPercent;
        }
        var encoderOffset = 32 + 4 * s.Rows * s.Cols * 2;
        var rgbOffset = encoderOffset + 16;
        for (var layer = 0; layer < 4; layer++)
        {
            var config = project.Layers[layer];
            foreach (var key in device.Keys)
            {
                var assignment = config.Keys[key.Id];
                var code = EncodeBinding(assignment.Binding, project.Macros);
                if (key.IsEncoder)
                {
                    if (code is >= 0x5220 and <= 0x5223) throw new InvalidDataException("Ein Drehgeber kann keine Ebene gedrückt halten. Verwende Umschalten oder eine normale Taste.");
                    Put16(data, encoderOffset + layer * 4 + (key.Id == "encoder:ccw" ? 0 : 2), code);
                }
                else
                {
                    var (row, col) = Matrix(key.Id);
                    Put16(data, 32 + ((layer * s.Rows + row) * s.Cols + col) * 2, code);
                }
            }
            for (var led = 0; led < s.Leds; led++)
            {
                var key = device.Keys.FirstOrDefault(k => k.LedIndex == led);
                var color = key != null ? config.Keys[key.Id].Color : config.ExtraLedColors.GetValueOrDefault(led, config.SideLedColor);
                Convert.FromHexString(color[1..]).CopyTo(data, rgbOffset + (layer * s.Leds + led) * 3);
            }
        }
        var descriptorOffset = rgbOffset + 4 * s.Leds * 3;
        var eventsOffset = descriptorOffset + 128;
        var eventIndex = 0;
        for (var macroIndex = 0; macroIndex < project.Macros.Count; macroIndex++)
        {
            var macro = project.Macros[macroIndex];
            Put16(data, descriptorOffset + macroIndex * 4, eventIndex);
            Put16(data, descriptorOffset + macroIndex * 4 + 2, macro.Steps.Count);
            foreach (var step in macro.Steps)
            {
                var offset = eventsOffset + eventIndex++ * 4;
                data[offset] = (byte)(step.Kind == "down" ? 1 : step.Kind == "up" ? 2 : 3);
                var value = step.Kind == "delay" ? step.DelayMs : EncodeBinding(step.Key, project.Macros);
                if (step.Kind != "delay" && !(value is >= 4 and <= 0xA4 or >= 0xE0 and <= 0xE7))
                    throw new InvalidDataException($"Makro „{macro.Name}“: {step.Key} ist kein unterstützter Tastaturcode. Medienaktionen sind als direkte Belegung möglich.");
                Put16(data, offset + 2, value);
            }
        }
        if (schemaVersion >= 2)
        {
            var effectsOffset = Shape(project.DeviceId).Size;
            for (var layer = 0; layer < 4; layer++)
            {
                var effect = project.Layers[layer].LightingEffect;
                var offset = effectsOffset + layer * 8;
                data[offset] = (byte)effect.Mode;
                data[offset + 1] = (byte)effect.Speed;
                data[offset + 2] = (byte)effect.Brightness;
                data[offset + 3] = (byte)effect.Target;
                data[offset + 4] = (byte)((effect.Reverse ? 1 : 0) | (project.Layers[layer].SynchronizeSides ? 2 : 0));
                for (var led = 0; led < s.Leds; led++)
                {
                    var ledOffset = effectsOffset + 32 + (layer * s.Leds + led) * 3;
                    if (project.Layers[layer].LedEffects.TryGetValue(led, out var individual))
                    {
                        data[ledOffset] = (byte)(individual.Mode | (individual.Reverse ? 128 : 0));
                        data[ledOffset + 1] = (byte)individual.Speed;
                        data[ledOffset + 2] = (byte)individual.Brightness;
                    }
                    else data[ledOffset] = 255; // FF 00 00 is the only canonical inherited record.
                }
            }
        }
        if (hasColors) WriteSequences(project, data);
        var warnings = new List<string> { "Profil-, Ebenen- und Makronamen bleiben in der Profildatei; im Gerät werden die Funktionen gespeichert." };
        if (hasEffects) warnings.Add("Effekte und erweiterte Beleuchtungseinstellungen benötigen INGLORIOUS-Firmware mit Effektunterstützung.");
        if (hasTiming) warnings.Add("USB-Abfrageintervall und Entprellzeit benötigen Firmware mit Unterstützung für Geräteeinstellungen. Ein geändertes USB-Abfrageintervall wird erst nach erneuter USB-Anmeldung wirksam.");
        if (project.Slider.Mode == 1) warnings.Add("MIDI benötigt einen MIDI-fähigen Empfänger.");
        if (project.Slider.Mode == 2) warnings.Add("Der Schieberegler sendet relative Lautstärkeschritte; seine Position entspricht keiner absoluten Windows-Lautstärke.");
        return new(project.DeviceId, data, used, device.Keys.Count, s.Leds, warnings);
    }

    private static void WriteSequences(AppProject project, byte[] data)
    {
        var shape = Shape(project.DeviceId); var poolOffset = SequenceOffset(project.DeviceId);
        var identities = new List<string>();
        byte Index(IReadOnlyList<string> colors)
        {
            if (colors.Count == 0) return 0;
            var key = ColorSequenceRules.Identity(colors); var found = identities.IndexOf(key);
            if (found >= 0) return checked((byte)(found + 1));
            if (identities.Count == 8) throw new InvalidDataException(L.T("Der Gerätespeicher fasst höchstens acht unterschiedliche Farbfolgen. Gleiche Folgen teilen sich einen Speicherplatz."));
            var offset = poolOffset + identities.Count * 25; identities.Add(key);
            data[offset] = checked((byte)colors.Count);
            for (var i = 0; i < colors.Count; i++) Convert.FromHexString(colors[i][1..]).CopyTo(data, offset + 1 + i * 3);
            return checked((byte)identities.Count);
        }
        for (var layer = 0; layer < 4; layer++)
        {
            data[shape.Size + layer * 8 + 5] = Index(project.Layers[layer].LightingEffect.Colors);
            for (var led = 0; led < shape.Leds; led++)
            {
                var at = layer * shape.Leds + led;
                var id = project.Layers[layer].LedEffects.TryGetValue(led, out var own) ? Index(own.Colors) : (byte)0;
                data[poolOffset + 200 + at / 2] |= checked((byte)(id << ((at & 1) * 4)));
            }
        }
    }
    private static List<string>[] ReadSequences(byte[] data, string deviceId)
    {
        var offset = SequenceOffset(deviceId); var result = new List<string>[9]; result[0] = [];
        var unused = false; var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var id = 1; id <= 8; id++)
        {
            var at = offset + (id - 1) * 25; var count = data[at];
            if (count > 8 || (unused && count != 0) || data.AsSpan(at + 1 + count * 3, 24 - count * 3).IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException(L.T("Ungültiger Farbfolgenspeicher."));
            unused |= count == 0; result[id] = [];
            for (var i = 0; i < count; i++) result[id].Add("#" + Convert.ToHexString(data.AsSpan(at + 1 + i * 3, 3)));
            if (count != 0 && !identities.Add(ColorSequenceRules.Identity(result[id]))) throw new InvalidDataException(L.T("Ungültiger Farbfolgenspeicher."));
        }
        var padding = offset + 200 + 2 * Shape(deviceId).Leds;
        if (data.AsSpan(padding, Shape(deviceId).Size - padding).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException(L.T("Ungültiger Farbfolgenspeicher."));
        return result;
    }
    public static AppProject Decode(byte[] data, bool allowEngineering = false)
    {
        if (data.Length < 32 || !data.AsSpan(0, 4).SequenceEqual("IGCF"u8) || Get16(data, 4) is not 1 and not 2 and not 3 and not 4)
            throw new InvalidDataException("Unbekanntes Geräte-Konfigurationsformat.");
        var schemaVersion = Get16(data, 4);
        var id = BoardDescriptorRegistry.GetByBoardId(data[6]).DeviceId;
        if (DeviceCatalog.Get(id).IsOfflineOnly && !EngineeringConfigurationPolicy.IsEnabled(id, allowEngineering))
            throw new InvalidDataException(L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar."));
        var s = Shape(id, schemaVersion);
        var legacySize = Shape(id).Size;
        var macroCapacity = MacroCapacity(id, schemaVersion);
        if (data.Length != s.Size || data[7] != 4 || data[8] != s.Rows || data[9] != s.Cols || data[10] != s.Leds || data[11] != 32 || Get16(data, 12) != macroCapacity || Get16(data, 14) > macroCapacity || data[17] != 0 || data.AsSpan(schemaVersion == 1 ? 24 : schemaVersion == 4 ? 31 : 26, schemaVersion == 1 ? 8 : schemaVersion == 4 ? 1 : 6).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Unpassende Konfigurationsgröße oder reservierte Daten.");
        var result = ProjectService.Create(id);
        result.Name = DeviceCatalog.Get(id).Name;
        result.Slider = new() { Mode = data[16], Minimum = Get16(data, 18), Maximum = Get16(data, 20), MidiChannel = data[22], MidiController = data[23] };
        if (schemaVersion >= 2) result.DeviceSettings = new() { UsbPollingIntervalMs = data[24], DebounceMs = data[25] };
        if (schemaVersion == 4)
        {
            if (data[26] > 1) throw new InvalidDataException(L.T("Ungültige Standby-Einstellungen."));
            result.Standby = new() { Enabled = data[26] == 1, TimeoutSeconds = Get16(data, 27), Mode = data[29], DimBrightnessPercent = data[30] };
        }
        var sequences = schemaVersion >= 3 ? ReadSequences(data, id) : null;
        var sequenceSeen = 0;
        List<string> Sequence(int index)
        {
            if (index == 0) return [];
            if (sequences is null || index > 8 || sequences[index].Count == 0) throw new InvalidDataException(L.T("Ungültiger Farbfolgenverweis."));
            // First-use order is canonical, so decode/re-encode is byte-exact.
            if (index > sequenceSeen + 1) throw new InvalidDataException(L.T("Ungültiger Farbfolgenverweis."));
            sequenceSeen = Math.Max(sequenceSeen, index); return [.. sequences[index]];
        }
        var encoderOffset = 32 + 4 * s.Rows * s.Cols * 2;
        var rgbOffset = encoderOffset + 16;
        var descriptorOffset = rgbOffset + 4 * s.Leds * 3;
        var eventsOffset = descriptorOffset + 128;
        var used = Get16(data, 14);
        for (var eventIndex = 0; eventIndex < used; eventIndex++)
        {
            var offset = eventsOffset + eventIndex * 4;
            var kind = data[offset]; var value = Get16(data, offset + 2);
            if (kind is < 1 or > 3 || data[offset + 1] != 0 || (kind == 3 ? value > 60000 : !(value is >= 4 and <= 0xA4 or >= 0xE0 and <= 0xE7)))
                throw new InvalidDataException("Ungültiges Ereignis im Makrospeicher.");
        }
        var macroIds = new Dictionary<int, string>();
        for (var macroIndex = 0; macroIndex < 32; macroIndex++)
        {
            var start = Get16(data, descriptorOffset + macroIndex * 4);
            var count = Get16(data, descriptorOffset + macroIndex * 4 + 2);
            if (count > 128 || start + count > used || (count == 0 && start != 0)) throw new InvalidDataException("Ungültiger Makrobereich.");
            if (count == 0) continue;
            var macro = new MacroDefinition { Id = "device-" + macroIndex, Name = "Makro " + (macroIndex + 1) };
            macroIds[macroIndex] = macro.Id;
            for (var i = 0; i < count; i++)
            {
                var p = eventsOffset + (start + i) * 4;
                var kind = data[p]; var value = Get16(data, p + 2);
                if (data[p + 1] != 0 || kind is < 1 or > 3) throw new InvalidDataException("Ungültiges Makroereignis.");
                macro.Steps.Add(kind == 3 ? new() { Kind = "delay", DelayMs = value } : new() { Kind = kind == 1 ? "down" : "up", Key = DecodeBinding(value, macroIds, false) });
            }
            result.Macros.Add(macro);
        }
        var unusedEventsOffset = eventsOffset + used * 4;
        var eventsEnd = schemaVersion >= 3 ? SequenceOffset(id) : legacySize;
        if (data.AsSpan(unusedEventsOffset, eventsEnd - unusedEventsOffset).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("Nichtleerer unbenutzter Makrospeicher.");
        var device = DeviceCatalog.Get(id);
        if (!device.HasEncoder && data.AsSpan(encoderOffset, 16).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Belegung eines nicht vorhandenen Drehgebers.");
        for (var layer = 0; layer < 4; layer++)
        {
            if (schemaVersion >= 2)
            {
                var offset = legacySize + layer * 8;
                if (data[offset] > 20 || data[offset + 3] is < 1 or > 3 || (data[offset + 4] & ~3) != 0 || (schemaVersion < 3 && data[offset + 5] != 0) || data[offset + 6] != 0 || data[offset + 7] != 0)
                    throw new InvalidDataException("Ungültige Beleuchtungseinstellungen oder reservierte Effektdaten.");
                result.Layers[layer].LightingEffect = new()
                {
                    Mode = data[offset], Speed = data[offset + 1], Brightness = data[offset + 2],
                    Target = data[offset + 3], Reverse = (data[offset + 4] & 1) != 0, Colors = Sequence(data[offset + 5])
                };
                result.Layers[layer].SynchronizeSides = (data[offset + 4] & 2) != 0;
                for (var led = 0; led < s.Leds; led++)
                {
                    var ledOffset = legacySize + 32 + (layer * s.Leds + led) * 3;
                    var modeAndFlags = data[ledOffset];
                    var at = layer * s.Leds + led;
                    var sequenceId = schemaVersion >= 3 ? (data[SequenceOffset(id) + 200 + at / 2] >> ((at & 1) * 4)) & 15 : 0;
                    if (modeAndFlags == 255)
                    {
                        if (data[ledOffset + 1] != 0 || data[ledOffset + 2] != 0 || sequenceId != 0) throw new InvalidDataException("Ungültiger geerbter LED-Effekt.");
                        continue;
                    }
                    if ((modeAndFlags & 0x60) != 0 || (modeAndFlags & 0x1F) > 20) throw new InvalidDataException("Ungültiger LED-Effekt oder reservierte Effektdaten.");
                    result.Layers[layer].LedEffects[led] = new()
                    {
                        Mode = modeAndFlags & 0x1F, Reverse = (modeAndFlags & 128) != 0,
                        Speed = data[ledOffset + 1], Brightness = data[ledOffset + 2], Colors = Sequence(sequenceId)
                    };
                }
            }
            for (var row = 0; row < s.Rows; row++) for (var col = 0; col < s.Cols; col++)
            {
                var keyId = $"r{row}c{col}";
                var code = Get16(data, 32 + ((layer * s.Rows + row) * s.Cols + col) * 2);
                if (device.Keys.Any(k => k.Id == keyId)) result.Layers[layer].Keys[keyId].Binding = DecodeBinding(code, macroIds, true);
                else if (code != 0) throw new InvalidDataException("Belegung einer nicht vorhandenen Taste.");
            }
            foreach (var key in device.Keys.Where(k => k.IsEncoder))
                result.Layers[layer].Keys[key.Id].Binding = DecodeBinding(Get16(data, encoderOffset + layer * 4 + (key.Id == "encoder:ccw" ? 0 : 2)), macroIds, true);
            for (var led = 0; led < s.Leds; led++)
            {
                var color = "#" + Convert.ToHexString(data.AsSpan(rgbOffset + (layer * s.Leds + led) * 3, 3));
                var key = device.Keys.FirstOrDefault(k => k.LedIndex == led);
                if (key != null) result.Layers[layer].Keys[key.Id].Color = color;
                else result.Layers[layer].ExtraLedColors[led] = color;
            }
            result.Layers[layer].SideLedColor = result.Layers[layer].ExtraLedColors.Values.FirstOrDefault() ?? "#8374F8";
        }
        if (sequences is not null && (schemaVersion == 3 && sequenceSeen == 0 || sequences.Skip(1).Count(c => c.Count != 0) != sequenceSeen)) throw new InvalidDataException(L.T("Ungültiger Farbfolgenverweis."));
        _ = Prepare(result, allowEngineering); // Apply the editor's stricter macro and binding constraints before adoption.
        return result;
    }

    private static (int Row, int Col) Matrix(string id)
    {
        var separator = id.IndexOf('c');
        return (int.Parse(id.AsSpan(1, separator - 1)), int.Parse(id.AsSpan(separator + 1)));
    }
    private static ushort EncodeBinding(string binding, IReadOnlyList<MacroDefinition> macros)
    {
        if (Codes.TryGetValue(binding, out var basic)) return basic;
        if (binding.StartsWith("MACRO:", StringComparison.Ordinal))
        {
            var index = macros.ToList().FindIndex(m => m.Id == binding[6..]);
            if (index is >= 0 and < 32) return (ushort)(0x7700 + index);
        }
        foreach (var (prefix, value) in new[] { ("TO(", 0x5200), ("MO(", 0x5220), ("TG(", 0x5260) })
            if (binding.Length == prefix.Length + 2 && binding.StartsWith(prefix, StringComparison.Ordinal) && binding[^1] == ')' && binding[^2] is >= '0' and <= '3') return (ushort)(value + binding[^2] - '0');
        throw new InvalidDataException($"„{binding}“ wird von der INGLORIOUS-Firmware noch nicht unterstützt.");
    }
    private static string DecodeBinding(ushort code, IReadOnlyDictionary<int, string> macroIds, bool allowActions)
    {
        if (Names.TryGetValue(code, out var name)) return name;
        if (allowActions)
        {
            if (code is >= 0x7700 and <= 0x771F && macroIds.TryGetValue(code - 0x7700, out var id)) return "MACRO:" + id;
            foreach (var (namePrefix, value) in new[] { ("TO", 0x5200), ("MO", 0x5220), ("TG", 0x5260) })
                if (code >= value && code < value + 4) return $"{namePrefix}({code - value})";
        }
        throw new InvalidDataException($"Gerätecode 0x{code:X4} kann vom Editor nicht verlustfrei dargestellt werden.");
    }
    internal static ushort Get16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    internal static void Put16(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], checked((ushort)value));
    private static Dictionary<string, ushort> BuildCodes()
    {
        var result = new Dictionary<string, ushort>(StringComparer.Ordinal);
        void Add(string name, int value) => result.Add(name, checked((ushort)value));
        Add("KC_NO", 0); Add("KC_TRNS", 1);
        for (var i = 0; i < 26; i++) Add("KC_" + (char)('A' + i), 4 + i);
        for (var i = 1; i <= 9; i++) Add("KC_" + i, 0x1D + i);
        Add("KC_0", 0x27); Add("KC_APP", 0x65);
        var consecutive = "KC_ENT KC_ESC KC_BSPC KC_TAB KC_SPC KC_MINS KC_EQL KC_LBRC KC_RBRC KC_BSLS KC_NUHS KC_SCLN KC_QUOT KC_GRV KC_COMM KC_DOT KC_SLSH KC_CAPS".Split(' ');
        for (var i = 0; i < consecutive.Length; i++) Add(consecutive[i], 0x28 + i);
        for (var i = 1; i <= 12; i++) Add("KC_F" + i, 0x39 + i);
        consecutive = "KC_PSCR KC_SCRL KC_PAUS KC_INS KC_HOME KC_PGUP KC_DEL KC_END KC_PGDN KC_RGHT KC_LEFT KC_DOWN KC_UP KC_NUM KC_PSLS KC_PAST KC_PMNS KC_PPLS KC_PENT KC_P1 KC_P2 KC_P3 KC_P4 KC_P5 KC_P6 KC_P7 KC_P8 KC_P9 KC_P0 KC_PDOT KC_NUBS".Split(' ');
        for (var i = 0; i < consecutive.Length; i++) Add(consecutive[i], 0x46 + i);
        for (var i = 13; i <= 24; i++) Add("KC_F" + i, 0x68 + i - 13);
        consecutive = "KC_LCTL KC_LSFT KC_LALT KC_LGUI KC_RCTL KC_RSFT KC_RALT KC_RGUI".Split(' ');
        for (var i = 0; i < consecutive.Length; i++) Add(consecutive[i], 0xE0 + i);
        Add("KC_MUTE", 0xA8); Add("KC_VOLU", 0xA9); Add("KC_VOLD", 0xAA); Add("KC_MNXT", 0xAB); Add("KC_MPRV", 0xAC); Add("KC_MSTP", 0xAD); Add("KC_MPLY", 0xAE); Add("KC_MAIL", 0xB1); Add("KC_CALC", 0xB2); Add("KC_MYCM", 0xB3);
        return result;
    }
}

public sealed class OnboardCommandException(byte status, string message) : IOException(message)
{
    public byte Status { get; } = status;
}

public static class OnboardProtocol
{
    public const byte Info = 1, Read = 2, Write = 3, Begin = 4, Commit = 5, Abort = 6, ReconnectUsb = 8, ReadRuntimeBrightness = 9, SetRuntimeBrightness = 10, GetExtensions = 11, LightingTest = 12, GetFirmwareVersion = 13;
    public const byte DeviceTest = 14, DeviceTestLed = 15;
    public static byte[] Packet(byte command, byte sequence, ReadOnlySpan<byte> payload)
    {
        if ((command is < 1 or > 6 && command is not ReconnectUsb and not ReadRuntimeBrightness and not SetRuntimeBrightness and not GetExtensions and not LightingTest and not GetFirmwareVersion and not DeviceTest and not DeviceTestLed) || payload.Length > 24 || (command is ReconnectUsb or ReadRuntimeBrightness or GetExtensions or GetFirmwareVersion && payload.Length != 0) || (command == SetRuntimeBrightness && payload.Length != 1) || (command == LightingTest && (payload.Length != 1 || payload[0] > 2)) || (command == DeviceTest && (payload.Length != 1 || payload[0] > 3)) || (command == DeviceTestLed && (payload.Length != 5 || payload[0] > 2 || (payload[0] != 2 && payload[1] != 0) || (payload[0] == 0 && payload[2..].IndexOfAnyExcept((byte)0) >= 0)))) throw new ArgumentOutOfRangeException(nameof(payload));
        var result = new byte[32]; result[0] = (byte)'I'; result[1] = (byte)'G'; result[2] = 1; result[3] = command; result[4] = sequence; result[6] = (byte)payload.Length;
        payload.CopyTo(result.AsSpan(8)); return result;
    }
    public static void ValidateRequest(byte[] packet)
    {
        if (packet.Length != 32 || packet[0] != 'I' || packet[1] != 'G' || packet[2] != 1 || packet[5] != 0 || packet[7] != 0 || packet[6] > 24 ||
            packet.AsSpan(8 + packet[6]).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid INGLORIOUS configuration packet.");
        try { _ = Packet(packet[3], packet[4], packet.AsSpan(8, packet[6])); }
        catch (ArgumentOutOfRangeException error) { throw new InvalidDataException("Invalid INGLORIOUS configuration packet.", error); }
    }
    public static byte[] ParseResponse(byte[] packet, byte command, byte sequence)
    {
        if (packet.Length != 32 || packet[0] != 'I' || packet[1] != 'G' || packet[2] != 1 || packet[3] != command || packet[4] != sequence || packet[6] > 24 || packet[7] != 0 || packet.AsSpan(8 + packet[6]).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Ungültige oder veraltete Geräteantwort.");
        if (packet[5] != 0) throw new OnboardCommandException(packet[5], "Gerät hat den Vorgang abgelehnt: " + (packet[5] switch { 3 => "Taste oder Makro aktiv. Bitte loslassen und erneut versuchen.", 5 => "Prüfsumme stimmt nicht.", 6 => "Ungültige Konfiguration.", 7 => "Speichern fehlgeschlagen.", 8 => "Konfiguration zwischenzeitlich geändert.", _ => "Status " + packet[5] }));
        return packet.AsSpan(8, packet[6]).ToArray();
    }
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (var b in bytes) { value ^= b; for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0); }
        return ~value;
    }
}
