using System.Buffers.Binary;
using System.IO;

namespace InGlorious.Core;

/// <summary>Bounded persistent-image geometry and read-only envelope validation.
/// This class never repairs, normalizes or creates bank records.</summary>
public static class ProfileBankLayout
{
    public const string Name = "profile-pairs-v1";
    public const int Start = 0x2A000, Length = 49152, End = Start + Length;
    public const int RecordBytes = 8192, PayloadOffset = 256, PayloadCapacity = 7680, CommitOffset = 7936;
    public sealed record Profile(byte Slot, ulong Generation, ushort Schema, byte[] Payload);
    public sealed record Scan(IReadOnlyList<string> RecordStates, IReadOnlyList<Profile> Profiles);

    public static void ValidateGeometry(string deviceId, byte boardId, int flashBytes, int applicationOffset, int applicationBytes, string? layout)
    {
        if (layout is not null && layout != Name) throw new InvalidDataException("UNKNOWN_PERSISTENT_LAYOUT");
        if (layout == Name && (deviceId != "gmmk3-pro-100-iso" || boardId != 16 || flashBytes != 0x40000 ||
            applicationOffset != 0x7000 || applicationBytes < 8 || applicationBytes > Start - applicationOffset))
            throw new InvalidDataException("PROFILE_BANK_GEOMETRY_INVALID");
    }
    public static bool GapIsValid(ReadOnlySpan<byte> image, int applicationEnd, string? layout)
    {
        int marker = InstallationIdentity.Offset(image.Length);
        if (layout is null) return !image.Slice(applicationEnd, marker - applicationEnd).ContainsAnyExcept((byte)255);
        if (layout != Name || image.Length != 0x40000 || applicationEnd > Start) return false;
        if (image.Slice(applicationEnd, Start - applicationEnd).ContainsAnyExcept((byte)255) ||
            image.Slice(End, marker - End).ContainsAnyExcept((byte)255)) return false;
        _ = Inspect(image.Slice(Start, Length));
        return true;
    }
    public static Scan Inspect(ReadOnlySpan<byte> bank)
    {
        if (bank.Length != Length) throw new InvalidDataException("PROFILE_BANK_SIZE_INVALID");
        var states = new string[6]; var records = new Profile?[6];
        for (int i = 0; i < 6; i++)
        {
            var r = bank.Slice(i * RecordBytes, RecordBytes); var c = r[CommitOffset..];
            byte slot = (byte)(i / 2 + 1);
            if (!r.ContainsAnyExcept((byte)255)) { states[i] = "empty"; continue; }
            bool header = r[..8].SequenceEqual("IGPBANK1"u8) && U32(r, 36) == Crc(r[..36]);
            bool commit = c[..8].SequenceEqual("IGPCMT1!"u8) && U32(c, 28) == Crc(c[..28]);
            if ((header && U16(r, 8) != 1) || (commit && U16(c, 8) != 1)) throw new InvalidDataException("PROFILE_BANK_FUTURE_FORMAT");
            if ((header && (U16(r, 12) != 16 || r[14] != slot)) || (commit && (U16(c, 10) != 16 || c[12] != slot)))
                throw new InvalidDataException("PROFILE_BANK_FOREIGN_RECORD");
            states[i] = "torn";
            if (!header || !commit || U16(r, 10) != 256 || r[15] != 0 || U16(r, 18) != 0 ||
                r.Slice(40, 216).ContainsAnyExcept((byte)255) || c[32..].ContainsAnyExcept((byte)255) ||
                c.Slice(13, 3).ContainsAnyExcept((byte)0)) continue;
            uint size = U32(r, 20); ulong generation = U64(r, 24); ushort schema = U16(r, 16);
            if (size is < 1 or > PayloadCapacity || generation == 0 || schema == 0 || U64(c, 16) != generation) continue;
            var payload = r.Slice(PayloadOffset, (int)size);
            if (r.Slice(PayloadOffset + (int)size, PayloadCapacity - (int)size).ContainsAnyExcept((byte)255) ||
                U32(r, 32) != Crc(payload) || U32(c, 24) != Crc(r[..CommitOffset])) continue;
            // Semantic IGCF decoding belongs to the caller that exports/opens a
            // profile. Firmware updates preserve every byte, including torn data.
            records[i] = new(slot, generation, schema, payload.ToArray()); states[i] = "valid";
        }
        var selected = new List<Profile>();
        for (int i = 0; i < 6; i += 2)
        {
            var a = records[i]; var b = records[i + 1];
            if (a is not null && b is not null && a.Generation == b.Generation) throw new InvalidDataException("PROFILE_BANK_AMBIGUOUS_GENERATION");
            var latest = a is null ? b : b is null || a.Generation > b.Generation ? a : b;
            if (latest is not null) selected.Add(latest);
        }
        return new(states, selected);
    }
    public static void Preserve(ReadOnlySpan<byte> before, Span<byte> target, string? sourceLayout, string? targetLayout)
    {
        if (sourceLayout is not null && sourceLayout != Name || targetLayout is not null && targetLayout != Name)
            throw new InvalidDataException("UNKNOWN_PERSISTENT_LAYOUT");
        if (sourceLayout == Name && targetLayout != Name) throw new InvalidDataException("PROFILE_BANK_DOWNGRADE_FORBIDDEN");
        if (before.Length != target.Length || before.Length is not (131072 or 262144)) throw new InvalidDataException("PROFILE_BANK_IMAGE_SIZE_INVALID");
        if (sourceLayout == Name)
        {
            if (before.Length != 0x40000) throw new InvalidDataException("PROFILE_BANK_IMAGE_SIZE_INVALID");
            _ = Inspect(before.Slice(Start, Length));
            before.Slice(Start, Length).CopyTo(target.Slice(Start, Length));
        }
        else if (targetLayout == Name && (target.Length != 0x40000 || target.Slice(Start, Length).ContainsAnyExcept((byte)255)))
            throw new InvalidDataException("PROFILE_BANK_INITIAL_STATE_INVALID");
        before[^InstallationIdentity.StorageBytes..].CopyTo(target[^InstallationIdentity.StorageBytes..]);
    }
    public static void RequirePreserved(ReadOnlySpan<byte> before, ReadOnlySpan<byte> target, string? sourceLayout, string? targetLayout)
    {
        var expected = target.ToArray(); Preserve(before, expected, sourceLayout, targetLayout);
        if (!expected.AsSpan().SequenceEqual(target)) throw new InvalidDataException("UPDATE_CONFIGURATION_CHANGED");
    }
    public static void RequireInitialState(ReadOnlySpan<byte> image, string? layout)
    {
        if (layout is not null && layout != Name) throw new InvalidDataException("UNKNOWN_PERSISTENT_LAYOUT");
        if (layout == Name && (image.Length != 0x40000 || image.Slice(Start, Length).ContainsAnyExcept((byte)255)))
            throw new InvalidDataException("PROFILE_BANK_INITIAL_STATE_INVALID");
    }
    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes) { value ^= b; for (int bit = 0; bit < 8; bit++) value = value >> 1 ^ ((value & 1) != 0 ? 0xEDB88320u : 0); }
        return ~value;
    }
}
