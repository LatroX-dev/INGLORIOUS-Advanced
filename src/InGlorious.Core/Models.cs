using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InGlorious.Core;

public record KeyDefinition(string Id, string Label, double X, double Y, double W = 1, double H = 1, string DefaultBinding = "KC_NO")
{
    public int? LedIndex { get; init; }
    public bool IsIsoEnter { get; init; }
    public bool IsEncoder => Id.StartsWith("encoder:", StringComparison.Ordinal);
    public string DisplayLabel => Label == "Knob" ? L.T("Drücken") : BindingCatalog.CompactLegend(Label);
    public string AccessibleLabel => Id switch
    {
        "encoder:ccw" => L.T("Nach links drehen"),
        "encoder:cw" => L.T("Nach rechts drehen"),
        _ => Label == "Knob" ? L.T("Drehregler drücken") : L.T(Label)
    };
}
// Preview geometry in key units, independent of QMK's normalized effect coordinates.
// A physical badge can exist without a separately addressable LED in the reviewed port.
public record BadgeGeometry(double X, double Y, double W, double H, int? LedIndex)
{
    public bool IsHorizontal => W > H;
}
public record DeviceDefinition(string Id, string Name, string SourceSubtitle, string ProductId, IReadOnlyList<KeyDefinition> Keys, double Width, double Height)
{
    internal string LayoutReferenceId { get; init; } = Id;
    public string Subtitle => L.T(SourceSubtitle);
    public bool SupportsOnboardConfiguration { get; init; }
    public bool IsOfflineOnly => !SupportsOnboardConfiguration;
    public bool IsExperimentalFirmware { get; init; }
    public bool HasApprovedFirmwareManagement { get; init; }
    public string OfflineModeLabel => IsOfflineOnly ? L.T("Offline-Profil") : "";
    public bool HasEncoder { get; init; }
    public bool HasSlider { get; init; }
    public string HardwareRevision { get; init; } = "";
    public double KeyAreaHeight { get; init; }
    public int LedCount { get; init; }
    public IReadOnlyList<int> NonKeyLedIndices { get; init; } = [];
    public IReadOnlyList<int> BadgeLedIndices { get; init; } = [];
    public BadgeGeometry? BadgeGeometry { get; init; }
    public double PreviewTopInset => Math.Max(0, -Math.Min(Keys.Min(key => key.Y), BadgeGeometry?.Y ?? 0));
    public double PreviewWidth => Math.Max(Math.Max(Width, Keys.Max(key => key.X + key.W)), (BadgeGeometry?.X ?? 0) + (BadgeGeometry?.W ?? 0));
    public double PreviewHeight => Math.Max(Height, (BadgeGeometry?.Y ?? 0) + (BadgeGeometry?.H ?? 0)) + PreviewTopInset;
    public IReadOnlyList<int> SideLedIndices => LeftSideLedIndices.Concat(RightSideLedIndices).ToArray();
    public IReadOnlyList<int> LeftSideLedIndices { get; init; } = [];
    public IReadOnlyList<int> RightSideLedIndices { get; init; } = [];
}
public record BindingOption(string Code, string SourceLabel)
{
    public bool ShowCode { get; init; }
    public string Label => L.T(SourceLabel) + (ShowCode ? $"  ·  {Code}" : "");
}
public record LightingEffectOption(int Id, string SourceLabel, string SourceDescription, bool HasDirection)
{
    public string Label => L.T(SourceLabel);
    public string Description => L.T(SourceDescription);
}

public static class LightingEffects
{
    public static IReadOnlyList<LightingEffectOption> All { get; } =
    [
        new(0, "Statisch", "Zeigt die eingestellten Farben jeder Taste und Seiten-LED.", false),
        new(1, "Atmen", "Blendet die eingestellten Einzelfarben gemeinsam ein und aus.", false),
        new(2, "Farbwechsel", "Wechselt den Farbton gemeinsam. Verwendet Effektfarben statt der Einzelfarben.", false),
        new(3, "Regenbogen horizontal", "Bewegt einen Regenbogen waagerecht. Verwendet Effektfarben statt der Einzelfarben.", true),
        new(4, "Regenbogen vertikal", "Bewegt einen Regenbogen senkrecht. Verwendet Effektfarben statt der Einzelfarben.", true),
        new(5, "Welle horizontal", "Bewegt eine Helligkeitswelle waagerecht durch die eingestellten Einzelfarben.", true),
        new(6, "Welle vertikal", "Bewegt eine Helligkeitswelle senkrecht durch die eingestellten Einzelfarben.", true),
        new(7, "Scanner", "Bewegt einen Lichtstreifen durch die eingestellten Einzelfarben.", true),
        new(8, "Funkeln", "Lässt einzelne LEDs in ihren eingestellten Farben aufleuchten.", false),
        new(9, "Tastenreaktion", "Tastendrücke lassen LEDs in ihren eingestellten Farben aufleuchten.", false),
        new(10, "Wellen bei Tastendruck", "Tastendrücke erzeugen Helligkeitswellen in den eingestellten Einzelfarben.", false),
        new(11, "Regenbogen bei Tastendruck", "Tastendrücke erzeugen Regenbogenfarben. Verwendet Effektfarben statt der Einzelfarben.", false),
        new(12, "Regen", "Bewegt farbige Lichtpunkte senkrecht. Verwendet Effektfarben statt der Einzelfarben.", true),
        new(13, "Regenbogenverlauf", "Fester Regenbogenverlauf über die LEDs. Die Richtung kehrt die Farbfolge um.", true),
        new(14, "Glitzern", "Lässt einzelne LEDs kurz hell aufblitzen.", false),
        new(15, "Nachleuchten", "Lässt gedrückte Tasten langsam in ihren eingestellten Farben ausblenden.", false),
        new(16, "Schlange", "Bewegt einen Lichtpunkt mit nachlaufendem Schweif durch die LEDs.", true),
        new(17, "Schachbrett", "Wechselt zwischen versetzten hellen und dunklen Feldern.", true),
        new(18, "Doppelpuls", "Pulsiert zweimal kurz in den eingestellten Einzelfarben.", false),
        new(19, "Feuer", "Zeigt flackernde warme Effektfarben.", false),
        new(20, "Kreuzwelle", "Überlagert waagerechte und senkrechte Helligkeitswellen.", false)
    ];
}

