namespace InGlorious.Core;

public sealed record FirmwareReleaseNotes(FirmwareSemanticVersion Version, IReadOnlyList<string> Changes);

/// <summary>Verified firmware changes, intentionally written and displayed in English.</summary>
public static class FirmwareChangelog
{
    private static readonly FirmwareSemanticVersion Version08 = new(0, 8, 0);
    private static readonly FirmwareSemanticVersion Version09 = new(0, 9, 0);
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
        var newestDocumentedVersion = Notes[0].Version;
        if (!installed.HasValue || installed.Value == default || installed.Value >= target || installed.Value >= newestDocumentedVersion)
            return Array.Empty<FirmwareReleaseNotes>();
        var notes = deviceId switch { "pro-iso" => Notes, "numpad" => NumpadNotes, _ => null };
        if (notes is null) return Array.Empty<FirmwareReleaseNotes>();
        return Array.AsReadOnly(notes.Where(note => installed.Value < note.Version && note.Version <= target && note.Version <= newestDocumentedVersion).ToArray());
    }
}
