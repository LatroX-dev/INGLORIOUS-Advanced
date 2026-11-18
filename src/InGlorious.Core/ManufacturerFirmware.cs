using System.IO;
using System.Security.Cryptography;

namespace InGlorious.Core;

/// <summary>Glorious V0003 application for the wired GMMK 3 PRO 100% ISO.
/// Both the coordinator and the adapter reconstruct the expected image from their own current read.</summary>
public static class ManufacturerFirmware
{
    internal const int ApplicationBytes = 49532;
    private const string ApplicationSha256 = "07591EF3F1A1BCEAC0210794DD4E4E8840D1246519EE16CC5E83A66BADAB58EC";

    internal static void ValidateApplication(ReadOnlySpan<byte> application)
    {
        if (application.Length != ApplicationBytes ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(application), Convert.FromHexString(ApplicationSha256)))
            throw new InvalidDataException("MANUFACTURER_APPLICATION_MISMATCH");
        RetainedLoaderLayout.ValidateApplicationVectors(application);
    }

    public static byte[] CreateImage(byte[] current, byte[] application) => Compose(current, application);

    private static byte[] Compose(byte[] current, ReadOnlySpan<byte> application)
    {
        ValidateApplication(application);
        if (current.Length != RetainedLoaderLayout.FlashBytes)
            throw new InvalidDataException("MANUFACTURER_CURRENT_SIZE_INVALID");
        KnownFirmwareImages.Validate(current, RetainedLoaderLayout.DeviceId, RetainedLoaderLayout.BoardId);

        // The vendor file begins at the application vector table and ends part way through a page.
        // Preserve the remainder of that page, the resident loader and every other untargeted byte.
        var target = current.ToArray();
        application.CopyTo(target.AsSpan(RetainedLoaderLayout.PrefixBytes));
        target.AsSpan(InstallationIdentity.Offset(target.Length), InstallationIdentity.Size).Fill(0xFF);
        return target;
    }

    public static void ValidateImage(byte[] current, byte[] target)
    {
        if (target.Length != RetainedLoaderLayout.FlashBytes)
            throw new InvalidDataException("MANUFACTURER_TARGET_SIZE_INVALID");
        var expected = Compose(current, target.AsSpan(RetainedLoaderLayout.PrefixBytes, ApplicationBytes));
        if (!target.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("MANUFACTURER_TARGET_CHANGED");
    }
}
