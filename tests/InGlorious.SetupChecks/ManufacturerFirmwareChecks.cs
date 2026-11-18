using System.Net.Http;
using System.Security.Cryptography;
using InGlorious.Core;

internal static partial class Program
{
    private static async Task CheckManufacturerFirmware()
    {
        const int packageBytes = 1948369;
        foreach (var length in new[] { 0, packageBytes - 1, packageBytes, packageBytes + 1 })
        {
            using var wrong = new ByteArrayContent(new byte[length]);
            wrong.Headers.ContentLength = packageBytes;
            await RejectAsync(() => ManufacturerFirmwareDownload.ReadPackageAsync(wrong, CancellationToken.None),
                "Truncated, changed and oversized manufacturer packages must be rejected even with the expected length header.");
        }
        Reject(() => ManufacturerFirmware.ValidateApplication(new byte[49532]), "A different manufacturer's payload cannot be used.");
        Reject(() => ManufacturerFirmware.ValidateImage(new byte[262144], new byte[131072]), "A shorter flash target cannot represent this model.");
        Reject(() => ManufacturerFirmware.ValidateImage(new byte[262144], new byte[262144]), "An unpinned target cannot pass the adapter check.");

        var packagePath = Environment.GetEnvironmentVariable("INGLORIOUS_TEST_MANUFACTURER_PACKAGE");
        if (packagePath is null) return;
        using var content = new StreamContent(File.OpenRead(packagePath));
        var application = await ManufacturerFirmwareDownload.ReadPackageAsync(content, CancellationToken.None);
        Check(application.Length == 49532, "The reviewed manufacturer package yields the application only.");
        if (RetainedOriginalPair() is not { } original) return;

        using var fixture = new SetupFixture(RetainedLoaderLayout.DeviceId);
        var code = File.ReadAllBytes(Path.Combine(fixture.Root, "Firmware", "gmmk3-pro-100-iso.bin"));
        var identity = new InstallationIdentity(RetainedLoaderLayout.BoardId, Guid.NewGuid(), Convert.ToHexString(SHA256.HashData(original)));
        var current = InstallationIdentity.CreateImage(code, RetainedLoaderLayout.FlashBytes, identity, original, ProfileBankLayout.Name);
        current[^1] = 0x12;
        var before = current.ToArray();
        var target = ManufacturerFirmware.CreateImage(current, application);
        ManufacturerFirmware.ValidateImage(current, target);
        StockFirmwareInspector.Validate(target, 0x342D, 0xE3CE, RetainedLoaderLayout.DeviceId);
        int start = RetainedLoaderLayout.PrefixBytes, end = start + application.Length, marker = InstallationIdentity.Offset(current.Length);
        Check(current.AsSpan().SequenceEqual(before) && target.AsSpan(start, application.Length).SequenceEqual(application) &&
            target.AsSpan(marker, InstallationIdentity.Size).IndexOfAnyExcept((byte)0xFF) < 0 &&
            target.AsSpan(0, start).SequenceEqual(current.AsSpan(0, start)) &&
            target.AsSpan(end, marker - end).SequenceEqual(current.AsSpan(end, marker - end)) &&
            target.AsSpan(marker + InstallationIdentity.Size).SequenceEqual(current.AsSpan(marker + InstallationIdentity.Size)),
            "Manufacturer recovery changes only the complete approved application and the INGLORIOUS identity.");
        foreach (var offset in new[] { 0, start, end - 1, end, ProfileBankLayout.Start, marker, marker + InstallationIdentity.Size, target.Length - 1 })
        {
            var changed = target.ToArray();
            changed[offset] ^= 1;
            Reject(() => ManufacturerFirmware.ValidateImage(current, changed), "Every targeted and preserved region is checked.");
        }
        current[start + 10] ^= 1;
        Reject(() => ManufacturerFirmware.CreateImage(current, application), "An unknown current application cannot be replaced.");
        Reject(() => ManufacturerFirmware.ValidateImage(original, target), "Stock firmware is never a source for this recovery operation.");
    }
}
