using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace InGlorious.FirmwareAdapter;

internal sealed record DeviceIdentity(byte Bus, byte Address, ushort Vendor, ushort Product, ushort DeviceVersion, string PortPath, string Serial);
internal sealed record NativeLibraryEvidence(string Path, long Bytes, string Sha256);

internal sealed class NativeUsb : IDisposable
{
    private nint context;
    private nint devices;
    private FileStream? libraryLock;
    private readonly List<(nint Pointer, DeviceDescriptor Descriptor, DeviceIdentity Identity)> matching = [];
    internal IReadOnlyList<DeviceIdentity> Devices => matching.Select(x => x.Identity).ToArray();
    internal NativeLibraryEvidence Library { get; }
    internal const string DllSha256 = "7C4531E5369A763B1DAE7AEDD9152406587D52F65C3935479CCFC9C84F983AAC";

    internal NativeUsb(bool inventoryOnly = false)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("A 64-bit USB platform is required.");
        ValidateNativeLayouts();
        Library = VerifyBundledLibrary();
        libraryLock = Files.Lock(Library.Path, Library.Bytes, Library.Sha256).Stream;
        try
        {
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) => name == "inglorious_libusb" ? NativeLibrary.Load(Library.Path) : 0);
            Check(Native.Init(out context), "libusb initialization");
            long count = Native.GetDeviceList(context, out devices).ToInt64();
            if (count < 0 || count > 4096) throw new IOException($"libusb enumeration error {count}.");
            for (int i = 0; i < count; i++)
            {
                nint device = Marshal.ReadIntPtr(devices, checked(i * IntPtr.Size));
                if (Native.GetDescriptor(device, out var descriptor) != 0) throw new IOException("USB descriptor could not be read.");
                bool bootloader = (descriptor.Vendor, descriptor.Product) is (0x342D, 0xDFA0) or (0x0483, 0xDF11);
                if (!bootloader && (!inventoryOnly || descriptor.Vendor is not (0x320F or 0x342D or 0xFEED))) continue;
                byte[] ports = new byte[8];
                int portCount = Native.GetPortNumbers(device, ports, ports.Length);
                if (portCount < 1 || portCount > ports.Length || ports.Take(portCount).Any(port => port == 0))
                    throw new IOException("USB physical port could not be identified.");
                string serial = "";
                // Inventory only uses descriptors provided by the OS; it never opens a device.
                if (!inventoryOnly && descriptor.SerialIndex != 0)
                {
                    Check(Native.Open(device, out var temporaryHandle), "reading USB serial descriptor");
                    try
                    {
                        byte[] chars = new byte[256];
                        int size = Native.GetString(temporaryHandle, descriptor.SerialIndex, chars, chars.Length);
                        Check(size, "reading USB serial descriptor"); serial = Encoding.ASCII.GetString(chars, 0, size);
                    }
                    finally { Native.Close(temporaryHandle); }
                }
                var identity = new DeviceIdentity(Native.GetBus(device), Native.GetAddress(device), descriptor.Vendor, descriptor.Product, descriptor.DeviceVersion, string.Join('.', ports.Take(portCount)), serial);
                matching.Add((device, descriptor, identity));
            }
            if (matching.Select(item => (item.Identity.Bus, item.Identity.PortPath)).Distinct().Count() != matching.Count)
                throw new IOException("USB physical port inventory is ambiguous.");
        }
        catch { Dispose(); throw; }
    }

    internal WbConnection OpenSelected(Selection selection)
    {
        var candidates = matching.Where(x => x.Identity.Vendor == selection.Vid && x.Identity.Product == selection.Pid && x.Identity.Bus == selection.Bus && x.Identity.PortPath == selection.PortPath && (selection.Serial == null || x.Identity.Serial == selection.Serial)).ToArray();
        if (selection.Vid != 0x342D || selection.Pid != 0xDFA0 || candidates.Length != 1) throw new IOException("Exactly one selected WB32 physical port is required; no device opened.");
        var item = candidates[0];
        if (item.Descriptor.NumConfigurations != 1) throw new IOException("Unexpected USB configuration count; no device opened.");
        ValidateInterfaces(item.Pointer);
        return new WbConnection(item.Pointer, item.Descriptor, item.Identity);
    }

    internal static NativeLibraryEvidence VerifyBundledLibrary()
    {
        return NativeToolCatalog.Verify(NativeToolCatalog.LibraryName);
    }

    internal static void ValidateNativeLayouts()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf<DeviceDescriptor>() != 18 || Marshal.SizeOf<ConfigDescriptor>() != 40
            || Marshal.SizeOf<UsbInterface>() != 16 || Marshal.SizeOf<InterfaceDescriptor>() != 40 || Marshal.SizeOf<EndpointDescriptor>() != 32
            || Marshal.OffsetOf<ConfigDescriptor>(nameof(ConfigDescriptor.Interfaces)).ToInt32() != 16
            || Marshal.OffsetOf<InterfaceDescriptor>(nameof(InterfaceDescriptor.Endpoints)).ToInt32() != 16)
            throw new PlatformNotSupportedException("Native USB descriptor layout is not the required 64-bit ABI.");
    }

    private static void ValidateInterfaces(nint device)
    {
        Check(Native.GetConfig(device, 0, out var pointer), "reading configuration descriptor");
        try
        {
            var config = Marshal.PtrToStructure<ConfigDescriptor>(pointer);
            if (config.Value != 1 || config.NumInterfaces != 1) throw new IOException("Unexpected configuration/interface count.");
            var iface = Marshal.PtrToStructure<UsbInterface>(config.Interfaces);
            if (iface.NumAltSettings != 1) throw new IOException("Unexpected alternate settings.");
            var alt = Marshal.PtrToStructure<InterfaceDescriptor>(iface.AltSettings);
            if (alt.Number != 0 || alt.Alternate != 0 || alt.Class != 255 || alt.Subclass != 255 || alt.Protocol != 0 || alt.NumEndpoints != 2) throw new IOException("Unexpected WB32 interface descriptor.");
            var endpoints = new List<EndpointDescriptor>();
            for (int i = 0; i < 2; i++) endpoints.Add(Marshal.PtrToStructure<EndpointDescriptor>(alt.Endpoints + i * Marshal.SizeOf<EndpointDescriptor>()));
            if (!endpoints.Any(e => e.Address == 0x01 && (e.Attributes & 3) == 2 && e.MaxPacketSize == 64) || !endpoints.Any(e => e.Address == 0x81 && (e.Attributes & 3) == 2 && e.MaxPacketSize == 64)) throw new IOException("Expected 64-byte bulk endpoints 01/81 are absent.");
        }
        finally { Native.FreeConfig(pointer); }
    }

    public void Dispose() { if (devices != 0) Native.FreeDeviceList(devices, 1); if (context != 0) Native.Exit(context); devices = context = 0; libraryLock?.Dispose(); libraryLock = null; }
    internal static void Check(int result, string operation) { if (result < 0) throw new IOException($"{operation}: libusb error {result}. No driver installation or recovery will be attempted."); }

    internal sealed class WbConnection : IWbExchange, IDevice
    {
        private nint handle;
        private bool claimed;
        private bool writesEnabled;
        private WbInfo? initialInfo;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        internal DeviceIdentity Identity { get; }
        internal string Manufacturer { get; }
        internal string Product { get; }
        internal string Serial { get; }
        internal List<object> Transfers { get; } = [];
        public object[] Log => Transfers.ToArray();

        internal WbConnection(nint device, DeviceDescriptor descriptor, DeviceIdentity identity)
        {
            Identity = identity;
            Check(Native.Open(device, out handle), "opening WB32 ROM bootloader");
            try
            {
                Manufacturer = String(descriptor.ManufacturerIndex);
                Product = String(descriptor.ProductIndex);
                Serial = descriptor.SerialIndex == 0 ? "" : String(descriptor.SerialIndex);
                if (Manufacturer != "Westberry Tech." || Product != "WB Device in DFU Mode") throw new IOException("USB strings do not match the audited WB32 ROM bootloader.");
                Check(Native.Claim(handle, 0), "claiming existing bootloader driver");
                claimed = true;
            }
            catch { Dispose(); throw; }
        }

        private string String(byte index)
        {
            byte[] bytes = new byte[256];
            int length = Native.GetString(handle, index, bytes, bytes.Length);
            Check(length, "reading USB string");
            return Encoding.ASCII.GetString(bytes, 0, length);
        }

        public byte[] Exchange(byte[] request)
        {
            var started = DateTimeOffset.UtcNow;
            int sent = -1, received = -1;
            string? error = null;
            byte[] response = new byte[1024];
            try
            {
                if (elapsed.ElapsedMilliseconds >= 300000) throw new TimeoutException("OPERATION_TIMEOUT");
                WbProtocol.ValidatePacket(request, writesEnabled);
                Check(Native.Bulk(handle, 0x01, request, request.Length, out sent, 5000), "sending allowed read request");
                if (sent != request.Length) throw new IOException("Incomplete read request transfer.");
                Check(Native.Bulk(handle, 0x81, response, response.Length, out received, 5000), "receiving bootloader data");
                if (received < 0 || received > response.Length) throw new IOException("Invalid native transfer length.");
                return response[..received];
            }
            catch (Exception ex) { error = ex.Message; throw; }
            finally
            {
                Transfers.Add(new { utc = started, request = Convert.ToHexString(request), sent, received, status = received > 1 ? response[1] : (byte?)null, error });
            }
        }

        public Fingerprint Probe(Model model)
        {
            if (model.Transport != "wb32-dfu") throw new InvalidDataException("MODEL_TRANSPORT_MISMATCH");
            var info = WbProtocol.GetInfo(this);
            if (initialInfo != null && initialInfo != info) throw new InvalidDataException("WB_IDENTITY_CHANGED");
            initialInfo = info;
            if (model.FlashBytes != info.FlashBytes || model.SramBytes != info.SramBytes) throw new InvalidDataException("MODEL_GEOMETRY_MISMATCH");
            return new("wb32-dfu", Identity.Vendor, Identity.Product, Identity.Bus, Identity.PortPath,
                Serial, 1, 0, 0, Identity.DeviceVersion, info.ChipId, info.BootVersion, Model.FlashBase,
                info.FlashBytes, info.SramBytes, null, null, "transport-session");
        }
        public byte[] ReadAll(Model model)
        {
            _ = Probe(model); byte[] result = new byte[model.FlashBytes];
            for (int offset = 0; offset < result.Length; offset += 256)
                WbProtocol.Read(this, Model.FlashBase + (uint)offset, 256).CopyTo(result, offset);
            _ = Probe(model); return result;
        }
        public void WriteAll(Model model, byte[] image, Action beforeFirstWrite)
        {
            _ = Probe(model);
            if (image.Length != model.FlashBytes || image.Length != 131072 || writesEnabled) throw new InvalidDataException("WB_FULL_IMAGE_REQUIRED");
            beforeFirstWrite(); writesEnabled = true;
            try
            {
                WbProtocol.WriteImage(this, image);
            }
            finally { writesEnabled = false; }
        }

        public void Dispose() { if (handle == 0) return; if (claimed) Native.Release(handle, 0); Native.Close(handle); handle = 0; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct DeviceDescriptor
    { public byte Length, Type; public ushort Usb; public byte Class, Subclass, Protocol, MaxPacket; public ushort Vendor, Product, DeviceVersion; public byte ManufacturerIndex, ProductIndex, SerialIndex, NumConfigurations; }
    [StructLayout(LayoutKind.Sequential)] private struct ConfigDescriptor
    { public byte Length, Type; public ushort TotalLength; public byte NumInterfaces, Value, Index, Attributes, MaxPower; public nint Interfaces, Extra; public int ExtraLength; }
    [StructLayout(LayoutKind.Sequential)] private struct UsbInterface { public nint AltSettings; public int NumAltSettings; }
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceDescriptor
    { public byte Length, Type, Number, Alternate, NumEndpoints, Class, Subclass, Protocol, Index; public nint Endpoints, Extra; public int ExtraLength; }
    [StructLayout(LayoutKind.Sequential)] private struct EndpointDescriptor
    { public byte Length, Type, Address, Attributes; public ushort MaxPacketSize; public byte Interval, Refresh, SynchAddress; public nint Extra; public int ExtraLength; }

    private static class Native
    {
        private const string Dll = "inglorious_libusb";
        [DllImport(Dll, EntryPoint = "libusb_init", CallingConvention = CallingConvention.Cdecl)] internal static extern int Init(out nint context);
        [DllImport(Dll, EntryPoint = "libusb_exit", CallingConvention = CallingConvention.Cdecl)] internal static extern void Exit(nint context);
        [DllImport(Dll, EntryPoint = "libusb_get_device_list", CallingConvention = CallingConvention.Cdecl)] internal static extern nint GetDeviceList(nint context, out nint list);
        [DllImport(Dll, EntryPoint = "libusb_free_device_list", CallingConvention = CallingConvention.Cdecl)] internal static extern void FreeDeviceList(nint list, int unref);
        [DllImport(Dll, EntryPoint = "libusb_get_device_descriptor", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetDescriptor(nint device, out DeviceDescriptor descriptor);
        [DllImport(Dll, EntryPoint = "libusb_get_bus_number", CallingConvention = CallingConvention.Cdecl)] internal static extern byte GetBus(nint device);
        [DllImport(Dll, EntryPoint = "libusb_get_device_address", CallingConvention = CallingConvention.Cdecl)] internal static extern byte GetAddress(nint device);
        [DllImport(Dll, EntryPoint = "libusb_get_port_numbers", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetPortNumbers(nint device, [Out] byte[] ports, int length);
        [DllImport(Dll, EntryPoint = "libusb_get_config_descriptor", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetConfig(nint device, byte index, out nint config);
        [DllImport(Dll, EntryPoint = "libusb_free_config_descriptor", CallingConvention = CallingConvention.Cdecl)] internal static extern void FreeConfig(nint config);
        [DllImport(Dll, EntryPoint = "libusb_open", CallingConvention = CallingConvention.Cdecl)] internal static extern int Open(nint device, out nint handle);
        [DllImport(Dll, EntryPoint = "libusb_close", CallingConvention = CallingConvention.Cdecl)] internal static extern void Close(nint handle);
        [DllImport(Dll, EntryPoint = "libusb_get_string_descriptor_ascii", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetString(nint handle, byte index, [Out] byte[] data, int length);
        [DllImport(Dll, EntryPoint = "libusb_claim_interface", CallingConvention = CallingConvention.Cdecl)] internal static extern int Claim(nint handle, int index);
        [DllImport(Dll, EntryPoint = "libusb_release_interface", CallingConvention = CallingConvention.Cdecl)] internal static extern int Release(nint handle, int index);
        [DllImport(Dll, EntryPoint = "libusb_bulk_transfer", CallingConvention = CallingConvention.Cdecl)] internal static extern int Bulk(nint handle, byte endpoint, [In, Out] byte[] data, int length, out int transferred, uint timeout);
    }
}

