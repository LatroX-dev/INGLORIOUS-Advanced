using System.Buffers.Binary;
using InGlorious.Core;

internal static partial class Program
{
    private static void CheckStockFirmwareInspector()
    {
        foreach (var match in StockDeviceCatalog.All)
        foreach (var model in match.CandidateDeviceIds)
        {
            var length = model is "pro-ansi" or "pro-iso" ? 262144 : 131072;
            var descriptor = UsbDescriptor(match.VendorId, match.ProductId);
            foreach (var offset in new[] { 0, 11, 791, length - 18 })
            {
                var capture = Enumerable.Repeat((byte)0xFF, length).ToArray();
                descriptor.CopyTo(capture, offset);
                StockFirmwareInspector.Validate(capture, match.VendorId, match.ProductId, model);
                Check(true, "All models recognize normal descriptors at arbitrary valid offsets, including the last complete one.");
            }
        }
        var valid = new byte[131072];
        UsbDescriptor(0x320F, 0x5088).CopyTo(valid, 37);
        var preserved = (byte[])valid.Clone();
        StockFirmwareInspector.Validate(valid, 0x320F, 0x5088, "numpad");
        Check(valid.SequenceEqual(preserved), "Capture validation is read-only.");
        foreach (var pair in new (ushort, ushort)[] { (0x320F,0xB00D), (0x342D,0xDFA0), (0x0483,0xDF11), (0x2442,0xB051) })
        {
            var bootAndStock = (byte[])valid.Clone(); UsbDescriptor(pair.Item1,pair.Item2).CopyTo(bootAndStock, 71);
            StockFirmwareInspector.Validate(bootAndStock, 0x320F,0x5088,"numpad");
            Check(true, "Bootloader descriptors cannot override the stock model.");
        }
        var conflict = (byte[])valid.Clone(); UsbDescriptor(0x342D,0xE3D3).CopyTo(conflict, 71);
        Reject(() => StockFirmwareInspector.Validate(conflict, 0x320F,0x5088,"numpad"), "Another known model blocks despite a matching descriptor.");
        Reject(() => StockFirmwareInspector.Validate(valid, 0x342D,0xE3CA,"gmmk3-65-ansi"), "Wrong normal pair must not borrow another model's descriptor.");
        Reject(() => StockFirmwareInspector.Validate(valid, 0x320F,0x5088,"pro-iso"), "Caller cannot replace the resolved layout/model.");
        Reject(() => StockFirmwareInspector.Validate(valid, 0x504B,0x320F,"gmmk3-65-ansi"), "Copied manufacturer-QMK IDs are not stock evidence.");
        Reject(() => StockFirmwareInspector.Validate(valid, 0x320F,0x5088,"NUMPAD"), "Unknown/noncanonical model is rejected.");
        Reject(() => StockFirmwareInspector.Validate(new byte[131072], 0x320F,0x5088,"numpad"), "No known normal descriptor means no approval.");
        Reject(() => StockFirmwareInspector.Validate(UsbDescriptor(0x320F,0x5088), 0x320F,0x5088,"numpad"), "A descriptor fragment is not a full capture.");
        var bootOnly = new byte[131072]; UsbDescriptor(0x320F,0xB00D).CopyTo(bootOnly,37);
        Reject(() => StockFirmwareInspector.Validate(bootOnly, 0x320F,0x5088,"numpad"), "Bootloader alone cannot identify the expected stock firmware.");
        foreach (var (index,value) in new (int, byte)[] { (0,17),(1,2),(2,0xFA),(3,0),(4,0x22),(5,1),(6,1),(7,0),(7,63),(17,0),(17,255) })
        {
            var malformed = (byte[])valid.Clone(); malformed[37+index] = value;
            Reject(() => StockFirmwareInspector.Validate(malformed, 0x320F,0x5088,"numpad"), "Malformed descriptor fields cannot identify stock.");
        }
        var truncated = new byte[131072]; UsbDescriptor(0x320F,0x5088).AsSpan(0,17).CopyTo(truncated.AsSpan(truncated.Length-17));
        Reject(() => StockFirmwareInspector.Validate(truncated, 0x320F,0x5088,"numpad"), "Tail-truncated descriptor does not overread or match.");
        var differentRevision = new byte[262144];
        UsbDescriptor(0x320F,0x5044).CopyTo(differentRevision,37); UsbDescriptor(0x320F,0x5092).CopyTo(differentRevision,71);
        Reject(() => StockFirmwareInspector.Validate(differentRevision,0x320F,0x5044,"pro-ansi"), "WB alternate descriptor conflicts with resolved STM32 PRO.");
        StockFirmwareInspector.Validate(differentRevision,0x320F,0x5044,"pro-rev2-ansi");
        Check(true, "Compatible catalog aliases do not contradict an independently resolved WB model.");
        // Deliberately documents the limit: valid copied/appended bytes can satisfy
        // a structural check. There is no authenticity or specimen-binding claim.
        StockFirmwareInspector.Validate(preserved,0x320F,0x5088,"numpad");
        Check(true, "Synthetic valid descriptor demonstrates structural, not cryptographic, evidence.");
    }

    private static byte[] UsbDescriptor(ushort vid, ushort pid)
    {
        byte[] value = [18,1,0,2,0,0,0,64,0,0,0,0,0,1,1,2,3,1];
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(8),vid);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(10),pid);
        return value;
    }
}
