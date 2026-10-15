using System.Runtime.InteropServices;

namespace InGlorious.Core;

public enum DeviceFeedbackResult { Success, Problem }

/// <summary>Only deliberately selected public metadata. Does not read hardware, files or profiles.</summary>
public sealed record DeviceFeedbackContext(string DeviceName, string DeviceId, string AppVersion, string FirmwareVersion, string OperatingSystem)
{
    public static DeviceFeedbackContext Create(string deviceId, OnboardFirmwareVersion? reportedVersion = null)
    {
        var device = DeviceCatalog.Get(deviceId);
        return new(device.Name, device.Id, SoftwareUpdateService.CurrentVersion,
            reportedVersion?.DeviceId == device.Id ? reportedVersion.Version.ToString() : "Not checked",
            RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")");
    }
}

/// <summary>A frozen, user-previewed draft. Opening a destination is always a separate UI action.</summary>
public sealed record DeviceFeedbackDraft(string Title, string Body)
{
    public const int MaximumCommentLength = 1000;
    public static DeviceFeedbackDraft Create(DeviceFeedbackContext context, DeviceFeedbackResult result, string? comment)
    {
        if (!Enum.IsDefined(result)) throw new ArgumentOutOfRangeException(nameof(result));
        comment = (comment ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (comment.Length > MaximumCommentLength) throw new ArgumentException("Comment is too long.", nameof(comment));
        var outcome = result == DeviceFeedbackResult.Success ? "Working well" : "Problem";
        return new($"Device feedback: {OneLine(context.DeviceName)} — {outcome}",
            $"## Device feedback\n\n- Device: {OneLine(context.DeviceName)} ({OneLine(context.DeviceId)})\n" +
            $"- App: {OneLine(context.AppVersion)}\n- Firmware: {OneLine(context.FirmwareVersion)}\n" +
            $"- OS: {OneLine(context.OperatingSystem)}\n- Result: {outcome}\n\n## Comment\n\n" +
            (comment.Length == 0 ? "No comment." : comment));
    }

    public Uri GitHubUri => new(SupportContact.IssueUrl + "?template=device-feedback.md&title=" + Uri.EscapeDataString(Title) + "&body=" + Uri.EscapeDataString(Body));
    public Uri? EmailUri => CreateEmailUri(SupportContact.Email);
    public Uri? CreateEmailUri(string? email) => SupportContact.ValidateEmail(email) is { } valid
        ? new("mailto:" + valid + "?subject=" + Uri.EscapeDataString(Title) + "&body=" + Uri.EscapeDataString(Body)) : null;

    private static string OneLine(string value) => string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
