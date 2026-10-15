using System.Text.Json;
using InGlorious.Core;

internal static partial class Program
{
    private static void CheckPortableUsb()
    {
        // Simulate a first launch with an OS-assigned user data folder that does not
        // exist. No actual profile or environment variable is changed by this fixture.
        string missingUserFolder = Path.Combine(Path.GetTempPath(), "inglorious-missing-user-data-" + Guid.NewGuid().ToString("N"), "data");
        Check(!Directory.Exists(missingUserFolder), "user data fixture starts nonexistent");
        bool requestedUnverifiedFolder = false;
        string userRoot = Paths.ResolveUserDataRoot((folder, option) =>
        {
            requestedUnverifiedFolder = folder == Environment.SpecialFolder.LocalApplicationData && option == Environment.SpecialFolderOption.DoNotVerify;
            return option == Environment.SpecialFolderOption.DoNotVerify ? missingUserFolder : "";
        });
        Check(requestedUnverifiedFolder, "first launch resolves OS user path without requiring an existing directory");
        Check(Path.IsPathFullyQualified(userRoot) && userRoot == Path.Combine(missingUserFolder, "INGLORIOUS Advanced"), "missing user directory never falls back to current directory");
        Check(!Directory.Exists(missingUserFolder), "path resolution does not create user directories or bypass later filesystem checks");
        foreach (string missingOrRelative in new[] { "", " ", "data", "./data", "../data" })
            Reject(() => Paths.ResolveUserDataRoot((_, _) => missingOrRelative), "unavailable or relative OS user path is rejected");
        Check(Path.IsPathFullyQualified(DeviceSetupService.RecoveryDirectory), "recovery archive path is always absolute");
        foreach (byte bus in new byte[] { 0, 1, 42, 255 })
        foreach (string chain in new[] { "1", "2.7", "1.2.3.4.5.6.7.255" })
        {
            string location = PlatformUsbIdentity.Location(bus, chain);
            Check(PlatformUsbIdentity.IsCanonicalLocation(location), "portable port encodes complete bus and topology");
            Check(DeviceSetupService.SamePort([location], [location]), "same physical portable port");
            Check(DeviceSetupService.UsbPortMatches([location], chain, bus), "native adapter selection matches bus and full port chain");
            Check(!DeviceSetupService.UsbPortMatches([location], chain), "portable port requires the adapter bus");
            Check(!DeviceSetupService.UsbPortMatches([location], chain, (byte)(bus ^ 1)), "different host bus rejected even with same port chain");
            Check(!DeviceSetupService.SamePort([location], [PlatformUsbIdentity.Location((byte)(bus ^ 1), chain)]), "cross-controller collision rejected");
            Check(DeviceSetupService.HasUsableUsbTransport(new("fixture", 0x342D, 0xDFA0, "", "libusb", [location])), "verified portable metadata uses native probe gate");
            Check(!DeviceSetupService.HasUsableUsbTransport(new("fixture", 0x342D, 0xDFA0, "", "WinUSB", [location])), "Windows driver cannot satisfy portable gate");
        }
        foreach (string bad in new[] { "", "0", "01", "1.0", "1.256", "1..2", "1.-2", "1.2.3.4.5.6.7.8.9", " 1", "1\n", "1/2" })
            Reject(() => PlatformUsbIdentity.Location(1, bad), "invalid portable port rejected");
        foreach (string bad in new[] { "USBPORT(256)#USB(1)", "USBPORT(01)#USB(1)", "USBPORT(1)#USB(0)", "USBPORT(1)#USB(01)", "USBPORT(1)", "usbport(1)#USB(1)", "USBPORT(1)#USB(1)#other(2)" })
        {
            Check(!PlatformUsbIdentity.IsCanonicalLocation(bad), "noncanonical portable port rejected");
            Check(!DeviceSetupService.SamePort([bad], [bad]), "malformed portable port cannot self-match");
        }
        var first = new { bus = 1, address = 2, vendor = 0x320F, product = 0x5088, portPath = "2.3" };
        var second = first with { address = 3, portPath = "2.4" };
        JsonElement Inventory(params object[] nodes) => JsonSerializer.SerializeToElement(new { schema = 1, operation = "discover", status = "ok", devices = nodes });
        var scanned = PosixDeviceSetupEnvironment.ParseInventory(Inventory(first, second));
        Check(scanned.Count == 2 && scanned[0].InstanceId != scanned[1].InstanceId && !DeviceSetupService.SamePort(scanned[0].LocationPaths, scanned[1].LocationPaths), "two identical models remain distinct physical devices");
        var reattached = PosixDeviceSetupEnvironment.ParseInventory(Inventory(first with { address = 4 }))[0];
        Check(scanned[0].InstanceId != reattached.InstanceId && DeviceSetupService.SamePort(scanned[0].LocationPaths, reattached.LocationPaths), "USB address change preserves physical reconnect port");
        Reject(() => PosixDeviceSetupEnvironment.ParseInventory(Inventory(first, first)), "duplicate inventory rejected");
        Reject(() => PosixDeviceSetupEnvironment.ParseInventory(Inventory(first, first with { address = 4 })), "two addresses on one physical port rejected");
        Reject(() => PosixDeviceSetupEnvironment.ParseInventory(Inventory(first with { address = 0 })), "unaddressed device rejected");
        Reject(() => PosixDeviceSetupEnvironment.ParseInventory(Inventory(first with { vendor = 0xFFFF })), "unexpected vendor rejected");
        Reject(() => PosixDeviceSetupEnvironment.ParseInventory(Inventory(first with { portPath = "1.256" })), "invalid discovered topology rejected");
        Reject(() => PosixDeviceSetupEnvironment.ParseInventory(JsonSerializer.SerializeToElement(new { schema = 2, operation = "discover", status = "ok", devices = new[] { first } })), "unknown discovery schema rejected");
        Reject(() => PosixDeviceSetupEnvironment.ParseInventory(JsonSerializer.SerializeToElement(new { schema = 1, operation = "discover", status = "failed", devices = new[] { first } })), "unsuccessful metadata never treated as disconnected");
        Check(PlatformUsbIdentity.LocationsForHidPath("", 0xFEED, 1).Count == 0, "empty HID identity cannot bind");
        Check(PlatformUsbIdentity.LocationsForHidPath("bad\0path", 0xFEED, 1).Count == 0, "invalid HID identity rejected without device access");
    }

