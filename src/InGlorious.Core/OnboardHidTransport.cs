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
    private static readonly object StringQueryGate = new();
    private readonly FileStream? _stream;
    private readonly IOnboardTransport? _shared;
    private OnboardHidTransport(IOnboardTransport shared) => _shared = shared;
    private OnboardHidTransport(SafeFileHandle handle) => _stream = new FileStream(handle, FileAccess.ReadWrite, 33, true);
    public static IReadOnlyList<OnboardDevice> FindDevices(bool allowEngineering = false)
    {
        try { return FindDevicesCore(allowEngineering); }
        catch (OnboardMetadataQueryException error)
        {
            if (!error.DiagnosticRecorded) OnboardMetadata.Record(null, OnboardMetadataStage.Enumeration, 1, OnboardMetadataOutcome.Failed, error);
            throw;
        }
    }
    private static IReadOnlyList<OnboardDevice> FindDevicesCore(bool allowEngineering)
    {
        var found = new List<OnboardDevice>();
        Native.HidD_GetHidGuid(out var guid);
        using var set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set.IsInvalid) throw MetadataError(OnboardMetadataOperation.InterfaceSet, Marshal.GetLastPInvokeError());
        for (uint i = 0; ; i++)
        {
            var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
            if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref item))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break;
                throw MetadataError(OnboardMetadataOperation.InterfaceEnumeration, error);
            }
            var size = ReadDetailSize(set, ref item);
            var buffer = Marshal.AllocHGlobal((int)size);
            string path;
            var deviceInfo = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                var success = Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, buffer, size, out _, ref deviceInfo);
                var error = Marshal.GetLastPInvokeError();
                OnboardMetadata.RequireWin32(success, OnboardMetadataOperation.InterfaceDetails, error);
                path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
            if (!IsAllowedPath(path, allowEngineering)) continue;
            // A failed query on an allowlisted endpoint must not silently hide a second matching keyboard.
            if (IdentifyConnectedWithMetadata(path, () => new NativeMetadataSession(path), allowEngineering) is { } device) found.Add(device);
        }
        return found;
    }

    public static bool IsAllowedPath(string path, bool allowEngineering = false) => BoardDescriptorRegistry.TryGetRuntimeHidPath(path, out _, allowEngineering);

    public static OnboardHidTransport Open(OnboardDevice device, bool allowEngineering = false)
    {
        return new(OpenWithMetadata(device, () => new NativeMetadataSession(device.Path), () => OpenNativeReports(device), allowEngineering));
    }

    internal static IOnboardTransport OpenWithMetadata(OnboardDevice device, Func<IOnboardMetadataSession> openMetadata,
        Func<IOnboardTransport> openReports, bool allowEngineering = false, Action? pause = null)
    {
        ValidateExpected(device, allowEngineering);
        return OnboardMetadata.Read(device.DeviceId, OnboardMetadataStage.Open,
            () => OpenWithMetadataOnce(device, openMetadata, openReports, allowEngineering), pause);
    }
    private static IOnboardTransport OpenWithMetadataOnce(OnboardDevice device, Func<IOnboardMetadataSession> openMetadata,
        Func<IOnboardTransport> openReports, bool allowEngineering)
    {
        // Keep the synchronous metadata handle alive across report-handle acquisition. HidD APIs perform
        // synchronous DeviceIoControl calls and must never receive the overlapped RAW-report handle.
        using var before = openMetadata();
        ValidateIdentity(before, device, allowEngineering);
        return OnboardTransportPool.Acquire(device.Path, device.DeviceId + "\n" + device.Name + "\n" + device.UsbInstanceId, () =>
        {
            var reports = openReports();
            try
            {
                // Reopen metadata separately after acquiring the report handle: no cached descriptor fallback.
                using var after = openMetadata();
                ValidateIdentity(after, device, allowEngineering);
                return reports;
            }
            catch (Exception failure)
            {
                // No request has been issued yet. Close the unused report handle before propagating rejection.
                try { reports.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch
                {
                    // Preserve the original rejection, but never retry while the unused report handle may still be alive.
                    if (failure is OnboardMetadataQueryException metadataFailure) metadataFailure.CleanupFailed = true;
                }
                throw;
            }
        });
    }
    private static void ValidateExpected(OnboardDevice device, bool allowEngineering)
    {
        if (!BoardDescriptorRegistry.TryGet(device.DeviceId, out var board) ||
            !BoardDescriptorRegistry.TryGetRuntimeHidPath(device.Path, out var pathBoard, allowEngineering) || board != pathBoard ||
            !IsPhysicalUsbRoot(device.UsbInstanceId))
            throw new InvalidDataException("Nur INGLORIOUS-Firmware wird zur Konfiguration geöffnet.");
    }
    internal static void ValidateIdentity(IOnboardMetadataReader metadata, OnboardDevice device, bool allowEngineering = false)
    {
        var current = Identify(metadata, device.Path, allowEngineering);
        if (current == null || current.DeviceId != device.DeviceId || current.Name != device.Name) throw new InvalidDataException("Die Geräteidentität oder HID-Schnittstelle hat sich geändert.");
        if (device.UsbInstanceId is not null && !string.Equals(device.UsbInstanceId, metadata.UsbRoot(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die USB-Zuordnung des Geräts hat sich geändert.");
    }
    internal static uint HandleFlags(OnboardHidHandleMode mode) => mode switch
    {
        OnboardHidHandleMode.Metadata => 0,
        OnboardHidHandleMode.Reports => 0x40000000, // FILE_FLAG_OVERLAPPED keeps report I/O cancellable.
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    private static OnboardHidTransport OpenNativeReports(OnboardDevice device)
    {
        var handle = Native.CreateFileW(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, HandleFlags(OnboardHidHandleMode.Reports), IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try { return new(handle); }
        catch { handle.Dispose(); throw; }
    }
    internal static bool IsPhysicalUsbRoot(string? value) => value is { Length: > 0 and <= 4096 } &&
        Regex.IsMatch(value, @"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&[A-Z0-9_]+)*\\[^\\\r\n\x00]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !value[..value.LastIndexOf('\\')].Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    internal static string RequirePhysicalUsbRoot(string? value) => IsPhysicalUsbRoot(value) ? value! :
        throw new InvalidDataException("Die physische USB-Zuordnung des Geräts konnte nicht eindeutig geprüft werden.");

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
    private static string ReadUsbRoot(uint start) => RequirePhysicalUsbRoot(ResolveUsbRoot(start,
        node =>
        {
            var value = new StringBuilder(4097);
            var status = Native.CM_Get_Device_IDW(node, value, value.Capacity, 0);
            OnboardMetadata.RequireConfigRet(status, OnboardMetadataOperation.DeviceInstanceId);
            return value.ToString();
        },
        node =>
        {
            var status = Native.CM_Get_Parent(out var parent, node, 0);
            OnboardMetadata.RequireConfigRet(status, OnboardMetadataOperation.DeviceParent);
            return parent;
        }));

    private static string? ReadUsbRootForInterface(string path)
    {
        Native.HidD_GetHidGuid(out var guid);
        using var set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set.IsInvalid) throw MetadataError(OnboardMetadataOperation.InterfaceSet, Marshal.GetLastPInvokeError());
        var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
        var opened = Native.SetupDiOpenDeviceInterfaceW(set, path, 0, ref item);
        var openError = Marshal.GetLastPInvokeError();
        OnboardMetadata.RequireWin32(opened, OnboardMetadataOperation.InterfaceOpen, openError);
        var size = ReadDetailSize(set, ref item);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            var info = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
            var success = Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, buffer, size, out _, ref info);
            var error = Marshal.GetLastPInvokeError();
            OnboardMetadata.RequireWin32(success, OnboardMetadataOperation.InterfaceDetails, error);
            return ReadUsbRoot(info.DevInst);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static uint ReadDetailSize(DeviceSet set, ref InterfaceData item)
    {
        var success = Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, IntPtr.Zero, 0, out var size, IntPtr.Zero);
        var error = Marshal.GetLastPInvokeError();
        // ERROR_INSUFFICIENT_BUFFER is the documented size-query response, not a failed metadata read.
        if (!success && error != 122) throw MetadataError(OnboardMetadataOperation.InterfaceDetails, error);
        if (size is < 6 or > 1048576) throw new InvalidDataException("Ungültiger HID-Gerätepfad.");
        return size;
    }
    internal static OnboardDevice? Identify(IOnboardMetadataReader metadata, string path, bool allowEngineering = false)
    {
        var attributes = metadata.Attributes();
        if (!BoardDescriptorRegistry.TryGetRuntimeUsb(attributes.VendorId, attributes.ProductId, out var board) ||
            (board.IsEngineeringOnly && !EngineeringConfigurationPolicy.IsEnabled(board.DeviceId, allowEngineering))) return null;
        var id = board.DeviceId;
        var name = board.RuntimeName;
        // Keyboard/consumer collections share VID/PID with RAW-HID. Positively rule them out before
        // issuing USB string requests; every accepted RAW collection still needs both exact names.
        var caps = metadata.Capabilities();
        if (caps.UsagePage != 0xFF60 || caps.Usage != 0x0061 || caps.InputBytes != 33 || caps.OutputBytes != 33) return null;
        if (!OnboardIdentity.MatchesNames(id, metadata.Manufacturer(), metadata.Product())) return null;
        return new(id, name, path);
    }

    internal static OnboardDevice? IdentifyConnectedWithMetadata(string path, Func<IOnboardMetadataSession> openMetadata,
        bool allowEngineering = false, Action? pause = null)
    {
        if (!BoardDescriptorRegistry.TryGetRuntimeHidPath(path, out var board, allowEngineering)) return null;
        return OnboardMetadata.Read(board.DeviceId, OnboardMetadataStage.Enumeration, () =>
        {
            using var metadata = openMetadata();
            // Resolve ancestry from the exact interface anew; a cached enumeration devnode may be stale after hotplug.
            var device = IdentifyConnected(metadata, path, metadata.UsbRoot, allowEngineering);
            if (device is not null && device.DeviceId != board.DeviceId)
                throw new InvalidDataException("The device identity does not match its USB interface.");
            return device;
        }, pause);
    }

    internal static OnboardDevice? IdentifyConnected(IOnboardMetadataReader metadata, string path, Func<string?> readUsbRoot, bool allowEngineering = false)
    {
        var device = Identify(metadata, path, allowEngineering);
        return device is null ? null : device with { UsbInstanceId = RequirePhysicalUsbRoot(readUsbRoot()) };
    }

    private static OnboardMetadataQueryException MetadataError(OnboardMetadataOperation operation, int error) =>
        new(operation, OnboardNativeErrorDomain.Win32, unchecked((uint)error));

    private sealed class NativeMetadataSession : IOnboardMetadataSession
    {
        private readonly string _path;
        private readonly SafeFileHandle _handle;
        public NativeMetadataSession(string path)
        {
            _path = path;
            _handle = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, HandleFlags(OnboardHidHandleMode.Metadata), IntPtr.Zero);
            var error = Marshal.GetLastPInvokeError();
            if (_handle.IsInvalid) { _handle.Dispose(); throw MetadataError(OnboardMetadataOperation.OpenMetadata, error); }
        }
        public OnboardHidAttributes Attributes()
        {
            var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>() };
            var success = Native.HidD_GetAttributes(_handle, ref attributes);
            var error = Marshal.GetLastPInvokeError();
            OnboardMetadata.RequireWin32(success, OnboardMetadataOperation.Attributes, error);
            return new(attributes.VendorId, attributes.ProductId);
        }
        public string Manufacturer() => ReadString(product: false);
        public string Product() => ReadString(product: true);
        public string? UsbRoot() => ReadUsbRootForInterface(_path);
        public OnboardHidCapabilities Capabilities()
        {
            var success = Native.HidD_GetPreparsedData(_handle, out var data);
            var error = Marshal.GetLastPInvokeError();
            OnboardMetadata.RequireWin32(success, OnboardMetadataOperation.ReportDescriptor, error);
            try
            {
                var status = Native.HidP_GetCaps(data, out var caps);
                OnboardMetadata.RequireCapabilitiesStatus(status);
                return new(caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength);
            }
            finally { Native.HidD_FreePreparsedData(data); }
        }
        private string ReadString(bool product)
        {
            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                Marshal.Copy(new byte[512], 0, buffer, 512);
                bool success; int error;
                // Restrict serialization to synchronous descriptor requests. Never hold this lock over pool access or report I/O.
                lock (StringQueryGate)
                {
                    success = product ? Native.HidD_GetProductString(_handle, buffer, 512) : Native.HidD_GetManufacturerString(_handle, buffer, 512);
                    error = Marshal.GetLastPInvokeError();
                }
                OnboardMetadata.RequireWin32(success, product ? OnboardMetadataOperation.Product : OnboardMetadataOperation.Manufacturer, error);
                return Marshal.PtrToStringUni(buffer, 256)?.Split('\0')[0] ?? "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Dispose() => _handle.Dispose();
    }
    public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_shared is not null) return await _shared.ExchangeAsync(packet, cancellationToken).ConfigureAwait(false);
        OnboardProtocol.ValidateRequest(packet);
        var output = new byte[33]; packet.CopyTo(output, 1);
        await _stream!.WriteAsync(output, cancellationToken).ConfigureAwait(false);
        // HID transfers are report-based: reject short reports instead of combining unrelated packets.
        var input = new byte[33];
        var count = await _stream.ReadAsync(input, cancellationToken).ConfigureAwait(false);
        if (count != 33 || input[0] != 0) throw new IOException("Unvollständiger Raw-HID-Bericht.");
        return input.AsSpan(1).ToArray();
    }
    public ValueTask DisposeAsync() => _shared?.DisposeAsync() ?? _stream!.DisposeAsync();

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
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetManufacturerString(SafeFileHandle handle, IntPtr buffer, int bytes);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, IntPtr buffer, int bytes);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    }
}
