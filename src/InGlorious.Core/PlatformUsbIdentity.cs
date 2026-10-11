using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace InGlorious.Core;

/// <summary>Maps one live HID endpoint to its physical USB port, never by model or serial alone.</summary>
public static class PlatformUsbIdentity
{
    internal static string Location(byte bus, string portPath)
    {
        if (string.IsNullOrEmpty(portPath) || portPath.Length > 31) throw new InvalidDataException("Invalid physical USB port.");
        string[] parts = portPath.Split('.');
        if (parts.Length is < 1 or > 8 || parts.Any(p => !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out byte value) || value == 0 || p != value.ToString(CultureInfo.InvariantCulture)))
            throw new InvalidDataException("Invalid physical USB port.");
        return "USBPORT(" + bus.ToString(CultureInfo.InvariantCulture) + ")" + string.Concat(parts.Select(p => "#USB(" + p + ")"));
    }

    internal static bool IsCanonicalLocation(string value)
    {
        var match = Regex.Match(value, @"\AUSBPORT\(([0-9]{1,3})\)((?:#USB\([0-9]{1,3}\)){1,8})\z", RegexOptions.CultureInvariant);
        if (!match.Success || !byte.TryParse(match.Groups[1].Value, out byte bus)) return false;
        try { return value == Location(bus, string.Join('.', Regex.Matches(value, @"#USB\((\d+)\)").Select(m => m.Groups[1].Value))); }
        catch (InvalidDataException) { return false; }
    }

    public static IReadOnlyList<string> LocationsForHidPath(string path, ushort expectedVendor, ushort expectedProduct)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0')) return [];
        try
        {
            if (OperatingSystem.IsLinux()) return LinuxLocation(path, expectedVendor, expectedProduct) is { } linux ? [linux] : [];
            if (OperatingSystem.IsMacOS()) return MacLocation(path, expectedVendor, expectedProduct) is { } mac ? [mac] : [];
            if (OperatingSystem.IsWindows()) return WindowsLocations(path, expectedVendor, expectedProduct);
            return [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
        { return []; }
    }

    private static string? LinuxLocation(string path, ushort vid, ushort pid)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith("/sys/devices/", StringComparison.Ordinal) || !Directory.Exists(full)) return null;
        for (DirectoryInfo? item = new(full); item is not null && item.FullName.StartsWith("/sys/devices/", StringComparison.Ordinal); item = item.Parent)
        {
            // HidSharp exposes udev's canonical syspath. Links cannot introduce another tree.
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            if (!File.Exists(Path.Combine(item.FullName, "idVendor"))) continue;
            string Read(string name)
            {
                using var reader = File.OpenText(Path.Combine(item.FullName, name));
                char[] buffer = new char[257]; int count = reader.ReadBlock(buffer, 0, buffer.Length);
                if (count == buffer.Length) throw new InvalidDataException("Unexpected USB sysfs value.");
                return new string(buffer, 0, count).Trim();
            }
            if (ushort.Parse(Read("idVendor"), NumberStyles.HexNumber, CultureInfo.InvariantCulture) != vid ||
                ushort.Parse(Read("idProduct"), NumberStyles.HexNumber, CultureInfo.InvariantCulture) != pid) return null;
            return Location(byte.Parse(Read("busnum"), CultureInfo.InvariantCulture), Read("devpath"));
        }
        return null;
    }

    private static string? MacLocation(string path, ushort vid, ushort pid)
    {
        if (!path.StartsWith("IOService:/", StringComparison.Ordinal)) return null;
        uint node = Mac.IORegistryEntryFromPath(0, path);
        if (node == 0) return null;
        try
        {
            // Resolve the actual HID service's first physical USB ancestor.
            for (int depth = 0; depth < 32; depth++)
            {
                if (Mac.IOObjectConformsTo(node, "IOUSBHostDevice") || Mac.IOObjectConformsTo(node, "IOUSBDevice"))
                {
                    if (MacNumber(node, "idVendor") != vid || MacNumber(node, "idProduct") != pid || MacNumber(node, "locationID") is not { } location || location is < int.MinValue or > uint.MaxValue) return null;
                    byte bus = (byte)(unchecked((uint)location) >> 24);
                    List<byte> ports = [];
                    uint physical = node; bool ownsPhysical = false;
                    try
                    {
                        // Same IOUSB parent/PortNum hierarchy used by libusb's Darwin backend.
                        for (int hops = 0; hops < 8; hops++)
                        {
                            byte? port = MacPort(physical);
                            if (port is null) return null;
                            if (port == 0)
                            {
                                if (ports.Count == 0) return null;
                                ports.Reverse(); return Location(bus, string.Join('.', ports));
                            }
                            ports.Add(port.Value);
                            uint parent = physical; bool found = false, reachedRoot = false;
                            for (int ascend = 0; ascend < 32; ascend++)
                            {
                                if (Mac.IORegistryEntryGetParentEntry(parent, "IOUSB", out uint next) != 0 || next == 0) { reachedRoot = true; break; }
                                if (parent != physical) Mac.IOObjectRelease(parent);
                                parent = next;
                                if (MacNumber(parent, "sessionID") is not null) { found = true; break; }
                            }
                            if (!found)
                            {
                                if (parent != physical) Mac.IOObjectRelease(parent);
                                if (!reachedRoot) return null;
                                ports.Reverse();
                                return Location(bus, string.Join('.', ports));
                            }
                            if (ownsPhysical) Mac.IOObjectRelease(physical);
                            physical = parent; ownsPhysical = true;
                        }
                        return null;
                    }
                    finally { if (ownsPhysical) Mac.IOObjectRelease(physical); }
                }
                if (Mac.IORegistryEntryGetParentEntry(node, "IOService", out uint parentNode) != 0 || parentNode == 0) return null;
                Mac.IOObjectRelease(node); node = parentNode;
            }
            return null;
        }
        finally { Mac.IOObjectRelease(node); }
    }

    private static byte? MacPort(uint node)
    {
        if (MacNumber(node, "PortNum") is >= 0 and <= 255 and var value) return (byte)value;
        if (Mac.IORegistryEntryGetParentEntry(node, "IOService", out uint parent) != 0 || parent == 0) return null;
        try
        {
            nint data = MacProperty(parent, "port");
            if (data == 0) return null;
            try { return Mac.CFGetTypeID(data) == Mac.CFDataGetTypeID() && Mac.CFDataGetLength(data) == 1 ? Marshal.ReadByte(Mac.CFDataGetBytePtr(data)) : null; }
            finally { Mac.CFRelease(data); }
        }
        finally { Mac.IOObjectRelease(parent); }
    }
    private static long? MacNumber(uint node, string key)
    {
        nint value = MacProperty(node, key);
        if (value == 0) return null;
        try { return Mac.CFGetTypeID(value) == Mac.CFNumberGetTypeID() && Mac.CFNumberGetValue(value, 4, out long number) ? number : null; }
        finally { Mac.CFRelease(value); }
    }
    private static nint MacProperty(uint node, string key)
    {
        nint name = Mac.CFStringCreateWithCString(0, key, 0x08000100);
        if (name == 0) return 0;
        try { return Mac.IORegistryEntryCreateCFProperty(node, name, 0, 0); }
        finally { Mac.CFRelease(name); }
    }

    private static IReadOnlyList<string> WindowsLocations(string path, ushort vid, ushort pid)
    {
        // Resolve the device-interface's devnode through SetupAPI, not a guessed instance suffix.
        Guid guid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
        nint set = Win.SetupDiGetClassDevsW(ref guid, null, 0, 0x12);
        if (set == -1) return [];
        try
        {
            var iface = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
            if (!Win.SetupDiOpenDeviceInterfaceW(set, path, 0, ref iface)) return [];
            Win.SetupDiGetDeviceInterfaceDetailW(set, ref iface, 0, 0, out uint size, 0);
            if (size is < 6 or > 1048576) return [];
            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                var device = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
                if (!Win.SetupDiGetDeviceInterfaceDetailW(set, ref iface, buffer, size, out _, ref device)) return [];
                uint node = device.DevInst; HashSet<uint> seen = [];
                for (int depth = 0; depth < 8 && seen.Add(node); depth++)
                {
                    var id = new StringBuilder(4097);
                    if (Win.CM_Get_Device_IDW(node, id, id.Capacity, 0) != 0) return [];
                    string instance = id.ToString();
                    if (Regex.IsMatch(instance, @"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\[^\\\r\n\x00]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        if (!instance.StartsWith($"USB\\VID_{vid:X4}&PID_{pid:X4}\\", StringComparison.OrdinalIgnoreCase)) return [];
                        var key = new PropertyKey { FormatId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 37 };
                        byte[] bytes = new byte[65536]; uint length = (uint)bytes.Length;
                        if (Win.CM_Get_DevNode_PropertyW(node, ref key, out uint type, bytes, ref length, 0) != 0 || type != 0x2012 || length < 4 || length > bytes.Length || length % 2 != 0) return [];
                        return Encoding.Unicode.GetString(bytes, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    }
                    if (Win.CM_Get_Parent(out node, node, 0) != 0) return [];
                }
                return [];
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Win.SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size; public Guid Guid; public uint Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoData { public int Size; public Guid Guid; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid FormatId; public uint PropertyId; }
    private static class Win
    {
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] internal static extern nint SetupDiGetClassDevsW(ref Guid guid, string? enumerator, nint window, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiOpenDeviceInterfaceW(nint set, string path, uint flags, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref InterfaceData data, nint detail, uint size, out uint required, nint deviceInfo);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref InterfaceData data, nint detail, uint size, out uint required, ref DeviceInfoData deviceInfo);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_Device_IDW(uint node, StringBuilder value, int capacity, uint flags);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type, [Out] byte[] bytes, ref uint length, uint flags);
    }
    private static class Mac
    {
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit", CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(IOKit)] internal static extern uint IORegistryEntryFromPath(uint mainPort, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(IOKit)] internal static extern int IORegistryEntryGetParentEntry(uint node, [MarshalAs(UnmanagedType.LPUTF8Str)] string plane, out uint parent);
        [DllImport(IOKit)] internal static extern int IOObjectRelease(uint node);
        [DllImport(IOKit)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool IOObjectConformsTo(uint node, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(IOKit)] internal static extern nint IORegistryEntryCreateCFProperty(uint node, nint key, nint allocator, uint options);
        [DllImport(CoreFoundation)] internal static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
        [DllImport(CoreFoundation)] internal static extern nuint CFGetTypeID(nint value);
        [DllImport(CoreFoundation)] internal static extern nuint CFNumberGetTypeID();
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool CFNumberGetValue(nint number, nint type, out long value);
        [DllImport(CoreFoundation)] internal static extern nuint CFDataGetTypeID();
        [DllImport(CoreFoundation)] internal static extern nint CFDataGetLength(nint data);
        [DllImport(CoreFoundation)] internal static extern nint CFDataGetBytePtr(nint data);
        [DllImport(CoreFoundation)] internal static extern void CFRelease(nint value);
    }
}
