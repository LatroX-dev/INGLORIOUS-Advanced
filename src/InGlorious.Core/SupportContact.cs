using System.Net.Mail;

namespace InGlorious.Core;

/// <summary>Public contact destinations only. Never populated from device or profile data.</summary>
public static class SupportContact
{
    public const string IssueUrl = SoftwareUpdateService.RepositoryUrl + "/issues/new";
    public const string PublicEmail = "inglorious@latrox.dev";
    // The maintainer has configured forwarding for this public address.
    public const bool EmailEnabled = true;
    public static string? Email => EmailEnabled ? ValidateEmail(PublicEmail) : null;
    public static Uri? EmailUri => Email is { } email ? new Uri("mailto:" + email) : null;

    public static string? ValidateEmail(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 254 || value.Any(c => c <= 32 || c >= 127 || "?&#%:<>\"".Contains(c))) return null;
        return MailAddress.TryCreate(value, out var address) && address.Address == value && address.DisplayName.Length == 0
            && value.LastIndexOf('.') > value.IndexOf('@') ? value : null;
    }
}
