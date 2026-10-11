// WB32 packet layout adapted from wb32-dfu-updater, commit 656f6a56354de86a8213b0d2dc8349b0aeb4b67e.
// Copyright (C) 2021 Westberry Technology (ChangZhou) Corp., Ltd. (Apache-2.0).
// INGLORIOUS adaptation: bounded C# commands, validation and tests (GPL-3.0-or-later).
// Preserve Licenses/Westberry-Apache-2.0.txt and the project LICENSE with distributions.
using System.Buffers.Binary;
using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal interface IWbExchange { byte[] Exchange(byte[] request); }
internal sealed record WbInfo(byte BootVersion, uint ChipId, int FlashBytes, int SramBytes);
internal static class WbProtocol
{
    internal const int Page = 256;
    internal static WbInfo GetInfo(IWbExchange connection)
    {
        byte[] response = connection.Exchange([1, 0]); Check(response, 1);
        if (response.Length != 16 || response[2] != 13) throw new InvalidDataException("WB_INFO_SHAPE");
        var info = new WbInfo(response[3], U32(response, 4), checked((int)U32(response, 8)), checked((int)U32(response, 12)));
        // Westberry's downloader tests this family mask, not the Numpad's whole chip-type value.
        // Geometry is independently fixed by the selected source-backed model.
        if ((info.ChipId & 0x3FFF) != 0x2980 || info.BootVersion != 3 || info.FlashBytes != 131072 || info.SramBytes != 28672)
            throw new InvalidDataException("WB_INCOMPATIBLE_GEOMETRY_OR_PROTOCOL");
        return info;
    }
    internal static byte[] ReadPacket(uint address, ushort count)
    {
        Range(address, count);
        if (count is 0 or > Page) throw new InvalidDataException("WB_READ_SIZE");
        byte[] packet = new byte[10]; packet[0] = 0x72;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), address);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), count); return packet;
    }
    internal static byte[] Read(IWbExchange connection, uint address, ushort count)
    {
        byte[] response = connection.Exchange(ReadPacket(address, count)); Check(response, 0x72);
        if (response.Length != count + 2) throw new IOException("WB_READ_LENGTH");
        return response[2..];
    }
    internal static byte[] ErasePacket(uint address)
    {
        PageRange(address); byte[] packet = new byte[12]; packet[0] = 0x5E;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), address);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 1); return packet;
    }
    internal static byte[] WritePacket(uint address, ReadOnlySpan<byte> page)
    {
        PageRange(address); if (page.Length != Page) throw new InvalidDataException("WB_WRITE_SIZE");
        byte[] packet = new byte[12 + Page]; packet[0] = 0x6A;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), address);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), Page); page.CopyTo(packet.AsSpan(12)); return packet;
    }
    internal static void ValidatePacket(byte[] packet, bool writes)
    {
        if (packet.AsSpan().SequenceEqual(new byte[] { 1, 0 })) return;
        if (packet.Length < 10 || packet[1] != 0 || packet[2] != 0 || packet[3] != 0) throw new InvalidDataException("WB_COMMAND_REJECTED");
        uint address = U32(packet, 4);
        if (packet[0] == 0x72 && packet.Length == 10 && packet.AsSpan().SequenceEqual(ReadPacket(address, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8))))) return;
        if (writes && packet[0] == 0x5E && packet.Length == 12 && packet.AsSpan().SequenceEqual(ErasePacket(address))) return;
        if (writes && packet[0] == 0x6A && packet.Length == 268 && U32(packet, 8) == Page) { PageRange(address); return; }
        throw new InvalidDataException("WB_COMMAND_REJECTED");
    }
    internal static void Check(byte[] response, byte command)
    {
        if (response.Length < 2 || response[0] != command) throw new IOException("WB_RESPONSE_SHAPE");
        if (response[1] == 0x1A) throw new IOException("READ_PROTECTED_NO_UNLOCK");
        if (response[1] != 0) throw new IOException("WB_PROTOCOL_ERROR_" + response[1].ToString("X2"));
    }
    internal static void CheckWrite(byte[] response, byte command)
    { Check(response, command); if (response.Length != 2) throw new IOException("WB_WRITE_ACK_LENGTH"); }
    internal static IEnumerable<int> PageOrder(byte[] image)
    {
        if (image.Length != 131072) throw new InvalidDataException("WB_FULL_IMAGE_REQUIRED");
        int identityPage = InstallationIdentity.Offset(image.Length);
        // Install the checked recovery marker first; retain it until last during restore.
        // A failure during this single page can still destroy it: this is not atomic recovery.
        bool hasIdentity = InstallationIdentity.Read(image) != null;
        if (hasIdentity) yield return identityPage;
        for (int offset = 0; offset < image.Length; offset += Page)
            if (offset != identityPage) yield return offset;
        if (!hasIdentity) yield return identityPage;
    }
    internal static void WriteImage(IWbExchange connection, byte[] image)
    {
        foreach (int offset in PageOrder(image))
        {
            uint address = Model.FlashBase + (uint)offset;
            CheckWrite(connection.Exchange(ErasePacket(address)), 0x5E);
            CheckWrite(connection.Exchange(WritePacket(address, image.AsSpan(offset, Page))), 0x6A);
            if (!Read(connection, address, Page).AsSpan().SequenceEqual(image.AsSpan(offset, Page))) throw new IOException("WB_PAGE_VERIFY_FAILED");
        }
    }
    private static void Range(uint address, int count)
    { if (count < 1 || address < Model.FlashBase || (ulong)address + (uint)count > Model.FlashBase + 131072UL) throw new InvalidDataException("WB_ADDRESS_RANGE"); }
    private static void PageRange(uint address) { Range(address, Page); if (address % Page != 0) throw new InvalidDataException("WB_PAGE_ALIGNMENT"); }
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
}