public sealed class AppProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Konfiguration";
    public string DeviceId { get; set; } = "pro-iso";
    public List<LayerProfile> Layers { get; set; } = [];
    public List<MacroDefinition> Macros { get; set; } = [];
    public SliderSettings Slider { get; set; } = new();
    public DeviceSettings DeviceSettings { get; set; } = new();
    public LightingStandbySettings Standby { get; set; } = new();
}
public sealed class LightingStandbySettings
{
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public int Mode { get; set; }
    public int DimBrightnessPercent { get; set; } = 10;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => !Enabled && TimeoutSeconds == 300 && Mode == 0 && DimBrightnessPercent == 10;
}
public sealed class DeviceSettings
{
    public int UsbPollingIntervalMs { get; set; } = 1;
    public int DebounceMs { get; set; } = 5;
}
public sealed class SliderSettings
{
    public int Mode { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 1023;
    public int MidiChannel { get; set; }
    public int MidiController { get; set; } = 62;
}
public sealed class LayerProfile
{
    public string Name { get; set; } = "Layer";
    public Dictionary<string, KeyAssignment> Keys { get; set; } = [];
    // Preserve the fallback for older JSON files; new profiles set their color explicitly.
    public string SideLedColor { get; set; } = "#8374F8";
    public Dictionary<int, string> ExtraLedColors { get; set; } = [];
    // Stored as an effect flag in schema 2; colors always remain individual LED values.
    public bool SynchronizeSides { get; set; }
    public LightingEffectSettings LightingEffect { get; set; } = new();
    // Missing LED entries inherit the global layer effect.
    public Dictionary<int, PerLedEffectSettings> LedEffects { get; set; } = [];
}
public sealed class PerLedEffectSettings
{
    public int Mode { get; set; }
    public int Speed { get; set; } = 128;
    public int Brightness { get; set; } = 255;
    public bool Reverse { get; set; }
    public List<string> Colors { get; set; } = [];
}
public sealed class LightingEffectSettings
{
    public int Mode { get; set; }
    public int Speed { get; set; } = 128;
    public int Brightness { get; set; } = 255;
    public int Target { get; set; } = 3;
    public bool Reverse { get; set; }
    public List<string> Colors { get; set; } = [];
}
public static class GlobalEffectSwitch
{
    // Off uses the existing static, full-brightness format so it survives a device read.
    // It is not a paused effect: enabling an applied static setting starts with breathing.
    public static bool IsEnabled(LightingEffectSettings effect) => effect.Mode != 0 || effect.Brightness != 255;
    public static LightingEffectSettings Disable(LightingEffectSettings effect) => Copy(effect, 0, 255);
    public static LightingEffectSettings Enable(LightingEffectSettings effect) =>
        IsEnabled(effect) ? Copy(effect, effect.Mode, effect.Brightness) : Copy(effect, 1, 255);

    private static LightingEffectSettings Copy(LightingEffectSettings effect, int mode, int brightness) => new()
    {
        Mode = mode, Speed = effect.Speed, Brightness = brightness, Target = effect.Target,
        Reverse = effect.Reverse, Colors = [.. effect.Colors]
    };
}
public static class ColorSequenceRules
{
    public const int MaximumColors = 8, MaximumSequences = 8;
    public static bool IsValid(IReadOnlyList<string>? colors) => colors is not null && colors.Count <= MaximumColors && colors.All(c => c is not null && Regex.IsMatch(c, "\\A#[0-9A-Fa-f]{6}\\z"));
    public static string Identity(IEnumerable<string> colors) => string.Join("", colors).ToUpperInvariant();
}
public sealed class KeyAssignment
{
    public string Color { get; set; } = "#7367F0";
    public string Binding { get; set; } = "KC_NO";
}
public sealed class MacroDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Neues Makro";
    public List<MacroStep> Steps { get; set; } = [];
}
public sealed class MacroStep
{
    public string Kind { get; set; } = "down";
    public string Key { get; set; } = "KC_A";
    public int DelayMs { get; set; } = 50;
}

public static class Paths
{
    public static string WorkspaceRoot { get; } = FindRoot();
    public static string DataRoot => Path.Combine(WorkspaceRoot, "data");
    public static string UserDataRoot => ResolveUserDataRoot(Environment.GetFolderPath);
    internal static string ResolveUserDataRoot(Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> getFolderPath)
    {
        // On Unix the default overload returns an empty string when the user data
        // directory does not exist yet. Resolve its intended location without creating it.
        string folder = getFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new IOException("The per-user application data directory could not be located.");
        return Path.GetFullPath(Path.Combine(folder, "INGLORIOUS Advanced"));
    }
    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "inglorious.workspace.json"))) return dir.FullName;
        return UserDataRoot;
    }
}

