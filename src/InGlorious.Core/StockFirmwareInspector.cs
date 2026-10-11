using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InGlorious.Core;

/// <summary>
/// Independent structural model check of a complete original flash capture.
/// USB descriptor bytes can be copied or appended: this is neither firmware
/// authentication nor a unique specimen identity. The caller must first verify
/// two complete identical readbacks and the actual MCU/transport geometry.
/// </summary>
public static class StockFirmwareInspector
{
    public static void Validate(ReadOnlySpan<byte> original, ushort expectedVid, ushort expectedPid, string resolvedDeviceId)
    {
        if (original.Length is not (131072 or 262144))
            throw new InvalidDataException("A complete supported flash capture is required.");
        var expected = StockDeviceCatalog.Match(expectedVid, expectedPid);
        if (expected is null || string.IsNullOrEmpty(resolvedDeviceId) ||
            !expected.CandidateDeviceIds.Contains(resolvedDeviceId, StringComparer.Ordinal))
            throw new InvalidDataException("The normal USB identity does not match the resolved model.");

        var foundExpected = false;
        for (var offset = 0; offset <= original.Length - 18; offset++)
        {
            var descriptor = original.Slice(offset, 18);
            if (!IsDeviceDescriptor(descriptor)) continue;
            var vid = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[8..]);
            var pid = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[10..]);
            var match = StockDeviceCatalog.Match(vid, pid);
            if (match is null) continue; // Includes vendor-updater/ROM bootloader identities.
            if (!match.CandidateDeviceIds.Contains(resolvedDeviceId, StringComparer.Ordinal))
                throw new InvalidDataException("The original capture contains a conflicting known keyboard model.");
            if (vid == expectedVid && pid == expectedPid) foundExpected = true;
        }
        if (!foundExpected)
            throw new InvalidDataException("The original capture does not confirm the expected normal USB identity.");
    }

    private static bool IsDeviceDescriptor(ReadOnlySpan<byte> value)
    {
        if (value[0] != 18 || value[1] != 1) return false;
        var usb = BinaryPrimitives.ReadUInt16LittleEndian(value[2..]);
        if (usb is < 0x0100 or > 0x0320 || !IsBcd(usb)) return false;
        // These composite/HID/vendor device classes are plausible for the supported
        // keyboards. Class zero requires the interface descriptors to supply class.
        if (!((value[4] == 0 && value[5] == 0 && value[6] == 0) ||
              (value[4] == 3 && value[5] <= 1 && value[6] <= 2) ||
              (value[4] == 0xEF && value[5] == 2 && value[6] == 1) || value[4] == 0xFF)) return false;
        if (usb >= 0x0300 ? value[7] != 9 : value[7] is not (8 or 16 or 32 or 64)) return false;
        if (value[17] is 0 or > 8) return false;
        var vendor = BinaryPrimitives.ReadUInt16LittleEndian(value[8..]);
        var product = BinaryPrimitives.ReadUInt16LittleEndian(value[10..]);
        return vendor is not (0 or 0xFFFF) && product is not (0 or 0xFFFF);
    }

    private static bool IsBcd(ushort value) =>
        (value & 0xF) <= 9 && ((value >> 4) & 0xF) <= 9 &&
        ((value >> 8) & 0xF) <= 9 && ((value >> 12) & 0xF) <= 9;
}
