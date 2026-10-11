using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace InGlorious.Core;

/// <summary>Identifies immutable application bytes from reviewed release manifests. USB names,
/// version responses and the recoverable installation marker alone are not firmware provenance.</summary>
public static class KnownFirmwareImages
{
    private sealed record Image(string DeviceId, byte BoardId, int Bytes, string Sha256, int FlashBytes);
    private sealed record Release(string ReleaseId, Image[] Targets);
    private sealed record Catalog(int Schema, Release[] Releases);
    private static readonly Lazy<Catalog> Approved = new(ReadCatalog);

    public static string Validate(ReadOnlySpan<byte> fullImage, string deviceId, byte boardId)
    {
        var identity = InstallationIdentity.Read(fullImage);
        if (identity is null || identity.BoardId != boardId) throw new InvalidDataException("KNOWN_FIRMWARE_IDENTITY_REQUIRED");
        int marker = InstallationIdentity.Offset(fullImage.Length);
        if (fullImage.Slice(marker + InstallationIdentity.Size, 256 - InstallationIdentity.Size).ContainsAnyExcept((byte)0xFF))
            throw new InvalidDataException("KNOWN_FIRMWARE_IDENTITY_PAGE_CHANGED");
        foreach (var release in Approved.Value.Releases)
        foreach (var image in release.Targets)
        {
            if (image.DeviceId != deviceId || image.BoardId != boardId || image.FlashBytes != fullImage.Length) continue;
            if (fullImage.Slice(image.Bytes, marker - image.Bytes).ContainsAnyExcept((byte)0xFF)) continue;
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(fullImage[..image.Bytes]), Convert.FromHexString(image.Sha256)))
                return release.ReleaseId;
        }
        throw new InvalidDataException("UNKNOWN_INSTALLED_FIRMWARE");
    }

    private static Catalog ReadCatalog()
    {
        var assembly = typeof(KnownFirmwareImages).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".KnownFirmwareCatalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("KNOWN_FIRMWARE_CATALOG_MISSING");
        if (catalog.Schema != 1 || catalog.Releases.Length == 0) throw new InvalidDataException("KNOWN_FIRMWARE_CATALOG_INVALID");
        foreach (var release in catalog.Releases)
        {
            if (!Version.TryParse(release.ReleaseId, out var version) || version.Revision >= 0 || version.Build < 0 || release.Targets.Length == 0)
                throw new InvalidDataException("KNOWN_FIRMWARE_VERSION_INVALID");
            foreach (var target in release.Targets)
                if (string.IsNullOrWhiteSpace(target.DeviceId) || target.BoardId is < 1 or > 15 || target.Bytes < 8 ||
                    target.Bytes > InstallationIdentity.Offset(target.FlashBytes) || target.Sha256.Length != 64 || target.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                    throw new InvalidDataException("KNOWN_FIRMWARE_CATALOG_INVALID");
        }
        return catalog;
    }
}
