using System.Globalization;

namespace InGlorious.Core;

public readonly record struct FirmwareSemanticVersion(ushort Major, ushort Minor, ushort Patch) : IComparable<FirmwareSemanticVersion>
{
    public int CompareTo(FirmwareSemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
    public static bool TryParse(string? value, out FirmwareSemanticVersion version)
    {
        version = default;
        var parts = value?.Split('.');
        // Older locally verified release labels used major.minor; the wire response always has three fields.
        if (parts is null || parts.Length is not (2 or 3)
            || !ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
        ushort patch = 0;
        if (parts.Length == 3 && !ushort.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return false;
        version = new(major, minor, patch); return true;
    }
    public static bool operator <(FirmwareSemanticVersion left, FirmwareSemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(FirmwareSemanticVersion left, FirmwareSemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(FirmwareSemanticVersion left, FirmwareSemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(FirmwareSemanticVersion left, FirmwareSemanticVersion right) => left.CompareTo(right) >= 0;
}

public sealed record OnboardFirmwareVersion(string DeviceId, FirmwareSemanticVersion Version, uint BuildId);
public sealed record FirmwareRuntimeVersion(OnboardFirmwareVersion Report, string UsbInstanceId, DateTimeOffset ObservedAt);
