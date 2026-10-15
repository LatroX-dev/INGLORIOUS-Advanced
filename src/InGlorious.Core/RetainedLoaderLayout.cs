using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace InGlorious.Core;

/// <summary>Reviewed executable layout for the PRO 100 ISO resident-loader port.
/// The digest identifies vendor loader code, never a user's complete flash image.</summary>
public static class RetainedLoaderLayout
{
    public const string DeviceId = "gmmk3-pro-100-iso";
    public const byte BoardId = 16;
    public const int FlashBytes = 262144, SramBytes = 36864, PrefixBytes = 0x7000;
    public const string LoaderSha256 = "079DC24559F9ED85368499AAC71CBC8A023C94524D5E4189C20C1D7E4CFCFCA4";

    public static int ApplicationOffset(byte boardId) => boardId == BoardId ? PrefixBytes : 0;

    public static void ValidateLayout(string deviceId, byte boardId, int flashBytes, int applicationOffset)
    {
        bool retained = deviceId == DeviceId || boardId == BoardId;
        if (retained ? deviceId != DeviceId || boardId != BoardId || flashBytes != FlashBytes || applicationOffset != PrefixBytes
                     : applicationOffset != 0)
            throw new InvalidDataException("FIRMWARE_APPLICATION_LAYOUT_MISMATCH");
    }

    public static void ValidatePrefix(ReadOnlySpan<byte> image)
    {
        if (image.Length != FlashBytes && image.Length != PrefixBytes)
            throw new InvalidDataException("RETAINED_LOADER_IMAGE_LENGTH");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(image[..PrefixBytes]), Convert.FromHexString(LoaderSha256)))
            throw new InvalidDataException("UNKNOWN_RESIDENT_LOADER");
    }

    public static void ValidateApplicationVectors(ReadOnlySpan<byte> application)
    {
        if (application.Length < 8 || application.Length > InstallationIdentity.Offset(FlashBytes) - PrefixBytes)
            throw new InvalidDataException("RETAINED_APPLICATION_LENGTH");
        uint stack = BinaryPrimitives.ReadUInt32LittleEndian(application), reset = BinaryPrimitives.ReadUInt32LittleEndian(application[4..]);
        if (stack <= 0x20000000 || stack > 0x20000000 + SramBytes || stack % 4 != 0 || (reset & 1) == 0 ||
            (reset & ~1U) < 0x08000000 + PrefixBytes || (reset & ~1U) >= 0x08000000UL + PrefixBytes + (uint)application.Length)
            throw new InvalidDataException("RETAINED_APPLICATION_VECTORS");
    }
}
