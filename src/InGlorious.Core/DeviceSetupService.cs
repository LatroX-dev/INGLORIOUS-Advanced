using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("InGlorious.SetupChecks")]

namespace InGlorious.Core;

public enum SetupStage { Disconnect, ConnectBootloader, ReadyToBackup, ReadyToInstall, Reconnect, Complete, Blocked }
public enum SetupOperation { Install, Restore, Update }
public sealed record SetupDevice(string InstanceId, string Name, IReadOnlyList<string> CandidateDeviceIds,
    ushort VendorId, ushort ProductId, IReadOnlyList<string> LocationPaths, bool IsRuntime)
{
    public bool HasFirmwareCandidate => CandidateDeviceIds.Count > 0;
    public bool IsExperimental => CandidateDeviceIds.Any(id => BoardDescriptorRegistry.Get(id).IsExperimental);
    public override string ToString() => Name;
}
public sealed record SetupProgress(SetupStage Stage, string Message, string? DeviceId = null)
{
    public bool CanCapture => Stage == SetupStage.ReadyToBackup;
    public bool CanInstall => Stage == SetupStage.ReadyToInstall;
    public bool CanRestore => Stage == SetupStage.ReadyToInstall;
    public bool CanUpdate => Stage == SetupStage.ReadyToInstall;
}
public sealed class SetupSession
{
    internal SetupSession(SetupDevice device, SetupOperation operation, string directory)
    { Device = device; Operation = operation; Directory = directory; }
    public SetupDevice Device { get; }
    public SetupOperation Operation { get; }
    public string? DeviceId { get; internal set; }
    public SetupStage Stage { get; internal set; } = SetupStage.Disconnect;
    public string Directory { get; }
    public string? BackupDirectory { get; internal set; }
    public bool WriteAttempted { get; internal set; }
    public bool OutcomeUncertain { get; internal set; }
    public string? InstalledFirmwareVersion { get; internal set; }
    public bool AutomaticUpdate { get; internal set; }
    public bool AutomaticRestartRequested { get; internal set; }
    public bool AutomaticRestartAcknowledged { get; internal set; }
    internal bool BootloaderEntryAttempted;
    internal DateTimeOffset? BootloaderEntryDeadline;
    internal DateTimeOffset? RuntimeReturnDeadline;
    internal byte[]? ExpectedRuntimeConfiguration;
    internal OnboardProfileBinding? ExpectedHardwareProfile;
    internal OnboardFirmwareVersion? ExpectedRuntimeVersion;
    internal SetupUsbNode? Bootloader;
    internal JsonElement Selection;
    internal string? FingerprintSha256, BackupPath, OriginalSha256, BundlePath, CurrentSha256;
    internal InstallationIdentity? Installation;
    internal bool Consumed;
    internal bool MigrateLegacy;
    internal ushort OriginalVendor, OriginalProduct;
}
public sealed record SetupUsbNode(string InstanceId, ushort VendorId, ushort ProductId, string Name,
    string Service, IReadOnlyList<string> LocationPaths);
internal sealed record SetupRuntimeConfiguration(byte[] Payload, OnboardProfileBinding? HardwareProfile = null,
    OnboardHardwareProfileStatus? ProfileStatus = null);