    private static async Task CheckPortableRoundTrips()
    {
        foreach (var board in RoundTripBoards)
        {
            using var fixture = new SetupFixture(board.DeviceId);
            var adapter = fixture.Environment;
            var portable = new PortableFixtureEnvironment(adapter);
            var service = new DeviceSetupService(portable, Path.Combine(fixture.Root, "portable-archive"), Path.Combine(fixture.Root, "Firmware"));
            byte[] original = adapter.Current.ToArray();
            foreach (var operation in new[] { SetupOperation.Install, SetupOperation.Update, SetupOperation.Restore })
            {
                adapter.Nodes = [operation == SetupOperation.Install ? adapter.Normal : adapter.Runtime];
                var selected = (await service.ListDevicesAsync()).Single();
                var session = await service.BeginAsync(selected, operation);
                Check(session.Stage == SetupStage.Disconnect, "portable operation still requires observed disconnect");
                adapter.Nodes = [];
                Check((await service.ObserveAsync(session)).Stage == SetupStage.ConnectBootloader, "portable disconnect advances through same coordinator");
                adapter.Nodes = [adapter.Boot];
                Check((await service.ObserveAsync(session)).CanCapture, "portable physical port binds native selection");
                Check((await service.CaptureAsync(session)).Stage == SetupStage.ReadyToInstall, "portable double capture produces explicit confirmation stage");
                var result = operation switch
                {
                    SetupOperation.Install => await service.InstallAsync(session, true),
                    SetupOperation.Update => await service.UpdateAsync(session, true),
                    _ => await service.RestoreAsync(session, true)
                };
                Check(result.Stage == SetupStage.Reconnect, "portable verified write still requires reconnect");
                adapter.Nodes = [operation == SetupOperation.Restore ? adapter.Normal : adapter.Runtime];
                Check((await service.ObserveAsync(session)).Stage == SetupStage.Complete, "portable normal-mode port completes reconnect");
            }
            Check(adapter.Writes == 3 && adapter.Current.SequenceEqual(original), "portable all-model install/update/restore preserves original bytes");
        }
        using (var fixture = new SetupFixture("numpad"))
        {
            var portable = new PortableFixtureEnvironment(fixture.Environment) { Bus = 2 };
            var service = new DeviceSetupService(portable, Path.Combine(fixture.Root, "portable-archive"), Path.Combine(fixture.Root, "Firmware"));
            var session = await service.BeginAsync((await service.ListDevicesAsync()).Single());
            fixture.Environment.Nodes = []; await service.ObserveAsync(session);
            fixture.Environment.Nodes = [fixture.Environment.Boot];
            Check((await service.ObserveAsync(session)).Stage == SetupStage.Blocked && fixture.Environment.Writes == 0,
                "adapter bus mismatch blocks despite matching USB chain");
        }
        if (!OperatingSystem.IsWindows())
        {
            string parent = Path.GetTempPath();
            Reject(() => DeviceSetupService.ContainedFile(Path.Combine(parent, "CaseSensitive"), "../casesensitive/escape.bin"), "POSIX case-only sibling escape rejected");
        }
    }

    private sealed class PortableFixtureEnvironment(FakeSetupEnvironment inner) : IDeviceSetupEnvironment
    {
        internal byte Bus = 1;
        public async Task<IReadOnlyList<SetupUsbNode>> ScanAsync() => (await inner.ScanAsync()).Select(node =>
        {
            byte address = node == inner.Normal ? (byte)2 : node == inner.Runtime ? (byte)3 : (byte)4;
            string path = PlatformUsbIdentity.Location(Bus, "1.2");
            return node with { InstanceId = $"USBINSTANCE({Bus},{address},{node.VendorId:X4},{node.ProductId:X4})", Service = "libusb", LocationPaths = [path] };
        }).ToArray();
        public Task<bool?> IsCoreRunningAsync() => inner.IsCoreRunningAsync();
        public Task<JsonElement> InvokeAdapterAsync(IReadOnlyDictionary<string, object?> request, string evidenceDirectory) => inner.InvokeAdapterAsync(request, evidenceDirectory);
    }
}
