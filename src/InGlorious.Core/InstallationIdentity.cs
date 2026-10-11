using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace InGlorious.Core;

/// <summary>A per-installation recovery identity in the unused gap before QMK storage.
/// It is not a physical MCU UID or proof of a board model.</summary>
public sealed record InstallationIdentity(byte BoardId, Guid InstallationId, string OriginalSha256)
{
    public const int Size = 64;
    public const int StorageBytes = 32768;
    public static int Offset(int flashBytes) => flashBytes is 131072 or 262144
        ? flashBytes - StorageBytes - 256 : throw new InvalidDataException("Unsupported flash size.");

    public byte[] Encode()
    {
        if (BoardId is < 1 or > 15 || InstallationId == Guid.Empty || InstallationId == new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff") || OriginalSha256.Length != 64)
            throw new InvalidDataException("Invalid installation identity.");
        var bytes = new byte[Size];
        "IGIDENT1"u8.CopyTo(bytes);
        bytes[8] = 1; bytes[9] = BoardId;
        InstallationId.TryWriteBytes(bytes.AsSpan(12, 16));
        Convert.FromHexString(OriginalSha256).CopyTo(bytes, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), Crc32(bytes.AsSpan(0, 60)));
        return bytes;
    }

    public static InstallationIdentity? Read(ReadOnlySpan<byte> flash)
    {
        var record = flash.Slice(Offset(flash.Length), Size);
        if (!record[..8].SequenceEqual("IGIDENT1"u8)) return null;
        if (record[8] != 1 || record[9] is < 1 or > 15 || record[10] != 0 || record[11] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(record[60..]) != Crc32(record[..60]))
            throw new InvalidDataException("Invalid recovery identity.");
        var id = new Guid(record.Slice(12, 16));
        if (id == Guid.Empty || id == new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff")) throw new InvalidDataException("Empty recovery identity.");
        return new(record[9], id, Convert.ToHexString(record.Slice(28, 32)));
    }

    public static byte[] CreateImage(ReadOnlySpan<byte> firmware, int flashBytes, InstallationIdentity identity)
    {
        if (firmware.Length < 8 || firmware.Length > Offset(flashBytes))
            throw new InvalidDataException("Firmware overlaps recovery identity or is empty.");
        var image = new byte[flashBytes];
        Array.Fill(image, (byte)0xFF);
        firmware.CopyTo(image);
        identity.Encode().CopyTo(image, Offset(flashBytes));
        return image;
    }

    public bool MatchesOriginal(ReadOnlySpan<byte> original) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(OriginalSha256), SHA256.HashData(original));

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320);
        }
        return ~crc;
    }
}
