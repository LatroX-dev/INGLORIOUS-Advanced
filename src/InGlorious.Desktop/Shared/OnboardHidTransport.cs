using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Encodings;

namespace InGlorious.Core;

public sealed record OnboardDevice(string DeviceId, string Name, string Path);

/// <summary>Portable transport for the private InGlorious collection only. No stock or bootloader commands.</summary>
public sealed class OnboardHidTransport : IOnboardTransport
{
    private readonly HidStream _stream;
    private int _closed;
    private OnboardHidTransport(HidStream stream) { _stream = stream; _stream.ReadTimeout = 130_000; _stream.WriteTimeout = 4_000; }
    public static IReadOnlyList<OnboardDevice> FindDevices()
    {
        var result = new List<OnboardDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices(BoardDescriptorRegistry.RuntimeVendorId).Where(d => BoardDescriptorRegistry.TryGetRuntimeUsb(d.VendorID, d.ProductID, out _)))
        {
            try { if (Identify(device) is { } identity) result.Add(identity); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return result;
    }
    private static OnboardDevice? Identify(HidDevice device)
    {
        if (!BoardDescriptorRegistry.TryGetRuntimeUsb(device.VendorID, device.ProductID, out var board) || board.IsEngineeringOnly) return null;
        var id = board.DeviceId;
        if (!OnboardIdentity.MatchesNames(id, device.GetManufacturer(), device.GetProductName()) || device.GetMaxInputReportLength() != 33 || device.GetMaxOutputReportLength() != 33) return null;
        if (!IsAllowedDescriptor(device.GetReportDescriptor())) return null;
        return new(id, DisplayName(id), device.DevicePath);
    }
    internal static string DisplayName(string deviceId) => BoardDescriptorRegistry.Get(deviceId).RuntimeName;
    public static bool IsAllowedDescriptor(ReportDescriptor descriptor)
    {
        if (descriptor.DeviceItems.Count != 1 || descriptor.ReportsUseID || descriptor.MaxInputReportLength != 33 || descriptor.MaxOutputReportLength != 33) return false;
        var item = descriptor.DeviceItems[0];
        if (item.CollectionType != CollectionType.Application || !item.Usages.GetAllValues().SequenceEqual(new uint[] { 0xFF600061 })) return false;
        var input = item.InputReports.ToArray(); var output = item.OutputReports.ToArray();
        return input.Length == 1 && output.Length == 1 && !item.FeatureReports.Any() &&
            input[0].ReportID == 0 && output[0].ReportID == 0 && input[0].Length == 33 && output[0].Length == 33;
    }
    public static OnboardHidTransport Open(OnboardDevice expected)
    {
        var board = BoardDescriptorRegistry.Get(expected.DeviceId);
        if (board.IsEngineeringOnly) throw new InvalidDataException(L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar."));
        var device = DeviceList.Local.GetHidDevices(BoardDescriptorRegistry.RuntimeVendorId).SingleOrDefault(d => d.DevicePath == expected.Path && d.ProductID == board.RuntimeProductId)
            ?? throw new IOException(L.T("Keine passende eigene INGLORIOUS-Firmware erkannt. Ein Gerät mit Original-Firmware kann hier noch nicht ausgelesen werden."));
        if (Identify(device) != expected) throw new IOException(L.T("Die gelesene Konfiguration passt nicht zum gewählten Gerät."));
        var stream = device.Open();
        try
        {
            if (Identify(stream.Device) != expected) throw new IOException(L.T("Die gelesene Konfiguration passt nicht zum gewählten Gerät."));
            return new(stream);
        }
        catch { stream.Dispose(); throw; }
    }
    public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
    {
        ValidateOutbound(packet);
        ObjectDisposedException.ThrowIf(_closed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Closing on cancellation unblocks native I/O even where Stream's default async shim cannot cancel it.
        // A cancelled connection is never reused, and a timed-out COMMIT is never retried here.
        using var registration = cancellationToken.Register(Close);
        var output = new byte[33]; packet.CopyTo(output, 1);
        await _stream.WriteAsync(output, 0, output.Length, cancellationToken).ConfigureAwait(false);
        var input = new byte[33];
        var count = await _stream.ReadAsync(input, 0, input.Length, cancellationToken).ConfigureAwait(false);
        if (count != 33 || input[0] != 0) throw new IOException("Incomplete INGLORIOUS HID report.");
        cancellationToken.ThrowIfCancellationRequested();
        return input.AsSpan(1).ToArray();
    }
    public static void ValidateOutbound(byte[] packet)
    {
        OnboardProtocol.ValidateRequest(packet);
    }
    private void Close() { if (Interlocked.Exchange(ref _closed, 1) == 0) _stream.Dispose(); }
    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}
