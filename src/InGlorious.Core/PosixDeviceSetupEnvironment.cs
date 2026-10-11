using System.IO;
using System.Text.Json;

namespace InGlorious.Core;

/// <summary>libusb inventory and transport share the same bus/physical-port namespace.</summary>
internal sealed class PosixDeviceSetupEnvironment : IDeviceSetupEnvironment
{
    public async Task<IReadOnlyList<SetupUsbNode>> ScanAsync()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        return ParseInventory(await FirmwareAdapterProcess.DiscoverAsync());
    }

    internal static IReadOnlyList<SetupUsbNode> ParseInventory(JsonElement inventory)
    {
        if (inventory.GetProperty("schema").GetInt32() != 1 || inventory.GetProperty("operation").GetString() != "discover" || inventory.GetProperty("status").GetString() != "ok")
            throw new InvalidDataException("Invalid USB inventory.");
        var devices = inventory.GetProperty("devices").EnumerateArray().ToArray();
        if (devices.Length > 4096) throw new InvalidDataException("USB inventory exceeded its limit.");
        List<SetupUsbNode> nodes = [];
        foreach (var item in devices)
        {
            byte bus = item.GetProperty("bus").GetByte(), address = item.GetProperty("address").GetByte();
            ushort vid = item.GetProperty("vendor").GetUInt16(), pid = item.GetProperty("product").GetUInt16();
            if (address == 0 || vid is not (0x0483 or 0x320F or 0x342D or 0xFEED)) throw new InvalidDataException("Unexpected USB inventory item.");
            string location = PlatformUsbIdentity.Location(bus, item.GetProperty("portPath").GetString()!);
            // Address is part of the live instance, not the persistent physical-port identity.
            string instance = $"USBINSTANCE({bus},{address},{vid:X4},{pid:X4})";
            nodes.Add(new(instance, vid, pid, "", "libusb", [location]));
        }
        if (nodes.Select(n => n.InstanceId).Distinct(StringComparer.Ordinal).Count() != nodes.Count ||
            nodes.Select(n => n.LocationPaths.Single()).Distinct(StringComparer.Ordinal).Count() != nodes.Count)
            throw new InvalidDataException("Ambiguous USB inventory.");
        return nodes;
    }

    public Task<bool?> IsCoreRunningAsync() => Task.FromResult(DeviceSetupService.IsGloriousCoreRunning());
    public Task<JsonElement> InvokeAdapterAsync(IReadOnlyDictionary<string, object?> request, string evidenceDirectory) =>
        FirmwareAdapterProcess.InvokeAsync(request, evidenceDirectory);
}
