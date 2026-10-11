using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace InGlorious.Core;

public sealed record OnboardDevice(string DeviceId, string Name, string Path)
{
    public string? UsbInstanceId { get; init; }
}

/// <summary>Only the InGlorious raw-HID collection may be opened for I/O. Stock devices never match.</summary>
public sealed class OnboardHidTransport : IOnboardTransport
{
    private readonly FileStream _stream;
    private OnboardHidTransport(SafeFileHandle handle) => _stream = new FileStream(handle, FileAccess.ReadWrite, 33, true);
    public static IReadOnlyList<OnboardDevice> FindDevices()
    {
        var found = new List<OnboardDevice>();
        Native.HidD_GetHidGuid(out var guid);
        using var set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        for (uint i = 0; ; i++)
        {
            var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
            if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref item))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break;
                throw new Win32Exception(error);
            }
            Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, IntPtr.Zero, 0, out var size, IntPtr.Zero);
            if (size is < 6 or > 1048576) throw new IOException("Ungültiger HID-Gerätepfad.");
            var buffer = Marshal.AllocHGlobal((int)size);
            string path;
            var deviceInfo = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, buffer, size, out _, ref deviceInfo)) throw new Win32Exception(Marshal.GetLastWin32Error());
                path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
            if (!IsAllowedPath(path)) continue;
            using var handle = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!handle.IsInvalid && Identify(handle, path) is { } device) found.Add(device with { UsbInstanceId = ReadUsbRoot(deviceInfo.DevInst) });
        }
        return found;
    }

    public static bool IsAllowedPath(string path) => BoardDescriptorRegistry.TryGetRuntimeHidPath(path, out _);

    public static OnboardHidTransport Open(OnboardDevice device)
    {
        if (!BoardDescriptorRegistry.TryGet(device.DeviceId, out var board) ||
            !BoardDescriptorRegistry.TryGetRuntimeHidPath(device.Path, out var pathBoard) || board != pathBoard)
            throw new InvalidDataException("Nur INGLORIOUS-Firmware wird zur Konfiguration geöffnet.");
        var handle = Native.CreateFileW(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            var current = Identify(handle, device.Path);
            if (current == null || current.DeviceId != device.DeviceId || current.Name != device.Name) throw new InvalidDataException("Die Geräteidentität oder HID-Schnittstelle hat sich geändert.");
            if (device.UsbInstanceId is not null && !string.Equals(device.UsbInstanceId, ReadUsbRootForInterface(device.Path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die USB-Zuordnung des Geräts hat sich geändert.");
            return new(handle);
        }
        catch { handle.Dispose(); throw; }
    }
    internal static bool IsPhysicalUsbRoot(string? value) => value is { Length: > 0 and <= 4096 } &&
        Regex.IsMatch(value, @"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&[A-Z0-9_]+)*\\[^\\\r\n\x00]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !value[..value.LastIndexOf('\\')].Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    // Stop at the first physical USB ancestor; never infer a root from a model name or HID path substring.
    internal static string? ResolveUsbRoot(uint start, Func<uint, string?> readId, Func<uint, uint?> readParent)
    {
        var visited = new HashSet<uint>();
        var node = start;
        for (var depth = 0; depth < 8 && visited.Add(node); depth++)
        {
            var id = readId(node);
            if (id is null) return null;
            if (IsPhysicalUsbRoot(id)) return id;
            if (depth == 7 || readParent(node) is not { } parent) return null;
            node = parent;
        }
        return null;
    }
    private static string? ReadUsbRoot(uint start) => ResolveUsbRoot(start,
        node => { var value = new StringBuilder(4097); return Native.CM_Get_Device_IDW(node, value, value.Capacity, 0) == 0 ? value.ToString() : null; },
        node => Native.CM_Get_Parent(out var parent, node, 0) == 0 ? parent : null);

    private static string? ReadUsbRootForInterface(string path)
    {
        Native.HidD_GetHidGuid(out var guid);
        using var set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set.IsInvalid) return null;
        var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
        if (!Native.SetupDiOpenDeviceInterfaceW(set, path, 0, ref item)) return null;
        Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, IntPtr.Zero, 0, out var size, IntPtr.Zero);
        if (size is < 6 or > 1048576) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            var info = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
            return Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, buffer, size, out _, ref info) ? ReadUsbRoot(info.DevInst) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static OnboardDevice? Identify(SafeFileHandle handle, string path)
    {
        var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes) || !BoardDescriptorRegistry.TryGetRuntimeUsb(attributes.VendorId, attributes.ProductId, out var board)) return null;
        var id = board.DeviceId;
        var name = board.RuntimeName;
        if (!OnboardIdentity.MatchesNames(id, ReadString(handle, false), ReadString(handle, true))) return null;
        if (!Native.HidD_GetPreparsedData(handle, out var data)) return null;
        try
        {
            if (Native.HidP_GetCaps(data, out var caps) != 0x00110000 || caps.UsagePage != 0xFF60 || caps.Usage != 0x0061 || caps.InputReportByteLength != 33 || caps.OutputReportByteLength != 33) return null;
        }
        finally { Native.HidD_FreePreparsedData(data); }
        return new(id, name, path);
    }
    private static string ReadString(SafeFileHandle handle, bool product)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.Copy(new byte[512], 0, buffer, 512);
            var success = product ? Native.HidD_GetProductString(handle, buffer, 512) : Native.HidD_GetManufacturerString(handle, buffer, 512);
            return success ? (Marshal.PtrToStringUni(buffer, 256)?.Split('\0')[0] ?? "") : "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (packet.Length != 32 || packet[0] != 'I' || packet[1] != 'G' || packet[2] != 1 || (packet[3] is < 1 or > 6 && packet[3] is not OnboardProtocol.ReconnectUsb and not OnboardProtocol.ReadRuntimeBrightness and not OnboardProtocol.SetRuntimeBrightness and not OnboardProtocol.GetExtensions and not OnboardProtocol.LightingTest and not OnboardProtocol.GetFirmwareVersion) || (packet[3] is OnboardProtocol.ReconnectUsb or OnboardProtocol.ReadRuntimeBrightness or OnboardProtocol.GetExtensions or OnboardProtocol.GetFirmwareVersion && packet[6] != 0) || (packet[3] == OnboardProtocol.SetRuntimeBrightness && packet[6] != 1) || (packet[3] == OnboardProtocol.LightingTest && (packet[6] != 1 || packet[8] > 2)) || packet[6] > 24 || packet[5] != 0 || packet[7] != 0 || packet.AsSpan(8 + packet[6]).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("Kein INGLORIOUS-Konfigurationspaket.");
        var output = new byte[33]; packet.CopyTo(output, 1);
        await _stream.WriteAsync(output, cancellationToken);
        // HID transfers are report-based: reject short reports instead of combining unrelated packets.
        var input = new byte[33];
        var count = await _stream.ReadAsync(input, cancellationToken);
        if (count != 33 || input[0] != 0) throw new IOException("Unvollständiger Raw-HID-Bericht.");
        return input.AsSpan(1).ToArray();
    }
    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size; public Guid Guid; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoData { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct Caps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    private sealed class DeviceSet : SafeHandleMinusOneIsInvalid
    {
        private DeviceSet() : base(true) { }
        protected override bool ReleaseHandle() => Native.SetupDiDestroyDeviceInfoList(handle);
    }
    private static class Native
    {
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern DeviceSet SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr window, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(DeviceSet set, IntPtr deviceInfo, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(DeviceSet set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr deviceInfo);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(DeviceSet set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceInfoData deviceInfo);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiOpenDeviceInterfaceW(DeviceSet set, string path, uint flags, ref InterfaceData data);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLength, uint flags);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetManufacturerString(SafeFileHandle handle, IntPtr buffer, int bytes);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, IntPtr buffer, int bytes);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    }
}