public static class DeviceCatalog
{
    private const string ProBindings = "KC_ESC KC_F1 KC_F2 KC_F3 KC_F4 KC_F5 KC_F6 KC_F7 KC_F8 KC_F9 KC_F10 KC_F11 KC_F12 KC_PSCR KC_MUTE KC_GRV KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_DEL KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_PGUP KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_NUHS KC_ENT KC_PGDN KC_LSFT KC_NUBS KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_END KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_RCTL KC_LEFT KC_DOWN KC_RGHT";
    private const string NumpadBindings = "MO(1) KC_PSLS KC_PAST KC_PMNS KC_P7 KC_P8 KC_P9 KC_PPLS KC_P4 KC_P5 KC_P6 KC_CALC KC_P1 KC_P2 KC_P3 KC_PENT KC_P0 KC_PDOT";
    private const string ProAnsiBindings = "KC_ESC KC_F1 KC_F2 KC_F3 KC_F4 KC_F5 KC_F6 KC_F7 KC_F8 KC_F9 KC_F10 KC_F11 KC_F12 KC_PSCR KC_MUTE KC_GRV KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_DEL KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_BSLS KC_PGUP KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_ENT KC_PGDN KC_LSFT KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_END KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_RCTL KC_LEFT KC_DOWN KC_RGHT";
    private const string Gmmk2Ansi65Bindings = "KC_ESC KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_DEL KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_BSLS KC_PGUP KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_ENT KC_PGDN KC_LSFT KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_END KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_LEFT KC_DOWN KC_RGHT";
    private const string Gmmk2Iso65Bindings = "KC_ESC KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_DEL KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_PGUP KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_NUHS KC_ENT KC_PGDN KC_LSFT KC_NUBS KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_END KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_LEFT KC_DOWN KC_RGHT";
    private const string Gmmk2Ansi96Bindings = "KC_ESC KC_F1 KC_F2 KC_F3 KC_F4 KC_F5 KC_F6 KC_F7 KC_F8 KC_F9 KC_F10 KC_F11 KC_F12 KC_PSCR KC_DEL KC_INS KC_PGUP KC_PGDN KC_GRV KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_NUM KC_PSLS KC_PAST KC_PMNS KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_BSLS KC_P7 KC_P8 KC_P9 KC_PPLS KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_ENT KC_P4 KC_P5 KC_P6 KC_LSFT KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_P1 KC_P2 KC_P3 KC_PENT KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_RCTL KC_LEFT KC_DOWN KC_RGHT KC_P0 KC_PDOT";
    private const string Gmmk2Iso96Bindings = "KC_ESC KC_F1 KC_F2 KC_F3 KC_F4 KC_F5 KC_F6 KC_F7 KC_F8 KC_F9 KC_F10 KC_F11 KC_F12 KC_PSCR KC_DEL KC_INS KC_PGUP KC_PGDN KC_GRV KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_NUM KC_PSLS KC_PAST KC_PMNS KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_ENT KC_P7 KC_P8 KC_P9 KC_PPLS KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_NUHS KC_P4 KC_P5 KC_P6 KC_LSFT KC_NUBS KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_P1 KC_P2 KC_P3 KC_PENT KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_RCTL KC_LEFT KC_DOWN KC_RGHT KC_P0 KC_PDOT";
    private const string Gmmk365AnsiBindings = "KC_ESC KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_MUTE KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_BSLS KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_ENT KC_PGUP KC_LSFT KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_END KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_LEFT KC_DOWN KC_RGHT";
    private const string Gmmk365IsoBindings = "KC_ESC KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_MUTE KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_NUHS KC_ENT KC_PGUP KC_LSFT KC_NUBS KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_PGDN KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_LEFT KC_DOWN KC_RGHT";
    private const string Gmmk375AnsiBindings = "KC_ESC KC_F1 KC_F2 KC_F3 KC_F4 KC_F5 KC_F6 KC_F7 KC_F8 KC_F9 KC_F10 KC_F11 KC_F12 KC_MUTE KC_GRV KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_DEL KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_BSLS KC_PGUP KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_ENT KC_PGDN KC_LSFT KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_END KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_LEFT KC_DOWN KC_RGHT";
    private const string Gmmk375IsoBindings = "KC_ESC KC_F1 KC_F2 KC_F3 KC_F4 KC_F5 KC_F6 KC_F7 KC_F8 KC_F9 KC_F10 KC_F11 KC_F12 KC_MUTE KC_GRV KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_DEL KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_PGUP KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_NUHS KC_ENT KC_PGDN KC_LSFT KC_NUBS KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_END KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_LEFT KC_DOWN KC_RGHT";
    private const string Gmmk3100AnsiBindings = "KC_ESC KC_F1 KC_F2 KC_F3 KC_F4 KC_F5 KC_F6 KC_F7 KC_F8 KC_F9 KC_F10 KC_F11 KC_F12 KC_PSCR KC_SCRL KC_PAUS KC_MUTE KC_GRV KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_INS KC_HOME KC_PGUP KC_NUM KC_PSLS KC_PAST KC_PMNS KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_BSLS KC_DEL KC_END KC_PGDN KC_P7 KC_P8 KC_P9 KC_PPLS KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_ENT KC_P4 KC_P5 KC_P6 KC_LSFT KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_P1 KC_P2 KC_P3 KC_PENT KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_APP KC_RCTL KC_LEFT KC_DOWN KC_RGHT KC_P0 KC_PDOT";
    private const string Gmmk3100IsoBindings = "KC_ESC KC_F1 KC_F2 KC_F3 KC_F4 KC_F5 KC_F6 KC_F7 KC_F8 KC_F9 KC_F10 KC_F11 KC_F12 KC_PSCR KC_SCRL KC_PAUS KC_MUTE KC_GRV KC_1 KC_2 KC_3 KC_4 KC_5 KC_6 KC_7 KC_8 KC_9 KC_0 KC_MINS KC_EQL KC_BSPC KC_INS KC_HOME KC_PGUP KC_NUM KC_PSLS KC_PAST KC_PMNS KC_TAB KC_Q KC_W KC_E KC_R KC_T KC_Y KC_U KC_I KC_O KC_P KC_LBRC KC_RBRC KC_DEL KC_END KC_PGDN KC_P7 KC_P8 KC_P9 KC_PPLS KC_CAPS KC_A KC_S KC_D KC_F KC_G KC_H KC_J KC_K KC_L KC_SCLN KC_QUOT KC_NUHS KC_ENT KC_P4 KC_P5 KC_P6 KC_LSFT KC_NUBS KC_Z KC_X KC_C KC_V KC_B KC_N KC_M KC_COMM KC_DOT KC_SLSH KC_RSFT KC_UP KC_P1 KC_P2 KC_P3 KC_PENT KC_LCTL KC_LGUI KC_LALT KC_SPC KC_RALT MO(1) KC_APP KC_RCTL KC_LEFT KC_DOWN KC_RGHT KC_P0 KC_PDOT";
    // Definitions describe editor layouts, never permission to open or flash a device.
    private sealed record Descriptor(string Id, string Name, string Subtitle, string ProductId, string Bindings,
        string HardwareRevision, string? EncoderKeyId = null, bool HasSlider = false, bool HasApprovedFirmwareManagement = false,
        bool SupportsOnboardConfiguration = false,
        string LayoutName = "LAYOUT", int LeftSideX = 0, int RightSideX = 224, bool AnsiLegends = false,
        string? LayoutReferenceId = null);
    public static IReadOnlyList<DeviceDefinition> All { get; } = new Descriptor[]
    {
        new("pro-iso", "GMMK PRO ISO", "83 Tasten · Drehgeber · USB", "5046", ProBindings, "rev1", EncoderKeyId: "r0c1", HasApprovedFirmwareManagement: true, SupportsOnboardConfiguration: true),
        new("numpad", "GMMK Numpad", "17 Tasten · Drehgeber · Schieberegler", "5088", NumpadBindings, "", EncoderKeyId: "r2c3", HasSlider: true, HasApprovedFirmwareManagement: true, SupportsOnboardConfiguration: true),
        new("pro-ansi", "GMMK PRO ANSI", "ANSI", "5044", ProAnsiBindings, "rev1", EncoderKeyId: "r0c1", AnsiLegends: true, SupportsOnboardConfiguration: true),
        new("pro-rev2-ansi", "GMMK PRO ANSI · Rev. 2", "ANSI", "5044", ProAnsiBindings, "rev2", EncoderKeyId: "r0c1", AnsiLegends: true, SupportsOnboardConfiguration: true),
        new("pro-rev2-iso", "GMMK PRO ISO · Rev. 2", "ISO", "5044", ProBindings, "rev2", EncoderKeyId: "r0c1", SupportsOnboardConfiguration: true),
        new("gmmk2-65-ansi", "GMMK 2 65% ANSI", "ANSI", "5045", Gmmk2Ansi65Bindings, "", LayoutName: "LAYOUT_65_ansi_blocker", AnsiLegends: true, SupportsOnboardConfiguration: true),
        new("gmmk2-65-iso", "GMMK 2 65% ISO", "ISO", "504A", Gmmk2Iso65Bindings, "", LayoutName: "LAYOUT_65_iso_blocker", SupportsOnboardConfiguration: true),
        new("gmmk2-96-ansi", "GMMK 2 96% ANSI", "ANSI", "504B", Gmmk2Ansi96Bindings, "", RightSideX: 209, AnsiLegends: true, SupportsOnboardConfiguration: true),
        new("gmmk2-96-iso", "GMMK 2 96% ISO", "ISO", "505A", Gmmk2Iso96Bindings, "", RightSideX: 209, SupportsOnboardConfiguration: true),
        new("gmmk3-65-ansi", "GMMK 3 65% ANSI", "ANSI", "", Gmmk365AnsiBindings, "", EncoderKeyId: "r11c6", SupportsOnboardConfiguration: true, AnsiLegends: true),
        new("gmmk3-65-iso", "GMMK 3 65% ISO", "ISO", "", Gmmk365IsoBindings, "", EncoderKeyId: "r11c6", SupportsOnboardConfiguration: true),
        new("gmmk3-75-ansi", "GMMK 3 75% ANSI", "ANSI", "", Gmmk375AnsiBindings, "", EncoderKeyId: "r11c6", SupportsOnboardConfiguration: true, AnsiLegends: true),
        new("gmmk3-75-iso", "GMMK 3 75% ISO", "ISO", "", Gmmk375IsoBindings, "", EncoderKeyId: "r11c6", SupportsOnboardConfiguration: true),
        new("gmmk3-100-ansi", "GMMK 3 100% ANSI", "ANSI", "", Gmmk3100AnsiBindings, "", EncoderKeyId: "r11c6", SupportsOnboardConfiguration: true, AnsiLegends: true),
        new("gmmk3-100-iso", "GMMK 3 100% ISO", "ISO", "", Gmmk3100IsoBindings, "", EncoderKeyId: "r11c6", SupportsOnboardConfiguration: true),
        // Shared physical editor geometry; firmware and recovery have a separate retained-loader target.
        new("gmmk3-pro-100-iso", "GMMK 3 PRO 100% ISO", "ISO", "", Gmmk3100IsoBindings, "",
            EncoderKeyId: "r11c6", LayoutReferenceId: "gmmk3-100-iso", SupportsOnboardConfiguration: true, HasApprovedFirmwareManagement: true)
    }.Select(Build).ToArray();
    // Display only: shared USB IDs must not imply that a hardware revision is resolved.
    public static string ModelGroupName(IReadOnlyList<string> deviceIds)
    {
        if (deviceIds.Count == 0) throw new InvalidDataException(L.T("Unbekanntes Gerätemodell."));
        var names = deviceIds.Select(id => Get(id).Name).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 1) return names[0];
        return string.Join(" / ", names.Select(name => name.Split(" · Rev.", StringSplitOptions.None)[0]).Distinct(StringComparer.Ordinal));
    }

    public static DeviceDefinition Get(string id) => All.FirstOrDefault(d => d.Id == id) ?? throw new InvalidDataException(L.T("Unbekanntes Gerätemodell."));
    private static DeviceDefinition Build(Descriptor definition)
    {
        var id = definition.Id;
        var assembly = typeof(DeviceCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith((definition.LayoutReferenceId ?? id) + ".json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        var leds = new Dictionary<string, int>();
        var leftSideLeds = new List<(int Index, double Y)>();
        var rightSideLeds = new List<(int Index, double Y)>();
        var badgeLeds = new List<int>();
        var index = 0;
        foreach (var led in root.GetProperty("rgb_matrix").GetProperty("layout").EnumerateArray())
        {
            if (led.TryGetProperty("matrix", out var matrix)) leds[$"r{matrix[0]}c{matrix[1]}"] = index;
            else if ((led.GetProperty("flags").GetInt32() & 8) != 0) badgeLeds.Add(index);
            else if ((led.GetProperty("flags").GetInt32() & 2) != 0)
            {
                var x = led.GetProperty("x").GetDouble();
                var y = led.GetProperty("y").GetDouble();
                if (x == definition.LeftSideX) leftSideLeds.Add((index, y));
                else if (x == definition.RightSideX) rightSideLeds.Add((index, y));
            }
            index++;
        }
        var ledCount = index;
        var bindings = definition.Bindings.Split(' ');
        var keys = new List<KeyDefinition>();
        index = 0;
        foreach (var key in root.GetProperty("layouts").GetProperty(definition.LayoutName).GetProperty("layout").EnumerateArray())
        {
            var matrix = key.GetProperty("matrix");
            var keyId = $"r{matrix[0]}c{matrix[1]}";
            var binding = bindings[index++];
            var label = definition.AnsiLegends ? BindingCatalog.AnsiKeyLabel(binding) : BindingCatalog.KeyLabel(binding);
            if (keyId == definition.EncoderKeyId) label = "Knob";
            var keyWidth = key.TryGetProperty("w", out var w) ? w.GetDouble() : 1;
            var keyHeight = key.TryGetProperty("h", out var h) ? h.GetDouble() : 1;
            keys.Add(new(keyId, label, key.GetProperty("x").GetDouble(), key.GetProperty("y").GetDouble(), keyWidth, keyHeight, binding)
            {
                LedIndex = leds.TryGetValue(keyId, out var led) ? led : null,
                IsIsoEnter = binding == "KC_ENT" && keyWidth == 1.25 && keyHeight == 2
            });
        }
        if (index != bindings.Length) throw new InvalidDataException(L.T("Layout und Belegungen stimmen nicht überein."));
        var width = keys.Max(k => k.X + k.W);
        var height = keys.Max(k => k.Y + k.H);
        if (definition.EncoderKeyId is not null)
        {
            var knob = keys.Single(key => key.Id == definition.EncoderKeyId);
            keys.Add(new("encoder:ccw", "↶", knob.X - .1, knob.Y - .78, .6, .65, "KC_VOLD"));
            keys.Add(new("encoder:cw", "↷", knob.X + .5, knob.Y - .78, .6, .65, "KC_VOLU"));
        }
        return new(id, definition.Name, definition.Subtitle, definition.ProductId, keys, width, height)
        {
            SupportsOnboardConfiguration = definition.SupportsOnboardConfiguration,
            IsExperimentalFirmware = definition.SupportsOnboardConfiguration && !definition.HasApprovedFirmwareManagement,
            HasApprovedFirmwareManagement = definition.HasApprovedFirmwareManagement,
            HasEncoder = definition.EncoderKeyId is not null,
            HasSlider = definition.HasSlider, HardwareRevision = definition.HardwareRevision, KeyAreaHeight = height,
            LedCount = ledCount,
            NonKeyLedIndices = Enumerable.Range(0, ledCount).Except(leds.Values).ToArray(),
            LayoutReferenceId = definition.LayoutReferenceId ?? id,
            BadgeLedIndices = badgeLeds.ToArray(),
            // Photo-derived housing positions, shared by ANSI/ISO. See docs/gmmk3-port.md.
            BadgeGeometry = id switch
            {
                "gmmk3-65-ansi" or "gmmk3-65-iso" => new(15.2, 1.0, 1.4, .85, null),
                "gmmk3-75-ansi" or "gmmk3-75-iso" => new(14.0, -.6, .85, 1.45, badgeLeds.Single()),
                "gmmk3-100-ansi" or "gmmk3-100-iso" or "gmmk3-pro-100-iso" => new(20.35, -.6, .85, 1.45, badgeLeds.Single()),
                _ => null
            },
            LeftSideLedIndices = leftSideLeds.OrderBy(led => led.Y).Select(led => led.Index).ToArray(),
            RightSideLedIndices = rightSideLeds.OrderBy(led => led.Y).Select(led => led.Index).ToArray()
        };
    }
}

public static class LayerGuidance
{
    public static string EditingHint => L.T("Die Auswahl zeigt, welche Ebene du bearbeitest. Sie schaltet die Tastatur nicht um.");
    public static string SwitchSteps => L.T("1. Taste in der Vorschau wählen.\n2. Zielebene und Verhalten wählen.\n3. Zuweisen und auf die Tastatur übertragen.");
    public static string FnExample => L.T("Beispiel: Fn auf Ebene 0 öffnet beim Halten Ebene 1. Dort kannst du einer Taste etwa „Lauter“ zuweisen.");
    public static string ModifierHint => L.T("Strg, Shift oder Alt hältst du zusammen mit einer anderen Taste gedrückt, etwa Strg+C. Fn öffnet stattdessen eine zusätzliche Belegung auf diesem Gerät.");
    public static string LayerSummary(int layer) => L.T(layer == 0
        ? "Basis: Deine normale Tastenbelegung."
        : "Zusätzliche Belegung: Nutze sie mit Fn oder einer Taste zum Ebenenwechsel.");
    public static string ActionDescription(int mode) => L.T(mode switch
    {
        0 => "Halten aktiviert die Zielebene. Loslassen schaltet sie wieder aus.",
        1 => "Ein Druck schaltet die Zielebene ein oder aus. Die Taste zum Ebenenwechsel muss auch auf dieser Ebene erreichbar bleiben.",
        2 => "Wechselt zur Zielebene und schaltet andere Zusatzebenen aus. Lege dort auch eine Taste zum Zurückwechseln an.",
        _ => ""
    });
    public static string ActionDescription(int mode, int sourceLayer, int targetLayer) => ActionDescription(mode)
        + (mode == 0 && targetLayer <= sourceLayer ? " " + L.T("Zum Öffnen einer zusätzlichen Belegung wähle eine höhere Ebene. Für den Rückweg zur Basis nutze „Direkt wechseln“.") : "");
    public static string? BindingLabel(string code)
    {
        if (code == "KC_TRNS") return L.T("Belegung darunter verwenden");
        return TryLayerAction(code, out var mode, out var target) ? mode switch
        {
            0 => L.F($"Fn · Ebene {target} beim Halten"),
            1 => L.F($"Ebene {target} ein-/ausschalten"),
            _ => L.F($"Zu Ebene {target} wechseln")
        } : null;
    }
    public static string BindingDescription(string code) => code == "KC_TRNS"
        ? L.T("Verwendet die Belegung der nächstniedrigeren eingeschalteten Ebene, sonst die Basis.")
        : TryLayerAction(code, out var mode, out _) ? ActionDescription(mode)
        : code is "KC_LCTL" or "KC_RCTL" or "KC_LSFT" or "KC_RSFT" or "KC_LALT" or "KC_RALT" or "KC_LGUI" or "KC_RGUI" ? ModifierHint : "";
    public static bool TryLayerAction(string code, out int mode, out int target)
    {
        mode = -1; target = -1;
        if (code.Length != 5 || code[2] != '(' || code[4] != ')' || code[3] is < '0' or > '3') return false;
        mode = code[..2] switch { "MO" => 0, "TG" => 1, "TO" => 2, _ => -1 };
        target = code[3] - '0';
        return mode >= 0;
    }
}

public static class BindingCatalog
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["KC_BSLS"]="\\", ["KC_SCRL"]="Scroll Lock", ["KC_PAUS"]="Pause", ["KC_APP"]="Menu",
        ["KC_ESC"]="Esc", ["KC_PSCR"]="Druck", ["KC_MUTE"]="Stumm", ["KC_GRV"]="^", ["KC_MINS"]="ß", ["KC_EQL"]="´", ["KC_BSPC"]="⌫", ["KC_DEL"]="Entf", ["KC_TAB"]="Tab", ["KC_LBRC"]="Ü", ["KC_RBRC"]="+", ["KC_CAPS"]="Caps", ["KC_SCLN"]="Ö", ["KC_QUOT"]="Ä", ["KC_NUHS"]="#", ["KC_ENT"]="Enter", ["KC_LSFT"]="Shift", ["KC_RSFT"]="Shift", ["KC_NUBS"]="<", ["KC_COMM"]=",", ["KC_DOT"]=".", ["KC_SLSH"]="-", ["KC_Y"]="Z", ["KC_Z"]="Y", ["KC_LCTL"]="Strg", ["KC_RCTL"]="Strg", ["KC_LGUI"]="Win", ["KC_RGUI"]="Win R", ["KC_LALT"]="Alt", ["KC_RALT"]="Alt Gr", ["KC_SPC"]="Space", ["MO(1)"]="Fn", ["KC_LEFT"]="←", ["KC_RGHT"]="→", ["KC_UP"]="↑", ["KC_DOWN"]="↓", ["KC_PGUP"]="Bild ↑", ["KC_PGDN"]="Bild ↓", ["KC_HOME"]="Pos1", ["KC_END"]="Ende", ["KC_INS"]="Einfg", ["KC_TRNS"]="Durchreichen", ["KC_NO"]="Deaktiviert", ["KC_VOLU"]="Lauter", ["KC_VOLD"]="Leiser", ["KC_MPLY"]="Play/Pause", ["KC_MNXT"]="Nächster Titel", ["KC_MPRV"]="Vorheriger Titel", ["KC_MSTP"]="Stopp", ["KC_CALC"]="Rechner", ["KC_MAIL"]="E-Mail öffnen", ["KC_MYCM"]="Dateimanager öffnen", ["KC_NUM"]="Num Lock", ["KC_PSLS"]="/", ["KC_PAST"]="*", ["KC_PMNS"]="−", ["KC_PPLS"]="+", ["KC_PENT"]="Enter", ["KC_PDOT"]=",", ["RM_TOGG"]="RGB an/aus", ["RM_VALU"]="RGB heller", ["RM_VALD"]="RGB dunkler", ["RM_NEXT"]="RGB Effekt weiter"
    };
    public static IReadOnlyList<BindingOption> All { get; } = Create();
    // Only character legends depend on the physical layout. Functional labels are
    // translated at display time, without changing the stored labels or bindings.
    public static string AnsiKeyLabel(string code) => code switch
    {
        "KC_Y" => "Y", "KC_Z" => "Z", "KC_GRV" => "`", "KC_MINS" => "-", "KC_EQL" => "=",
        "KC_LBRC" => "[", "KC_RBRC" => "]", "KC_BSLS" => "\\", "KC_SCLN" => ";", "KC_QUOT" => "'",
        "KC_COMM" => ",", "KC_DOT" or "KC_PDOT" => ".", "KC_SLSH" => "/",
        "KC_RALT" => "Alt", _ => KeyLabel(code)
    };
    // Keep narrow keycaps readable; tooltips and accessibility retain the full names.
    internal static string CompactLegend(string label) => L.CurrentLanguage == "en" ? label switch
    {
        "Druck" => "PrtSc", "Entf" => "Del", "Einfg" => "Ins",
        "Bild ↑" => "PgUp", "Bild ↓" => "PgDn", _ => L.T(label)
    } : L.T(label);
    public static string KeyLabel(string code) => Labels.TryGetValue(code, out var name) ? name : code.StartsWith("KC_P") && code.Length == 5 && char.IsDigit(code[4]) ? code[4].ToString() : code.StartsWith("KC_") ? code[3..] : code;
    public static string FunctionLabel(string code)
    {
        if (LayerGuidance.BindingLabel(code) is { } action) return action;
        if (code is "KC_LCTL" or "KC_RCTL" or "KC_LSFT" or "KC_RSFT" or "KC_LALT" or "KC_RALT" or "KC_LGUI" or "KC_RGUI")
        {
            var name = code is "KC_LGUI" or "KC_RGUI" ? L.T("Windows / Command") : L.T(KeyLabel(code));
            return name + " · " + L.T(code[3] == 'L' ? "Links" : "Rechts");
        }
        return L.T(KeyLabel(code));
    }
    private static IReadOnlyList<BindingOption> Create()
    {
        var codes = Enumerable.Range('A', 26).Select(c => "KC_" + (char)c).Concat(Enumerable.Range(0, 10).Select(i => "KC_" + i)).Concat(Enumerable.Range(1, 24).Select(i => "KC_F" + i)).Concat(Enumerable.Range(0, 10).Select(i => "KC_P" + i)).Concat(Labels.Keys).Distinct();
        return codes.Select(c => new BindingOption(c, KeyLabel(c)) { ShowCode = true }).ToList();
    }
    public static bool IsBasic(string value) => All.Any(b => b.Code == value) && value.StartsWith("KC_", StringComparison.Ordinal) && value is not "KC_NO" and not "KC_TRNS";
}

