using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace InGlorious.Core;

/// <summary>A per-installation recovery identity in the unused gap before QMK storage.
/// It is not a physical MCU UID or proof of a board model.</summary>
public sealed record InstallationIdentity(byte BoardId, Guid InstallationId, string? OriginalSha256)
{
    public string? LegacyStmSerial { get; private init; }
    private static readonly Guid ErasedId = new("ffffffff-ffff-ffff-ffff-ffffffffffff");
    public const int Size = 64;
    public const int StorageBytes = 32768;
    public static int Offset(int flashBytes) => flashBytes is 131072 or 262144
        ? flashBytes - StorageBytes - 256 : throw new InvalidDataException("Unsupported flash size.");

    public static InstallationIdentity CreateLegacyWithoutOriginal(Guid installationId, string serial)
    {
        if (installationId == Guid.Empty || installationId == ErasedId) throw new InvalidDataException("Empty recovery identity.");
        ValidateLegacyStmSerial(serial);
        return new(1, installationId, null) { LegacyStmSerial = serial.ToUpperInvariant() };
    }

    internal static void ValidateLegacyStmSerial(string serial)
    {
        if (serial is not { Length: 12 } || !serial.All(char.IsAsciiHexDigit) ||
            serial.All(c => c == '0') || serial.All(c => c is 'F' or 'f'))
            throw new InvalidDataException("LEGACY_ORIGINAL_IDENTITY_MISMATCH");
    }

    public byte[] Encode()
    {
        if (BoardId is < 1 or > 16 || InstallationId == Guid.Empty || InstallationId == ErasedId ||
            (LegacyStmSerial is null ? OriginalSha256?.Length != 64 : BoardId != 1 || OriginalSha256 is not null))
            throw new InvalidDataException("Invalid installation identity.");
        var bytes = new byte[Size];
        "IGIDENT1"u8.CopyTo(bytes);
        bytes[8] = LegacyStmSerial is null ? (byte)1 : (byte)2;
        bytes[9] = BoardId;
        InstallationId.TryWriteBytes(bytes.AsSpan(12, 16));
        // Version 1 holds the original SHA-256. Version 2 holds the six STM ROM
        // serial bytes followed by 26 reserved zeroes; it never asserts an original hash.
        Convert.FromHexString(LegacyStmSerial ?? OriginalSha256!).CopyTo(bytes, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), Crc32(bytes.AsSpan(0, 60)));
        return bytes;
    }

    public static InstallationIdentity? Read(ReadOnlySpan<byte> flash)
    {
        var record = flash.Slice(Offset(flash.Length), Size);
        if (!record[..8].SequenceEqual("IGIDENT1"u8)) return null;
        if (record[8] is not (1 or 2) || record[9] is < 1 or > 16 || record[10] != 0 || record[11] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(record[60..]) != Crc32(record[..60]))
            throw new InvalidDataException("Invalid recovery identity.");
        var id = new Guid(record.Slice(12, 16));
        if (id == Guid.Empty || id == ErasedId) throw new InvalidDataException("Empty recovery identity.");
        if (record[8] == 1) return new(record[9], id, Convert.ToHexString(record.Slice(28, 32)));
        if (flash.Length != 262144 || record[9] != 1 || record.Slice(34, 26).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Invalid legacy recovery identity.");
        return CreateLegacyWithoutOriginal(id, Convert.ToHexString(record.Slice(28, 6)));
    }

    public static byte[] CreateImage(ReadOnlySpan<byte> firmware, int flashBytes, InstallationIdentity identity, ReadOnlySpan<byte> original = default, string? storageLayout = null)
    {
        int applicationOffset = RetainedLoaderLayout.ApplicationOffset(identity.BoardId);
        if (applicationOffset != 0)
        {
            if (flashBytes != RetainedLoaderLayout.FlashBytes || original.Length != flashBytes || !identity.MatchesOriginal(original) || Read(original) is not null)
                throw new InvalidDataException("RETAINED_LOADER_ORIGINAL_REQUIRED");
        }
        return ComposeImage(firmware, flashBytes, identity, original, storageLayout);
    }

    internal static byte[] CreateRetainedUpdate(ReadOnlySpan<byte> firmware, InstallationIdentity identity, ReadOnlySpan<byte> current, string? storageLayout)
    {
        if (identity.BoardId != RetainedLoaderLayout.BoardId || current.Length != RetainedLoaderLayout.FlashBytes || Read(current) != identity)
            throw new InvalidDataException("RETAINED_UPDATE_IDENTITY_MISMATCH");
        return ComposeImage(firmware, current.Length, identity, current, storageLayout);
    }

    private static byte[] ComposeImage(ReadOnlySpan<byte> firmware, int flashBytes, InstallationIdentity identity, ReadOnlySpan<byte> loaderSource, string? storageLayout)
    {
        if (identity.LegacyStmSerial is not null && flashBytes != 262144)
            throw new InvalidDataException("LEGACY_MODEL_NOT_APPROVED");
        int applicationOffset = RetainedLoaderLayout.ApplicationOffset(identity.BoardId);
        ProfileBankLayout.ValidateGeometry(identity.BoardId == RetainedLoaderLayout.BoardId ? RetainedLoaderLayout.DeviceId : "", identity.BoardId,
            flashBytes, applicationOffset, firmware.Length, storageLayout);
        if (firmware.Length < 8 || firmware.Length > Offset(flashBytes) - applicationOffset)
            throw new InvalidDataException("Firmware overlaps recovery identity or is empty.");
        var image = new byte[flashBytes];
        Array.Fill(image, (byte)0xFF);
        if (applicationOffset != 0)
        {
            RetainedLoaderLayout.ValidatePrefix(loaderSource);
            RetainedLoaderLayout.ValidateApplicationVectors(firmware);
            loaderSource[..applicationOffset].CopyTo(image);
        }
        firmware.CopyTo(image.AsSpan(applicationOffset));
        identity.Encode().CopyTo(image, Offset(flashBytes));
        return image;
    }

    public bool MatchesOriginal(ReadOnlySpan<byte> original) =>
        OriginalSha256 is not null && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(OriginalSha256), SHA256.HashData(original));

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
