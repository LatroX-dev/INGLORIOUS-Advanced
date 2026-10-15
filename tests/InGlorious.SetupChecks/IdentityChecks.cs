using System.Buffers.Binary;
using System.Security.Cryptography;
using InGlorious.Core;

internal static partial class Program
{
    private static void CheckIdentity()
    {
        var id = Guid.Parse("502192c6-e07c-4f3f-83a3-a96e792ab027");
        var engineeringImage = Enumerable.Repeat((byte)0xFF, 262144).ToArray();
        new InstallationIdentity(16, id, new string('0', 64)).Encode().CopyTo(engineeringImage, InstallationIdentity.Offset(engineeringImage.Length));
        Reject(() => InstallationIdentity.CreateImage(new byte[64], 262144, new(16, id, new string('0', 64))),
            "Retained-loader image creation requires its own verified original, never a synthetic prefix.");
        Reject(() => KnownFirmwareImages.Validate(engineeringImage, "gmmk3-pro-100-iso", 16),
            "A structurally valid engineering marker cannot whitelist an unapproved firmware image.");
        foreach (var flashBytes in new[] { 131072, 262144 })
        {
            var original = Enumerable.Range(0, flashBytes).Select(i => (byte)(i * 17 + 3)).ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(original));
            var firmware = Enumerable.Range(0, 65536).Select(i => (byte)(i * 7)).ToArray();
            var firmwareBefore = (byte[])firmware.Clone();
            var offset = InstallationIdentity.Offset(flashBytes);
            Check(offset == flashBytes - 32768 - 256, "Identity lies outside both application and QMK settings.");
            Check(InstallationIdentity.Read(original) is null, "Ordinary original bytes do not invent an identity.");
            for (byte board = 1; board <= 15; board++)
            {
                var identity = new InstallationIdentity(board, id, hash.ToLowerInvariant());
                var encoded = identity.Encode();
                var image = InstallationIdentity.CreateImage(firmware, flashBytes, identity);
                var decoded = InstallationIdentity.Read(image);
                Check(encoded.Length == 64 && encoded.AsSpan(0, 8).SequenceEqual("IGIDENT1"u8), "Identity has exact format and length.");
                Check(decoded is not null && decoded.BoardId == board && decoded.InstallationId == id && decoded.OriginalSha256 == hash,
                    "Every supported board preserves ID and full original SHA256.");
                Check(image.AsSpan(0, firmware.Length).SequenceEqual(firmwareBefore), "Installation identity cannot mutate firmware bytes.");
                Check(firmware.SequenceEqual(firmwareBefore), "Caller firmware buffer remains unchanged.");
                Check(image.AsSpan(firmware.Length, offset - firmware.Length).IndexOfAnyExcept((byte)0xFF) < 0, "Unused application gap remains erased.");
                Check(image.AsSpan(offset, 64).SequenceEqual(encoded), "Exactly one identity record is embedded.");
                Check(image.AsSpan(offset + 64).IndexOfAnyExcept((byte)0xFF) < 0, "All remaining gap and original-install settings remain erased.");
                Check(decoded!.MatchesOriginal(original), "Identity binds the complete original image hash.");
                var wrong = (byte[])original.Clone(); wrong[^1] ^= 1;
                Check(!decoded.MatchesOriginal(wrong), "A different settings byte invalidates original-image binding.");
                Check(InstallationIdentity.Read((byte[])image.Clone()) == decoded,
                    "Cloning flash clones identity: this format must not claim immutable hardware identity.");
            }
            var valid = InstallationIdentity.CreateImage(firmware, flashBytes, new(2, id, hash));
            for (var i = 8; i < InstallationIdentity.Size; i++)
            {
                var corrupt = (byte[])valid.Clone(); corrupt[offset + i] ^= 0x40;
                Reject(() => InstallationIdentity.Read(corrupt), "Corrupt identity metadata/CRC cannot be accepted.");
            }
            foreach (var index in new[] { 8, 9, 10, 11 })
            {
                var invalid = (byte[])valid.Clone();
                invalid[offset + index] = index == 9 ? (byte)17 : (byte)2;
                RecalculateIdentityCrc(invalid.AsSpan(offset, 64));
                Reject(() => InstallationIdentity.Read(invalid), "Valid CRC cannot bypass version, board, or reserved-byte validation.");
            }
            var emptyGuid = (byte[])valid.Clone();
            emptyGuid.AsSpan(offset + 12, 16).Clear(); RecalculateIdentityCrc(emptyGuid.AsSpan(offset, 64));
            Reject(() => InstallationIdentity.Read(emptyGuid), "Empty installation IDs are rejected even with a valid CRC.");
            var maximum = new byte[offset];
            Check(InstallationIdentity.Read(InstallationIdentity.CreateImage(maximum, flashBytes, new(1, id, hash))) is not null,
                "Firmware may end exactly at the identity boundary.");
            Reject(() => InstallationIdentity.CreateImage(new byte[offset + 1], flashBytes, new(1, id, hash)), "Firmware may not overlap identity.");
        }
        foreach (var bytes in new[] { 0, 65536, 131071, 131073, 262143, 262145 })
            Reject(() => InstallationIdentity.Read(new byte[bytes]), "Only supported complete flash geometries are allowed.");
        var goodHash = new string('A', 64);
        foreach (var board in new byte[] { 0, 17, 255 }) Reject(() => new InstallationIdentity(board, id, goodHash).Encode(), "Unknown boards cannot be encoded.");
        Reject(() => new InstallationIdentity(1, Guid.Empty, goodHash).Encode(), "Empty ID cannot be encoded.");
        Reject(() => new InstallationIdentity(1, id, new string('Z', 64)).Encode(), "Nonhex hash cannot be encoded.");
        Reject(() => new InstallationIdentity(1, id, "1234").Encode(), "Truncated hash cannot be encoded.");
        Reject(() => InstallationIdentity.CreateImage(new byte[7], 131072, new(1, id, goodHash)), "Empty/truncated application cannot become an install image.");
    }

    private static void RecalculateIdentityCrc(Span<byte> record)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in record[..60])
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(record[60..], ~crc);
    }
}