public static class ProjectService
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static AppProject Create(string deviceId)
    {
        var device = DeviceCatalog.Get(deviceId);
        const string color = "#FF0000";
        var result = new AppProject { DeviceId = deviceId, Name = device.Name };
        for (var l = 0; l < 4; l++)
        {
            var layer = new LayerProfile { Name = l == 0 ? L.T("Basis") : l == 1 ? L.T("Funktion") : L.F($"Ebene {l}"), SideLedColor = color };
            foreach (var key in device.Keys) layer.Keys[key.Id] = new() { Binding = l == 0 ? key.DefaultBinding : "KC_TRNS", Color = color };
            result.Layers.Add(layer);
        }
        if (deviceId == "numpad") result.Layers[1].Keys["r4c0"].Binding = "KC_NUM"; // Fn + 0; the held Fn switch cannot press itself a second time.
        return result;
    }
    public static AppProject Clone(AppProject project) => JsonSerializer.Deserialize<AppProject>(JsonSerializer.Serialize(project, JsonOptions), JsonOptions)!;
    public static string DefaultPath(string deviceId) { DeviceCatalog.Get(deviceId); return Path.Combine(Paths.DataRoot, deviceId + ".inglorious.json"); }
    public static IReadOnlyList<string> Validate(AppProject project)
    {
        var errors = new List<string>();
        if (project.SchemaVersion != 1) errors.Add(L.T("Diese Profilversion wird nicht unterstützt."));
        if (string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > 120) errors.Add(L.T("Profilname muss 1–120 Zeichen lang sein."));
        var device = DeviceCatalog.All.FirstOrDefault(d => d.Id == project.DeviceId);
        if (device == null) { errors.Add(L.T("Unbekanntes Gerät.")); return errors; }
        if (project.DeviceSettings == null || project.DeviceSettings.UsbPollingIntervalMs is not (1 or 2 or 4 or 8) || project.DeviceSettings.DebounceMs is < 0 or > 40)
            errors.Add(L.T("Ungültige Geräteeinstellungen: USB-Abfrageintervall 1, 2, 4 oder 8 ms und Entprellzeit 0–40 ms erforderlich."));
        if (project.Standby == null || project.Standby.TimeoutSeconds is < 10 or > 3600 || project.Standby.Mode is < 0 or > 2 || project.Standby.DimBrightnessPercent is < 1 or > 100)
            errors.Add(L.T("Ungültige Standby-Einstellungen."));
        if (project.Slider == null || project.Slider.Mode is < 0 or > 3 || (!device.HasSlider && project.Slider.Mode != 0) || project.Slider.Minimum is < 0 or > 1022 || project.Slider.Maximum <= project.Slider.Minimum || project.Slider.Maximum > 1023 || project.Slider.MidiChannel is < 0 or > 15 || project.Slider.MidiController is < 0 or > 127)
            errors.Add(L.T("Ungültige Schieberegler-Einstellungen."));
        if (project.Layers == null || project.Layers.Count != 4) { errors.Add(L.T("Ein Profil benötigt vier Ebenen.")); return errors; }
        if (project.Macros == null || project.Macros.Count > 32) { errors.Add(L.T("Maximal 32 Makros werden unterstützt.")); return errors; }
        var macroIds = new HashSet<string>();
        foreach (var macro in project.Macros)
        {
            if (macro == null || macro.Id == null || !Regex.IsMatch(macro.Id, "\\A[a-zA-Z0-9_-]{1,64}\\z") || !macroIds.Add(macro.Id)) { errors.Add(L.T("Ungültige oder doppelte Makro-ID.")); continue; }
            if (string.IsNullOrWhiteSpace(macro.Name) || macro.Name.Length > 80) errors.Add(L.T("Makroname muss 1–80 Zeichen lang sein."));
            if (macro.Steps == null || macro.Steps.Count is < 1 or > 128) { errors.Add(L.F($"Makro {macro.Name}: 1–128 Schritte erforderlich.")); continue; }
            var held = new HashSet<string>();
            long totalDelay = 0;
            foreach (var step in macro.Steps)
            {
                if (step == null) { errors.Add(L.T("Leerer Makroschritt.")); continue; }
                if (step.Kind == "delay")
                {
                    if (step.DelayMs is < 1 or > 10000) errors.Add(L.T("Eine Pause muss 1–10000 ms dauern."));
                    totalDelay += step.DelayMs;
                }
                else if (step.Kind is "down" or "up")
                {
                    if (!BindingCatalog.IsBasic(step.Key ?? "")) errors.Add(L.F($"Makro {macro.Name}: ungültige Taste."));
                    else if (step.Kind == "down" && !held.Add(step.Key!)) errors.Add(L.F($"Makro {macro.Name}: Taste ist bereits gedrückt."));
                    else if (step.Kind == "up" && !held.Remove(step.Key!)) errors.Add(L.F($"Makro {macro.Name}: Taste wurde noch nicht gedrückt."));
                }
                else errors.Add(L.T("Unbekannter Makroschritt."));
            }
            if (held.Count > 0) errors.Add(L.F($"Makro {macro.Name}: jede gedrückte Taste muss wieder losgelassen werden."));
            if (totalDelay > 30000) errors.Add(L.F($"Makro {macro.Name}: maximal 30 Sekunden Pausen insgesamt."));
        }
        foreach (var layer in project.Layers)
        {
            if (layer == null || string.IsNullOrWhiteSpace(layer.Name) || layer.Name.Length > 80 || layer.Keys == null) { errors.Add(L.T("Ungültiger Layer.")); continue; }
            if (layer.LightingEffect == null || layer.LightingEffect.Mode is < 0 or > 20 || layer.LightingEffect.Speed is < 0 or > 255 || layer.LightingEffect.Brightness is < 0 or > 255 || layer.LightingEffect.Target is < 1 or > 3)
                errors.Add(L.T("Ungültige Beleuchtungseinstellungen: Effekt 0–20, Geschwindigkeit und Helligkeit 0–255, Bereich 1–3 erforderlich."));
            if (layer.LedEffects == null || layer.LedEffects.Any(kv => kv.Key < 0 || kv.Key >= device.LedCount || kv.Value == null || kv.Value.Mode is < 0 or > 20 || kv.Value.Speed is < 0 or > 255 || kv.Value.Brightness is < 0 or > 255))
                errors.Add(L.T("Ungültiger LED-Effekt: vorhandene LED, Effekt 0–20 und Geschwindigkeit/Helligkeit 0–255 erforderlich."));
            if (!ColorSequenceRules.IsValid(layer.LightingEffect?.Colors) || (layer.LedEffects?.Values.Any(e => !ColorSequenceRules.IsValid(e?.Colors)) ?? true))
                errors.Add(L.T("Eine Farbfolge darf höchstens acht Farben im Format #RRGGBB enthalten."));
            if (layer.SideLedColor == null || !Regex.IsMatch(layer.SideLedColor, "\\A#[0-9A-Fa-f]{6}\\z") || layer.ExtraLedColors == null || layer.ExtraLedColors.Any(kv => !device.NonKeyLedIndices.Contains(kv.Key) || kv.Value == null || !Regex.IsMatch(kv.Value, "\\A#[0-9A-Fa-f]{6}\\z")))
                errors.Add(L.T("Ungültige Seitenbeleuchtung."));
            if (layer.Keys.Count != device.Keys.Count || device.Keys.Any(k => !layer.Keys.ContainsKey(k.Id))) errors.Add(L.F($"Layer {layer.Name}: Tasten passen nicht zum Gerät."));
            foreach (var key in layer.Keys.Values)
            {
                if (key == null) { errors.Add(L.T("Leere Tastenbelegung.")); continue; }
                if (key.Color == null || !Regex.IsMatch(key.Color, "\\A#[0-9A-Fa-f]{6}\\z")) errors.Add(L.T("RGB-Farben benötigen das Format #RRGGBB."));
                var binding = key.Binding ?? "";
                if (!BindingCatalog.All.Any(b => b.Code == binding) && !Regex.IsMatch(binding, "\\A(MO|TG|TO)\\([0-3]\\)\\z") && !(binding.StartsWith("MACRO:", StringComparison.Ordinal) && macroIds.Contains(binding[6..]))) errors.Add(L.F($"Unbekannte Belegung: {binding}"));
            }
        }
        if (project.Layers[0]?.Keys?.Values.Any(k => k?.Binding == "KC_TRNS") == true) errors.Add(L.T("Der Basis-Layer darf keine durchgereichten Tasten enthalten."));
        var sequences = project.Layers.Where(l => l is not null).SelectMany(l => new[] { l.LightingEffect?.Colors }.Concat(l.LedEffects?.Values.Select(e => e?.Colors) ?? []))
            .Where(c => c is { Count: > 0 } && ColorSequenceRules.IsValid(c)).Select(c => ColorSequenceRules.Identity(c!)).Distinct(StringComparer.Ordinal);
        if (sequences.Count() > ColorSequenceRules.MaximumSequences) errors.Add(L.T("Der Gerätespeicher fasst höchstens acht unterschiedliche Farbfolgen. Gleiche Folgen teilen sich einen Speicherplatz."));
        return errors.Distinct().ToList();
    }
    public static void Save(string path, AppProject project)
    {
        var errors = Validate(project);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(project, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException(L.T("Profildatei ist zu groß (max. 2 MB)."));
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static AppProject Load(string path)
    {
        using var document = ReadProfileDocument(path);
        return Load(document);
    }
    internal static JsonDocument ReadProfileDocument(string path) => ReadJsonFile(path, MaximumFileBytes, L.T("Profildatei ist zu groß (max. 2 MB)."));
    internal static AppProject Load(JsonDocument document)
    {
        var project = document.Deserialize<AppProject>(JsonOptions) ?? throw new InvalidDataException(L.T("Leere Profildatei."));
        var errors = Validate(project);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return project;
    }
    internal static JsonDocument ReadJsonFile(string path, int maximumBytes, string sizeMessage)
    {
        // Check and read the same open handle. Replacing/growing a file cannot bypass the bound.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximumBytes) throw new InvalidDataException(sizeMessage);
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException(sizeMessage);
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { MaxDepth = 16 });
        try { CheckUniqueProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException(L.T("Die Datei ist ungültig. Bitte wähle eine andere Datei."));
                CheckUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckUniqueProperties(item);
    }
}
