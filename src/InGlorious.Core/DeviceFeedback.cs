using System.Runtime.InteropServices;

namespace InGlorious.Core;

public enum DeviceFeedbackResult { Success, Problem }
public enum DeviceFeedbackTrigger { Installation, ConfigurationTransfer }

/// <summary>A single optional invitation per supported model, after a verified successful user action.</summary>
public static class ExperimentalDeviceFeedback
{
    public static bool ShouldOffer(AppPreferences preferences, string deviceId, DeviceFeedbackTrigger trigger, bool succeeded, bool offline) =>
        succeeded && !offline && Enum.IsDefined(trigger) &&
        DeviceCatalog.All.Any(device => device.Id == deviceId && device.IsExperimentalFirmware) &&
        !preferences.FeedbackRequestedModels.Contains(deviceId, StringComparer.Ordinal);

    public static bool TryMarkOffered(string deviceId, DeviceFeedbackTrigger trigger, bool succeeded, bool offline,
        out AppPreferences? preferences, string? path = null)
    {
        preferences = null;
        // Reject offline and ineligible events before touching settings files.
        if (!ShouldOffer(new AppPreferences(), deviceId, trigger, succeeded, offline)) return false;
        var session = AppPreferencesStore.BeginEdit(path);
        if (!ShouldOffer(session.Preferences, deviceId, trigger, succeeded, offline)) return false;
        var updated = session.Preferences with { FeedbackRequestedModels = [.. session.Preferences.FeedbackRequestedModels, deviceId] };
        AppPreferencesStore.Save(session, updated);
        preferences = updated;
        return true;
    }
}

/// <summary>Only deliberately selected public metadata. Does not read hardware, files or profiles.</summary>
public sealed record DeviceFeedbackContext(string DeviceName, string DeviceId, string AppVersion, string FirmwareVersion, string OperatingSystem)
{
    public string Layout { get; init; } = "Not checked";
    public string SupportStatus { get; init; } = "Not checked";
    public string AppArchitecture { get; init; } = "Not checked";
    public string UiLanguage { get; init; } = "Not checked";
    public string FirmwareBuildId { get; init; } = "Not checked";
    public string BundledFirmwareVersion { get; init; } = "Not checked";
    public static string Invitation(DeviceDefinition device) => L.T(device.IsExperimentalFirmware
        ? "Dein Modell wird experimentell unterstützt. Deine Rückmeldung zu Tasten und Beleuchtung hilft uns besonders."
        : "Deine Rückmeldung hilft uns, INGLORIOUS Advanced zu verbessern.");

    public static DeviceFeedbackContext Create(string deviceId, OnboardFirmwareVersion? reportedVersion = null)
    {
        var device = DeviceCatalog.Get(deviceId);
        var reported = reportedVersion is { BuildId: not 0 } && reportedVersion.DeviceId == device.Id ? reportedVersion : null;
        return new(device.Name, device.Id, SoftwareUpdateService.CurrentVersion,
            reported?.Version.ToString() ?? "Not checked",
            RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")")
        {
            Layout = device.Id == "numpad" ? "Numpad" : device.Id.EndsWith("-iso", StringComparison.Ordinal) ? "ISO" : "ANSI",
            SupportStatus = device.IsOfflineOnly ? "Offline profile only" : device.IsExperimentalFirmware ? "Experimental" : "Tested",
            AppArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), UiLanguage = L.CurrentLanguage,
            FirmwareBuildId = reported is null ? "Not checked" : reported.BuildId.ToString("X8"),
            BundledFirmwareVersion = BundledVersion(device)
        };
    }

    private static string BundledVersion(DeviceDefinition device)
    {
        if (device.IsOfflineOnly || !device.HasApprovedFirmwareManagement) return "Not available";
        try { return DeviceSetupService.TargetFirmwareVersionFor(device.Id); }
        catch (Exception error) when (error is SetupException or InvalidOperationException or System.Text.Json.JsonException)
        { return "Not available"; }
    }
}

public sealed record DeviceFeedbackDetails(string Steps = "", string Expected = "", string Actual = "")
{
    public const int MaximumFieldLength = 500;
}

/// <summary>A frozen, user-previewed draft. Opening a destination is always a separate UI action.</summary>
public sealed record DeviceFeedbackDraft(string Title, string Body)
{
    public const int MaximumCommentLength = 1000;
    public static DeviceFeedbackDraft Create(DeviceFeedbackContext context, DeviceFeedbackResult result, string? comment, DeviceFeedbackDetails? details = null)
    {
        if (!Enum.IsDefined(result)) throw new ArgumentOutOfRangeException(nameof(result));
        comment = (comment ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (comment.Length > MaximumCommentLength) throw new ArgumentException("Comment is too long.", nameof(comment));
        string Detail(string? value)
        {
            var normalized = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (normalized.Length > DeviceFeedbackDetails.MaximumFieldLength) throw new ArgumentException("Problem detail is too long.", nameof(details));
            return normalized;
        }
        var steps = Detail(details?.Steps); var expected = Detail(details?.Expected); var actual = Detail(details?.Actual);
        var problem = result == DeviceFeedbackResult.Problem
            ? (steps.Length == 0 ? "" : "\n\n## Steps to reproduce\n\n" + steps) +
              (expected.Length == 0 ? "" : "\n\n## Expected behavior\n\n" + expected) +
              (actual.Length == 0 ? "" : "\n\n## Actual behavior\n\n" + actual) : "";
        var outcome = result == DeviceFeedbackResult.Success ? "Working well" : "Problem";
        return new($"Device feedback: {OneLine(context.DeviceName)} — {outcome}",
            $"## Device feedback\n\n- Device: {OneLine(context.DeviceName)} ({OneLine(context.DeviceId)})\n" +
            $"- Layout: {OneLine(context.Layout)}\n- Support: {OneLine(context.SupportStatus)}\n" +
            $"- App: {OneLine(context.AppVersion)} ({OneLine(context.AppArchitecture)})\n- UI language: {OneLine(context.UiLanguage)}\n" +
            $"- Firmware: {OneLine(context.FirmwareVersion)}\n- Firmware build: {OneLine(context.FirmwareBuildId)}\n" +
            $"- Bundled firmware: {OneLine(context.BundledFirmwareVersion)}\n" +
            $"- OS: {OneLine(context.OperatingSystem)}\n- Result: {outcome}" + problem + "\n\n## Comment\n\n" +
            (comment.Length == 0 ? "No comment." : comment));
    }

    public Uri GitHubUri => new(SupportContact.IssueUrl + "?template=device-feedback.md&title=" + Uri.EscapeDataString(Title) + "&body=" + Uri.EscapeDataString(Body));
    public Uri? EmailUri => CreateEmailUri(SupportContact.Email);
    public Uri? CreateEmailUri(string? email) => SupportContact.ValidateEmail(email) is { } valid
        ? new("mailto:" + valid + "?subject=" + Uri.EscapeDataString(Title) + "&body=" + Uri.EscapeDataString(Body)) : null;

    private static string OneLine(string value) => string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