/// <summary>Per-operation discovery and provisioning. Never consumes private device bindings.
/// USB metadata selects candidates; the observed cable transition and bootloader independently
/// resolve the hardware before capture. The adapter owns bounded device I/O and read-back.</summary>
public sealed partial class DeviceSetupService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _archiveRoot, _packageRoot;
    private readonly IDeviceSetupEnvironment _environment;
    private readonly Func<string, string> _targetVersionFor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SetupSession? _active;
    public static string RecoveryDirectory => Path.Combine(Paths.UserDataRoot, "Firmware", "Devices");
    public DeviceSetupService() : this(OperatingSystem.IsWindows() ? new WindowsDeviceSetupEnvironment() : new PosixDeviceSetupEnvironment(),
        RecoveryDirectory,
        Path.Combine(AppContext.BaseDirectory, "Firmware")) { }
    internal DeviceSetupService(IDeviceSetupEnvironment environment, string archiveRoot, string packageRoot, Func<string, string>? targetVersionFor = null)
    { _environment = environment; _archiveRoot = Path.GetFullPath(archiveRoot); _packageRoot = Path.GetFullPath(packageRoot); _targetVersionFor = targetVersionFor ?? TargetFirmwareVersionFor; }

    public async Task<IReadOnlyList<SetupDevice>> ListDevicesAsync()
    {
        var nodes = await _environment.ScanAsync();
        return nodes.Where(node => node.Service != "Unavailable").Select(node =>
        {
            if (StockDeviceCatalog.Match(node.VendorId, node.ProductId) is { } stock)
                return new SetupDevice(node.InstanceId, stock.IdentifiedName ?? DeviceCatalog.ModelGroupName(stock.CandidateDeviceIds), stock.CandidateDeviceIds,
                    node.VendorId, node.ProductId, node.LocationPaths, false);
            if (BoardDescriptorRegistry.TryGetRuntimeUsb(node.VendorId, node.ProductId, out var board))
                return new SetupDevice(node.InstanceId, DeviceCatalog.Get(board.DeviceId).Name, board.IsEngineeringOnly ? Array.Empty<string>() : new[] { board.DeviceId },
                    node.VendorId, node.ProductId, node.LocationPaths, true);
            return null;
        }).Where(device => device is not null).Cast<SetupDevice>().ToArray();
    }

    public async Task<SetupSession> BeginAsync(SetupDevice device, SetupOperation operation = SetupOperation.Install)
    {
        await _gate.WaitAsync();
        try
        {
            if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
            await AssertCoreClosed();
            var matches = (await ListDevicesAsync()).Where(item => item.InstanceId == device.InstanceId).ToArray();
            if (matches.Length != 1 || !matches[0].CandidateDeviceIds.SequenceEqual(device.CandidateDeviceIds) ||
                !SamePort(matches[0].LocationPaths, device.LocationPaths)) throw Error("Gerät nicht eindeutig erkannt.");
            device = matches[0];
            if (!device.HasFirmwareCandidate)
                throw Error("Für dieses Modell ist noch keine INGLORIOUS-Firmware verfügbar.");
            if (operation == SetupOperation.Install && device.IsRuntime)
                throw Error("INGLORIOUS ist bereits installiert. Wähle bei Bedarf die Wiederherstellung.");
            if (operation is SetupOperation.Restore or SetupOperation.Update && !device.IsRuntime)
                throw Error("Das Gerät verwendet bereits die Original-Firmware.");
            AssertNoLinks(_archiveRoot);
            System.IO.Directory.CreateDirectory(_archiveRoot);
            var directory = Path.Combine(_archiveRoot, Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            _active = new(device, operation, directory) { OriginalVendor = device.VendorId, OriginalProduct = device.ProductId };
            return _active;
        }
        finally { _gate.Release(); }
    }

    public async Task<SetupProgress> ObserveAsync(SetupSession session)
    {
        await _gate.WaitAsync();
        try
        {
            AssertSession(session);
            if (session.Stage is SetupStage.Blocked or SetupStage.Complete) return Progress(session);
            await AssertCoreClosed();
            var nodes = await _environment.ScanAsync();
            if (session.Stage == SetupStage.Reconnect)
            {
                if (session.AutomaticUpdate && session.RuntimeReturnDeadline is { } deadline && DateTimeOffset.UtcNow >= deadline)
                {
                    // Firmware was already verified. A slow or failed restart must never rerun the write.
                    session.RuntimeReturnDeadline = null;
                    session.AutomaticRestartRequested = false;
                }
                if (nodes.Any(IsBootloader)) return Progress(session);
                var board = BoardDescriptorRegistry.Get(session.DeviceId!);
                var vendor = session.Operation != SetupOperation.Restore ? BoardDescriptorRegistry.RuntimeVendorId : session.OriginalVendor;
                var product = session.Operation != SetupOperation.Restore ? board.RuntimeProductId : session.OriginalProduct;
                if (nodes.Count(node => node.Service != "Unavailable" && node.VendorId == vendor && node.ProductId == product && SamePort(node.LocationPaths, session.Device.LocationPaths)) == 1)
                    session.Stage = SetupStage.Complete;
                return Progress(session);
            }
            if (session.AutomaticUpdate && session.Stage == SetupStage.ConnectBootloader)
            {
                if (session.BootloaderEntryDeadline is not { } deadline || DateTimeOffset.UtcNow >= deadline)
                    return Block(session, "Der Update-Modus wurde nicht rechtzeitig erkannt. Verbinde das Gerät normal neu und starte das Update erneut.");
                // Software entry may be faster than USB polling. The acknowledged command replaces
                // the physical-disconnect observation, never the original instance and port checks.
                var samePort = nodes.Where(node => SamePort(node.LocationPaths, session.Device.LocationPaths)).ToArray();
                if (samePort.Length > 1) return Block(session, "Die USB-Verbindung hat sich geändert. Starte die Einrichtung erneut.");
                if (nodes.Any(node => node.InstanceId == session.Device.InstanceId)) return Progress(session);
            }
            if (session.Stage == SetupStage.Disconnect)
            {
                if (nodes.Any(node => node.InstanceId == session.Device.InstanceId)) return Progress(session);
                // Require an observed interval with neither the normal device nor its bootloader present.
                if (nodes.Any(node => SamePort(node.LocationPaths, session.Device.LocationPaths)))
                    return new(session.Stage, L.T("Trenne das Gerät zuerst vom USB-Kabel."));
                session.Stage = SetupStage.ConnectBootloader;
                return Progress(session);
            }
            var bootloaders = nodes.Where(IsBootloader).ToArray();
            if (bootloaders.Length != 1 || !SamePort(bootloaders[0].LocationPaths, session.Device.LocationPaths))
            {
                if (session.Stage is SetupStage.ReadyToBackup or SetupStage.ReadyToInstall)
                    return Block(session, "Die USB-Verbindung hat sich geändert. Starte die Einrichtung erneut.");
                return Progress(session);
            }
            var boot = bootloaders[0];
            if (!HasUsableUsbTransport(boot))
                return new(session.Stage, L.T(OperatingSystem.IsWindows() ? "Für den Update-Modus fehlt der USB-Treiber." : "Der USB-Zugriff ist nicht freigegeben. Prüfe die USB-Berechtigungen für INGLORIOUS Advanced."));
            if (session.Bootloader is not null)
            {
                if (boot.InstanceId != session.Bootloader.InstanceId) return Block(session, "Die USB-Verbindung hat sich geändert. Starte die Einrichtung erneut.");
                return Progress(session);
            }
            var transport = boot.VendorId == 0x0483 ? "stm32-dfuse" : "wb32-dfu";
            var candidates = session.Device.CandidateDeviceIds.Where(id => TransportFor(id) == transport).ToArray();
            if (candidates.Length != 1) return Block(session, "Gerät nicht eindeutig erkannt.");
            session.DeviceId = candidates[0];
            var listing = await Adapter(session, "list");
            RequireSuccess(listing);
            var selections = listing.GetProperty("devices").EnumerateArray().ToArray();
            if (selections.Length != 1) return Block(session, "Verbinde nur eine Tastatur im Update-Modus.");
            var selection = selections[0];
            if (selection.GetProperty("vid").GetUInt16() != boot.VendorId || selection.GetProperty("pid").GetUInt16() != boot.ProductId ||
                selection.GetProperty("transport").GetString() != transport || !UsbPortMatches(boot.LocationPaths, selection.GetProperty("portPath").GetString(), selection.GetProperty("bus").GetByte()))
                return Block(session, "Gerät nicht eindeutig erkannt.");
            session.Selection = selection.Clone();
            var probe = await Adapter(session, "probe");
            RequireSuccess(probe);
            if (!probe.GetProperty("canCapture").GetBoolean()) return Block(session, "Die Original-Firmware kann nicht sicher gelesen werden.");
            ValidateProbe(probe, session);
            session.FingerprintSha256 = probe.GetProperty("fingerprintSha256").GetString();
            session.Bootloader = boot;
            session.Stage = SetupStage.ReadyToBackup;
            return Progress(session);
        }
        finally { _gate.Release(); }
    }

    internal async Task<SetupProgress> StartAutomaticUpdateAsync(SetupSession session, OnboardFirmwareVersion expectedVersion,
        bool explicitlyConfirmed, Func<Task<SetupRuntimeConfiguration>> readConfiguration, Func<Task> enterBootloader)
    {
        await _gate.WaitAsync();
        try
        {
            AssertSession(session);
            if (!explicitlyConfirmed || session.Operation != SetupOperation.Update || !session.Device.IsRuntime ||
                !SupportsAutomaticUpdateDevice(expectedVersion.DeviceId) ||
                !session.Device.CandidateDeviceIds.SequenceEqual([expectedVersion.DeviceId]) ||
                expectedVersion.BuildId == 0 || expectedVersion.Version < OnboardClient.AppBootloaderMinimumVersion ||
                session.Stage != SetupStage.Disconnect || session.BootloaderEntryAttempted ||
                session.WriteAttempted || session.Consumed || session.OutcomeUncertain)
                throw Error("Starte die Einrichtung erneut.");
            if (!FirmwareSemanticVersion.TryParse(_targetVersionFor(expectedVersion.DeviceId), out var targetVersion) || expectedVersion.Version >= targetVersion)
                throw Error("Die Firmware ist bereits aktuell oder neuer als die angebotene Version.");
            await AssertCoreClosed();
            await RequireBoundRuntimeAsync(session);
            // Consume the entry attempt before calling external code; timeout or lost ACK never retries it.
            session.BootloaderEntryAttempted = true;
            session.AutomaticUpdate = true;
            session.DeviceId = expectedVersion.DeviceId;
            session.InstalledFirmwareVersion = expectedVersion.Version.ToString();
            session.ExpectedRuntimeVersion = expectedVersion;
            var configuration = await readConfiguration();
            if (OnboardSerializer.Decode(configuration.Payload).DeviceId != session.DeviceId ||
                !IsValidAutomaticProfileBinding(session.DeviceId, configuration))
                throw Error("Gerät nicht eindeutig erkannt.");
            session.ExpectedRuntimeConfiguration = configuration.Payload.ToArray();
            session.ExpectedHardwareProfile = configuration.HardwareProfile;
            WriteNew(Path.Combine(session.Directory, "before-update.igcfg.bin"), session.ExpectedRuntimeConfiguration);
            if (session.ExpectedHardwareProfile is { } profile)
                WriteNew(Path.Combine(session.Directory, "before-update-profile.json"), JsonSerializer.SerializeToUtf8Bytes(profile, Json));
            await AssertCoreClosed();
            await RequireBoundRuntimeAsync(session);
            await enterBootloader();
            session.BootloaderEntryDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
            session.Stage = SetupStage.ConnectBootloader;
            return Progress(session);
        }
        catch
        {
            if (session.BootloaderEntryAttempted) session.Stage = SetupStage.Blocked;
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task RequireBoundRuntimeAsync(SetupSession session)
    {
        var nodes = await _environment.ScanAsync();
        var matches = nodes.Where(node => node.InstanceId == session.Device.InstanceId).ToArray();
        if (nodes.Any(IsBootloader) || matches.Length != 1 || matches[0].Service == "Unavailable" ||
            matches[0].VendorId != session.Device.VendorId || matches[0].ProductId != session.Device.ProductId ||
            !SamePort(matches[0].LocationPaths, session.Device.LocationPaths))
            throw Error("Die USB-Verbindung hat sich geändert. Starte die Einrichtung erneut.");
    }

    internal static bool SupportsAutomaticUpdateDevice(string deviceId) =>
        BoardDescriptorRegistry.ProtocolBoards.Any(board => board.DeviceId == deviceId);

    private static bool IsValidAutomaticProfileBinding(string deviceId, SetupRuntimeConfiguration configuration) =>
        deviceId == "gmmk3-pro-100-iso" ? configuration.HardwareProfile is { } profile &&
            configuration.ProfileStatus is { } status && status.StorageHealthy && !status.UploadOpen &&
            !status.SwitchPending && !status.OsPending && status.PhysicalSlot == profile.Slot && status.ActiveSlot == profile.Slot &&
            status.Bind(profile.InstallationId) == profile && (profile.Generation == 0) == status.LegacyFallback &&
            profile.DeviceId == deviceId && profile.Slot is >= 1 and <= 3 && profile.InstallationId != Guid.Empty &&
            profile.InstallationId != new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff") &&
            profile.Generation < ulong.MaxValue && profile.Crc32 == OnboardProtocol.Crc32(configuration.Payload) :
            configuration.HardwareProfile is null && configuration.ProfileStatus is null;

    internal static async Task<SetupRuntimeConfiguration> ReadAutomaticRuntimeConfigurationAsync(OnboardClient client,
        string deviceId, CancellationToken cancellationToken)
    {
        if (deviceId != "gmmk3-pro-100-iso") return new(await client.ReadAsync(cancellationToken));
        var status = await client.GetHardwareProfileStatusAsync(cancellationToken: cancellationToken);
        if (status is null || !status.StorageHealthy || status.UploadOpen || status.SwitchPending || status.OsPending ||
            status.ActiveSlot is < 1 or > 3 || status.PhysicalSlot != status.ActiveSlot)
            throw Error("Die Gerätekonfiguration hat sich geändert. Bitte erneut laden.");
        var snapshot = await client.ReadHardwareProfileAsync(status.ActiveSlot, cancellationToken);
        if (snapshot.Status != status)
            throw Error("Die Gerätekonfiguration hat sich geändert. Bitte erneut laden.");
        return new(snapshot.Payload, snapshot.Binding, snapshot.Status);
    }

    internal async Task VerifyReturnedConfigurationAsync(SetupSession session, OnboardClient client, CancellationToken cancellationToken)
    {
        if (session.ExpectedHardwareProfile is { } expected)
        {
            // Verify the saved slot, even if the physical switch moved while the bootloader was running.
            var snapshot = await client.ReadHardwareProfileAsync(expected.Slot, cancellationToken);
            if (!snapshot.Status.StorageHealthy)
            {
                session.Stage = SetupStage.Blocked;
                throw Error("Die Einstellungen konnten nach dem Update nicht bestätigt werden. Lade die Konfiguration erneut von der Tastatur.");
            }
            VerifyReturnedConfiguration(session, snapshot.Payload, snapshot.Binding);
        }
        else VerifyReturnedConfiguration(session, await client.ReadAsync(cancellationToken));
    }

    internal void VerifyReturnedConfiguration(SetupSession session, byte[] configuration, OnboardProfileBinding? profile = null)
    {
        AssertSession(session);
        if (!session.AutomaticUpdate) return;
        if (session.ExpectedRuntimeConfiguration is not { } expected || !expected.AsSpan().SequenceEqual(configuration) ||
            session.ExpectedHardwareProfile != profile)
        {
            session.Stage = SetupStage.Blocked;
            throw Error("Die Einstellungen konnten nach dem Update nicht bestätigt werden. Lade die Konfiguration erneut von der Tastatur.");
        }
    }

    internal static string TransportFor(string id) => id is "pro-iso" or "pro-ansi" ? "stm32-dfuse" : "wb32-dfu";
    private static void ValidateProbe(JsonElement response, SetupSession session)
    {
        var fingerprint = response.GetProperty("fingerprint");
        var selection = session.Selection;
        if (response.GetProperty("schema").GetInt32() != 1 || response.GetProperty("operation").GetString() != "probe" ||
            response.GetProperty("deviceId").GetString() != session.DeviceId ||
            !Regex.IsMatch(response.GetProperty("fingerprintSha256").GetString() ?? "", "\\A[0-9a-fA-F]{64}\\z") ||
            fingerprint.GetProperty("transport").GetString() != TransportFor(session.DeviceId!) ||
            fingerprint.GetProperty("vid").GetUInt16() != selection.GetProperty("vid").GetUInt16() ||
            fingerprint.GetProperty("pid").GetUInt16() != selection.GetProperty("pid").GetUInt16() ||
            fingerprint.GetProperty("bus").GetByte() != selection.GetProperty("bus").GetByte() ||
            fingerprint.GetProperty("portPath").GetString() != selection.GetProperty("portPath").GetString() ||
            (fingerprint.GetProperty("serial").GetString() ?? "") != (selection.GetProperty("serial").GetString() ?? "") ||
            fingerprint.GetProperty("configuration").GetInt32() != 1 || fingerprint.GetProperty("interface").GetInt32() != 0 ||
            fingerprint.GetProperty("alternate").GetInt32() != 0 || fingerprint.GetProperty("flashBase").GetUInt32() != 0x08000000 ||
            fingerprint.GetProperty("flashBytes").GetInt32() != FlashBytesFor(session.DeviceId!) ||
            fingerprint.GetProperty("sramBytes").GetInt32() != SramBytesFor(session.DeviceId!))
            throw Error("Der Prüfbericht gehört nicht zum erkannten Gerät.");
    }
    internal static int FlashBytesFor(string id) => BoardDescriptorRegistry.Get(id).FlashBytes;
    internal static int SramBytesFor(string id) => BoardDescriptorRegistry.Get(id).SramBytes;
    internal static bool IsBootloader(SetupUsbNode node) => (node.VendorId, node.ProductId) is (0x0483, 0xDF11) or (0x342D, 0xDFA0);
    internal static bool SamePort(IReadOnlyList<string> left, IReadOnlyList<string> right) => left.Count > 0 && right.Count > 0 &&
        left.Intersect(right, StringComparer.OrdinalIgnoreCase).Any(path => path.StartsWith("USBPORT(", StringComparison.OrdinalIgnoreCase)
            ? PlatformUsbIdentity.IsCanonicalLocation(path) : path.Contains("#USB(", StringComparison.OrdinalIgnoreCase));
    internal static bool UsbPortMatches(IReadOnlyList<string> locations, string? portPath, byte? bus = null) =>
        !string.IsNullOrWhiteSpace(portPath) && locations.Any(location => location.StartsWith("USBPORT(", StringComparison.OrdinalIgnoreCase)
            ? bus.HasValue && PlatformUsbIdentity.IsCanonicalLocation(location) && location == PlatformUsbIdentity.Location(bus.Value, portPath)
            : string.Join('.', Regex.Matches(location, @"#USB\((\d+)\)", RegexOptions.IgnoreCase).Select(match => match.Groups[1].Value)) == portPath);
    internal static bool HasUsableUsbTransport(SetupUsbNode node) =>
        string.Equals(node.Service, "WinUSB", StringComparison.OrdinalIgnoreCase) && node.LocationPaths.Any(path => !path.StartsWith("USBPORT(", StringComparison.OrdinalIgnoreCase) && path.Contains("#USB(", StringComparison.OrdinalIgnoreCase)) ||
        node.Service == "libusb" && node.LocationPaths.Count == 1 && PlatformUsbIdentity.IsCanonicalLocation(node.LocationPaths[0]);

    public bool MatchesRuntimePort(SetupSession session, string hidPath, ushort vendor, ushort product)
    {
        AssertSession(session);
        if (session.DeviceId is not { } id || !BoardDescriptorRegistry.TryGetRuntimeUsb(vendor, product, out var board) || board.DeviceId != id) return false;
        return SamePort(session.Device.LocationPaths, PlatformUsbIdentity.LocationsForHidPath(hidPath, vendor, product));
    }
    public bool MatchesRuntimePort(SetupDevice device, string hidPath, ushort vendor, ushort product) =>
        device.IsRuntime && device.VendorId == vendor && device.ProductId == product &&
        SamePort(device.LocationPaths, PlatformUsbIdentity.LocationsForHidPath(hidPath, vendor, product));

    public static bool? IsGloriousCoreRunning()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("Glorious Core"); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
        bool uncertain = false;
        try
        {
            foreach (var process in processes)
            {
                try { if (!process.HasExited && string.Equals(process.ProcessName, "Glorious Core", StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or NotSupportedException) { uncertain = true; }
            }
            return uncertain ? null : false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static SetupException Error(string message) => new(L.T(message));
    public static string ForUser(Exception error) => error switch
    {
        SetupException => error.Message,
        OnboardBootloaderEntryException => L.T("Der Wechsel in den Update-Modus wurde nicht bestätigt. Prüfe die USB-Verbindung."),
        OnboardCommandException { Status: 3 } => L.T("Die Tastatur ist beschäftigt. Warte kurz und versuche es erneut."),
        _ => L.T("Der Vorgang konnte nicht abgeschlossen werden. Es wird nichts automatisch wiederholt.")
    };
    private void AssertSession(SetupSession session)
    { if (!ReferenceEquals(session, _active)) throw Error("Starte die Einrichtung erneut."); }
    private async Task AssertCoreClosed()
    { if (await _environment.IsCoreRunningAsync() is not false) throw new SetupException(L.T("Schließe Glorious CORE, bevor du fortfährst."), recoverable: true); }
    private static SetupProgress Block(SetupSession session, string message)
    { session.Stage = SetupStage.Blocked; return new(session.Stage, L.T(message), session.DeviceId); }
    private static SetupProgress Progress(SetupSession session) => new(session.Stage, L.T(session.Stage switch
    {
        SetupStage.Disconnect => "Trenne das Gerät vom USB-Kabel.",
        SetupStage.ConnectBootloader => session.AutomaticUpdate ? "Das Gerät wechselt in den Update-Modus …" : "Verbinde das Gerät im Update-Modus am selben USB-Anschluss.",
        SetupStage.ReadyToBackup => session.Operation == SetupOperation.Install
            ? "Gerät erkannt. Die Original-Firmware kann jetzt gesichert werden."
            : "Gerät erkannt. Die installierte Firmware kann jetzt geprüft werden.",
        SetupStage.ReadyToInstall => "Die Sicherung ist geprüft. Der Vorgang wird fortgesetzt.",
        SetupStage.Reconnect => session.AutomaticRestartRequested ? "Die Tastatur startet neu …" : "Verbinde das Gerät ohne gedrückte Tasten neu.",
        SetupStage.Complete => "Firmware übertragen und Gerät wieder erkannt.",
        _ => "Der Vorgang wurde angehalten."
    }), session.DeviceId);

    private async Task<JsonElement> Adapter(SetupSession session, string operation, bool confirmWrite = false)
    {
        var request = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["operation"] = operation, ["sessionDirectory"] = Path.Combine(session.Directory, operation + "-" + Guid.NewGuid().ToString("N")),
            ["confirmWrite"] = confirmWrite
        };
        if (session.DeviceId is not null) request["deviceId"] = session.DeviceId;
        if (session.Selection.ValueKind == JsonValueKind.Object) request["selection"] = session.Selection;
        if (session.FingerprintSha256 is not null) request["expectedFingerprintSha256"] = session.FingerprintSha256;
        if (operation is "install" or "restore" or "update")
        {
            request["backupPath"] = session.BackupPath;
            request["expectedBackupSha256"] = session.OriginalSha256;
            request["expectedInstallationId"] = session.Installation!.InstallationId.ToString();
            if (operation is "install" or "update") request["bundlePath"] = session.BundlePath;
            if (operation == "update") request["expectedCurrentSha256"] = session.CurrentSha256;
            if (operation == "update" && session.AutomaticUpdate) request["restartAfterVerify"] = true;
            if (session.MigrateLegacy) request["migrateLegacy"] = true;
        }
        return await _environment.InvokeAdapterAsync(request, session.Directory);
    }
    private static void RequireSuccess(JsonElement response)
    {
        if (response.GetProperty("status").GetString() is not ("ok" or "verified"))
        {
            if (!OperatingSystem.IsWindows() && response.TryGetProperty("errorCode", out var code) && (code.GetString() ?? "").Contains("libusb error -3", StringComparison.Ordinal))
                throw Error("Der USB-Zugriff ist nicht freigegeben. Prüfe die USB-Berechtigungen für INGLORIOUS Advanced.");
            throw Error("Das Gerät konnte nicht sicher geprüft werden. Es wurde keine Installation freigegeben.");
        }
    }
    internal static void AssertNoLinks(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw Error("Verknüpfte Sicherungsordner werden nicht verwendet.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw Error("Verknüpfte Sicherungsdateien werden nicht verwendet.");
    }
}

public sealed class SetupException(string message, bool recoverable = false) : IOException(message)
{
    public bool Recoverable { get; } = recoverable;
}

internal interface IDeviceSetupEnvironment
{
    Task<IReadOnlyList<SetupUsbNode>> ScanAsync();
    Task<bool?> IsCoreRunningAsync();
    Task<JsonElement> InvokeAdapterAsync(IReadOnlyDictionary<string, object?> request, string evidenceDirectory);
}
