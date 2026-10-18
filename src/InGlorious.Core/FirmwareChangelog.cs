namespace InGlorious.Core;

public sealed record FirmwareReleaseNotes(FirmwareSemanticVersion Version, IReadOnlyList<string> Changes);

/// <summary>Verified firmware changes, intentionally written and displayed in English.</summary>
public static class FirmwareChangelog
{
    private static readonly FirmwareSemanticVersion Version08 = new(0, 8, 0);
    private static readonly FirmwareSemanticVersion Version09 = new(0, 9, 0);
    private static readonly FirmwareReleaseNotes KnobFeedbackNotes = new(new(0, 11, 8), Array.AsReadOnly(new[]
    {
        "Set a different short-press action for each knob function.",
        "See the active knob function and get a brief light signal when switching."
    }));
    private static readonly FirmwareReleaseNotes KnobOrderNotes = new(new(0, 11, 7), Array.AsReadOnly(new[]
    {
        "Choose the order of knob functions."
    }));
    private static readonly FirmwareReleaseNotes KnobCycleNotes = new(new(0, 11, 6), Array.AsReadOnly(new[]
    {
        "New knob actions for scrolling, zoom, tabs and undo/redo.",
        "Adjust knob sensitivity and hold to switch between selected functions."
    }));
    private static readonly FirmwareReleaseNotes BrightnessBindingNotes = new(new(0, 11, 5), Array.AsReadOnly(new[]
    {
        "Adjust keyboard brightness with the knob or assigned keys."
    }));
    private static readonly FirmwareReleaseNotes LightingTestNotes = new(new(0, 11, 4), Array.AsReadOnly(new[]
    {
        "Fixed lighting tests being blocked when no key is pressed."
    }));
    private static readonly FirmwareReleaseNotes RedDefaultsNotes = new(new(0, 11, 3), Array.AsReadOnly(new[]
    {
        "Red default lighting when no saved colors are available."
    }));
    private static readonly FirmwareReleaseNotes ColorOutputNotes = new(new(0, 9, 2), Array.AsReadOnly(new[]
    {
        "Improved color output for less washed-out colors."
    }));
    private static readonly FirmwareReleaseNotes Version08Notes = new(Version08, Array.AsReadOnly(new[]
    {
        "Installed firmware version shown in the app."
    }));
    // Customer-facing features, improvements and bugfixes: short English bullets, without technical details.
    private static readonly IReadOnlyList<FirmwareReleaseNotes> Notes = Array.AsReadOnly(new[]
    {
        BrightnessBindingNotes,
        LightingTestNotes,
        RedDefaultsNotes,
        new FirmwareReleaseNotes(new(0, 11, 1), Array.AsReadOnly(new[]
        {
            "Fixed reactive lighting reappearing after long periods without input."
        })),
        ColorOutputNotes,
        new FirmwareReleaseNotes(Version09, Array.AsReadOnly(new[]
        {
            "New lighting test after firmware updates.",
            "Optional screensaver."
        })),
        Version08Notes
    });
    private static readonly IReadOnlyList<FirmwareReleaseNotes> NumpadNotes = Array.AsReadOnly(new[]
    {
        new FirmwareReleaseNotes(new(0, 9, 1), Array.AsReadOnly(new[]
        {
            "Fixed the Numpad lighting test stopping too early.",
            "Reduced unintended wake-ups from standby."
        }))
    }.Concat(Notes).OrderByDescending(note => note.Version).ToArray());

    public static IReadOnlyList<FirmwareReleaseNotes> Between(string deviceId, FirmwareSemanticVersion? installed, FirmwareSemanticVersion target)
    {
        // No guessed starting version for an unknown device or a missing/default version report.
        // This catalog contains released changes only. Never derive a pending
        // change from a device response or an undocumented future target.
        var newestDocumentedVersion = KnobFeedbackNotes.Version;
        if (!installed.HasValue || installed.Value == default || installed.Value >= target || installed.Value >= newestDocumentedVersion)
            return Array.Empty<FirmwareReleaseNotes>();
        IReadOnlyList<FirmwareReleaseNotes>? notes = deviceId switch
        {
            "pro-iso" => Notes,
            "numpad" => NumpadNotes,
            "gmmk3-pro-100-iso" => [BrightnessBindingNotes, LightingTestNotes, new(new(0, 11, 3), Array.AsReadOnly(new[]
            { "Red default lighting when no saved colors are available.", "Individual key, knob and LED tests." }))],
            _ when BoardDescriptorRegistry.ProtocolBoards.Any(board => board.DeviceId == deviceId) => [BrightnessBindingNotes, LightingTestNotes, RedDefaultsNotes],
            _ => null
        };
        if (notes is null) return Array.Empty<FirmwareReleaseNotes>();
        if (BoardDescriptorRegistry.ProtocolBoards.Any(board => board.DeviceId == deviceId && board.HasEncoder))
            notes = Array.AsReadOnly(new[] { KnobFeedbackNotes, KnobOrderNotes, KnobCycleNotes }.Concat(notes).ToArray());
        return Array.AsReadOnly(notes.Where(note => installed.Value < note.Version && note.Version <= target && note.Version <= newestDocumentedVersion).ToArray());
    }
}
