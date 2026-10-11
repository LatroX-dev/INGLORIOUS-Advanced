using System.IO;

namespace InGlorious.Core;

/// <summary>Pure boundary between encoders and a future Windows transport. No native
/// imports or device access. Never treat descriptor validation as write authorization.</summary>
public sealed record StockHidDescriptor(ushort VendorId, ushort ProductId, ushort UsagePage,
    ushort Usage, int FeatureReportByteLength, int InputReportByteLength, int OutputReportByteLength);

public static class StockHidContract
{
    public const bool LiveTransportImplemented = false;
    public const bool ConfigurationReadbackImplemented = false;
    public const bool PersistenceVerified = false;
    public static IReadOnlyList<string> ExecutionBlockers { get; } = Array.AsReadOnly(new[] {
        "Windows transport is not implemented or connected to the UI.",
        "Configuration readback, device acknowledgements and rollback are not established.",
        "Macro slot capacity, allocation and collision handling are not established.",
        "Complete original-device backup and a tested recovery path must be assessed separately.",
        "Power-cycle persistence and physical key/LED mapping have not been tested with INGLORIOUS packets."
    });

    public static void ValidateDescriptor(StockDevice device, StockHidDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ushort pid = device switch { StockDevice.ProIso => 0x5046, StockDevice.Numpad => 0x5088, _ => throw new ArgumentOutOfRangeException(nameof(device)) };
        if (descriptor.VendorId != 0x320f || descriptor.ProductId != pid || descriptor.UsagePage != 0xff01 || descriptor.Usage != 1 || descriptor.FeatureReportByteLength != StockProtocol.FeatureReportLength)
            throw new InvalidDataException("Descriptor does not match the exact stock USB configuration collection (320F:5046/5088, FF01:0001, Feature264). No fallback interface is allowed.");
    }

    public static void Transmit(StockDevice device, StockHidDescriptor descriptor, IReadOnlyList<StockPacket> packets)
    {
        ValidateDescriptor(device, descriptor);
        throw new NotSupportedException(string.Join(Environment.NewLine, ExecutionBlockers));
    }

    /// <summary>Matches the statically inspected native wrapper: short feature reports
    /// are copied then zero-padded to HIDP_CAPS.FeatureReportByteLength. No I/O.</summary>
    public static byte[] NormalizeFeatureReport(StockDevice device, StockHidDescriptor descriptor, StockPacket packet)
    {
        ValidateDescriptor(device, descriptor); ArgumentNullException.ThrowIfNull(packet);
        var input = packet.ToArray();
        if (input.Length != 264 && input.Length != 64) throw new InvalidDataException("Unexpected stock packet length.");
        if (input[0] != 7 || input[1] is not (1 or 3 or 4 or 5 or 6) || (input.Length == 64 && input[1] != 1)) throw new InvalidDataException("Unexpected stock configuration command.");
        var report = new byte[descriptor.FeatureReportByteLength];
        input.CopyTo(report, 0);
        return report;
    }
}
