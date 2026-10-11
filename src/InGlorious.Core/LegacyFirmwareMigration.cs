using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InGlorious.Core;

/// <summary>One reviewed pre-identity release. Never accepts a USB/version claim as code provenance.
/// Original ownership additionally requires a matching STM ROM serial and a complete original pair.
/// This does not authenticate a malicious USB device or a deliberately forged historical archive.</summary>
public static class LegacyFirmwareMigration
{
    public const string Version = "0.9.2";
    public const int ApplicationBytes = 45204;
    public const string ApplicationSha256 = "6511C5CD6A7BD3AC82E2B84D69F078997C50CDF18C6A81A77884A2BB5F1A3630";

    public static void ValidateImage(ReadOnlySpan<byte> image, string deviceId)
    {
        if (deviceId != "pro-iso") throw new InvalidDataException("LEGACY_MODEL_NOT_APPROVED");
        ValidateImageBytes(image, ApplicationBytes, ApplicationSha256);
    }

    internal static void ValidateImageBytes(ReadOnlySpan<byte> image, int codeBytes, string codeHash)
    {
        if (image.Length != 262144 || codeBytes < 8 || codeBytes > InstallationIdentity.Offset(image.Length) ||
            image.Slice(codeBytes, image.Length - InstallationIdentity.StorageBytes - codeBytes).ContainsAnyExcept((byte)0xFF) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(image[..codeBytes]), Convert.FromHexString(codeHash)))
            throw new InvalidDataException("LEGACY_IMAGE_NOT_APPROVED");
    }

    public static void ValidateFingerprint(JsonElement fingerprint, string currentSerial)
    {
        var serial = fingerprint.GetProperty("serial").GetString();
        if (!Regex.IsMatch(currentSerial, "\\A[0-9A-Fa-f]{12}\\z", RegexOptions.CultureInvariant) || serial != currentSerial ||
            fingerprint.GetProperty("transport").GetString() != "stm32-dfuse" ||
            fingerprint.GetProperty("vid").GetUInt16() != 0x0483 || fingerprint.GetProperty("pid").GetUInt16() != 0xDF11 ||
            fingerprint.GetProperty("configuration").GetByte() != 1 || fingerprint.GetProperty("interface").GetByte() != 0 ||
            fingerprint.GetProperty("alternate").GetByte() != 0 || fingerprint.GetProperty("flashBase").GetUInt32() != 0x08000000 ||
            fingerprint.GetProperty("flashBytes").GetInt32() != 262144 || fingerprint.GetProperty("sramBytes").GetInt32() != 40960 ||
            fingerprint.GetProperty("identityStrength").GetString() != "usb-serial-and-port" ||
            !Regex.IsMatch(fingerprint.GetProperty("memoryMap").GetString() ?? "", "\\A@Internal Flash[ ]+/0x08000000/128\\*0*2Kg\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("LEGACY_ORIGINAL_IDENTITY_MISMATCH");
    }

    public static void ValidateOriginal(ReadOnlySpan<byte> original, string deviceId)
    {
        if (deviceId != "pro-iso" || original.Length != 262144 || InstallationIdentity.Read(original) is not null)
            throw new InvalidDataException("LEGACY_ORIGINAL_NOT_APPROVED");
        StockFirmwareInspector.Validate(original, 0x320F, 0x5046, deviceId);
    }
}
