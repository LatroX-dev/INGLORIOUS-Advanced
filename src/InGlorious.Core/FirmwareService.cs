using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InGlorious.Core;

public enum FirmwareOperation { Update, RestoreOriginal, InstallFromOriginal }
internal enum FirmwareNoWriteReason { None, UnsupportedSource, SourceChanged }
public sealed record FirmwareAvailability(bool Available, string Message)
{
    public string? NormalInstanceId { get; init; }
}
public sealed record FirmwareResult(bool Verified, string Message, string? EvidenceDirectory, bool ExecutionStarted, FirmwareReconnectTarget? ReconnectTarget = null)
{
    public bool WriteNotAttemptedVerified { get; init; }
}
public sealed class FirmwareReconnectTarget
{
    internal FirmwareReconnectTarget(Guid operationId, string deviceId, FirmwareOperation operation, string bootloaderInstanceId, IEnumerable<string> locationPaths)
    {
        RunId = operationId; DeviceId = deviceId; Operation = operation; BootloaderInstanceId = bootloaderInstanceId;
        LocationPaths = Array.AsReadOnly(locationPaths.ToArray());
        ExpectedVendorId = operation == FirmwareOperation.RestoreOriginal ? "320F" : "FEED";
        ExpectedProductId = (deviceId, operation == FirmwareOperation.RestoreOriginal) switch
        {
            ("pro-iso", true) => "5046", ("numpad", true) => "5088",
            ("pro-iso", false) => "1C01", ("numpad", false) => "1C02",
            _ => throw new InvalidDataException("Unbekanntes Gerät.")
        };
    }
    public Guid RunId { get; }
    public string DeviceId { get; }
    public FirmwareOperation Operation { get; }
    public string ExpectedVendorId { get; }
    public string ExpectedProductId { get; }
    public IReadOnlyList<string> LocationPaths { get; }
    internal string BootloaderInstanceId { get; }
}
public sealed class FirmwarePlan
{
    internal FirmwarePlan(string deviceId, FirmwareOperation operation, string text, string? expectedSourceRelease = null, string? sourceVersion = null)
    { DeviceId = deviceId; Operation = operation; Text = text; ExpectedSourceRelease = expectedSourceRelease; SourceVersion = sourceVersion; }
    public Guid Id { get; } = Guid.NewGuid();
    public string DeviceId { get; }
    public FirmwareOperation Operation { get; }
    public string Text { get; }
    public string? ExpectedSourceRelease { get; }
    public string? SourceVersion { get; }
}

/// <summary>Only the two pinned local update/recovery wrappers are callable. No arbitrary command, path or device API is exposed.</summary>
public sealed class FirmwareService
{
    // The local file contains private device identities and recovery pins. It is deliberately absent from public exports.
    private const string BindingHash = "E6C7B583242AA80713C703F38B28ED1F62C3BADE73B73E0A012EF56ABD577B20";
    private readonly string _workspace;
    private readonly string _bindingHash;
    private readonly IFirmwareProcessRunner _runner;
    private readonly string? _powerShellOverride;
    private readonly string _evidenceRoot;
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private FirmwarePlan? _prepared;
    private FirmwareReconnectTarget? _reconnectTarget;
    private string? _reconnectPowerShell;
    private readonly object _reconnectSync = new();
    private long _reconnectRevision;
    private readonly object _diagnosticSync = new();
    private readonly Dictionary<string, string> _lastDiagnosticHashes = [];
    public string? DiagnosticDirectory { get; private set; }
    internal void RecordLightingTestFailure(Exception error) => SaveDiagnostic("lighting-test", error: error);

    public FirmwareService() : this(Paths.WorkspaceRoot, new FirmwareProcessRunner(), BindingHash, null, FirmwareEvidenceStore.DefaultRoot) { }
    internal FirmwareService(string workspace, IFirmwareProcessRunner runner, string bindingHash, string? powerShellOverride, string? evidenceRoot = null)
    { _workspace = Path.GetFullPath(workspace); _runner = runner; _bindingHash = bindingHash; _powerShellOverride = powerShellOverride; _evidenceRoot = Path.GetFullPath(evidenceRoot ?? Path.Combine(_workspace, "reports", "firmware")); }

    public static string DeviceName(string deviceId) => deviceId switch { "pro-iso" => "GMMK PRO ISO", "numpad" => "GMMK Numpad", _ => throw new InvalidDataException("Dieses Gerät wird vom lokalen Firmware-Werkzeug nicht unterstützt.") };
    public static string OperationName(FirmwareOperation operation) => operation switch { FirmwareOperation.Update => L.T("Firmware aktualisieren"), FirmwareOperation.RestoreOriginal => L.T("Original-Firmware wiederherstellen"), FirmwareOperation.InstallFromOriginal => L.T("INGLORIOUS einrichten"), _ => throw new InvalidDataException("Unbekannter Firmware-Vorgang.") };
    internal static string ScriptMode(FirmwareOperation operation) => operation switch { FirmwareOperation.Update => "Update", FirmwareOperation.RestoreOriginal => "Restore", FirmwareOperation.InstallFromOriginal => "Install", _ => throw new InvalidDataException("Unbekannter Firmware-Vorgang.") };
    internal static int FlashBytes(string deviceId) => deviceId == "pro-iso" ? 262144 : deviceId == "numpad" ? 131072 : throw new InvalidDataException("Unbekanntes Gerät.");

    // Official CORE Legacy 1.1.36 and installed CORE 2.1.21 both use "Glorious Core.exe".
    // The Legacy name was verified in the signed official installer, not inferred from its title.
    // ProcessName has no extension;
    // do not match our own INGLORIOUS app, generic updaters, drivers, or window titles.
    internal static bool IsGloriousCoreProcessName(string? name) => string.Equals(name, "Glorious Core", StringComparison.OrdinalIgnoreCase);

    internal static bool? IsGloriousCoreRunning()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("Glorious Core"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { return null; }
        try
        {
            var uncertain = false;
            foreach (var process in processes)
            {
                try
                {
                    // No MainWindowHandle requirement: a tray-only Electron process still owns CORE.
                    if (!process.HasExited && IsGloriousCoreProcessName(process.ProcessName)) return true;
                }
                catch (InvalidOperationException) { /* Process exited between enumeration and inspection. */ }
                catch (ArgumentException) { /* Process no longer exists. */ }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
                { uncertain = true; }
            }
            return uncertain ? null : false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public FirmwareAvailability CheckAvailability(string deviceId, FirmwareOperation operation)
    {
        try { _ = GetEnvironment(deviceId, operation); return new(true, L.T("Lokale Werkzeuge und Dateien sind vorhanden und ihre Prüfsummen stimmen.")); }
        catch (Exception ex) { SaveDiagnostic("availability", error: ex); return new(false, FirmwareMessages.ForUser(ex.Message)); }
    }

    public async Task<FirmwarePlan> PrepareAsync(string deviceId, FirmwareOperation operation, IProgress<string>? log = null, FirmwareRuntimeVersion? installedFirmware = null)
    {
        InvalidateReconnectTarget();
        await _exclusive.WaitAsync();
        try
        {
            _prepared = null;
            Report(log, L.T("Prüfe die Dateien für diesen Firmware-Vorgang …"));
            var environment = GetEnvironment(deviceId, operation);
            var expectedSource = ExpectedSourceForRuntimeVersion(environment.Device, operation, installedFirmware);
            using var locks = LockArtifacts(environment.Files);
            _ = GetEnvironment(deviceId, operation); // Pins are rechecked while source files are held read-only.
            var command = BuildWrapperCommand(environment.PowerShell, deviceId, operation, execute: false);
            var result = await RunProcessAsync(command);
            if (result.ExitCode != 0) throw new IOException("Die lokale Planprüfung ist fehlgeschlagen. " + LastMessage(result));
            using var json = ParseJson(result.StandardOutput, "Der Firmware-Plan enthält kein gültiges Ergebnis.");
            ValidatePlan(json.RootElement, environment.Device, operation);
            _prepared = new(deviceId, operation, json.RootElement.GetRawText(), expectedSource, installedFirmware?.Report.Version.ToString());
            Report(log, L.T("Vorbereitung abgeschlossen. Die Firmware-Dateien sind geprüft."));
            return _prepared;
        }
        catch (Exception ex)
        {
            SaveDiagnostic("preparation", error: ex);
            var message = FirmwareMessages.ForUser(ex.Message);
            Report(log, message);
            throw new IOException(message, ex);
        }
        finally { _exclusive.Release(); }
    }

    private static string? ExpectedSourceForRuntimeVersion(FirmwareDeviceBinding device, FirmwareOperation operation, FirmwareRuntimeVersion? installed)
    {
        if (operation != FirmwareOperation.Update)
        {
            if (installed is not null) throw new InvalidDataException("Die Ausgangsfirmware gehört nicht zu diesem Gerät oder Vorgang.");
            return null;
        }
        if (installed is null) throw new InvalidDataException("Die Firmware-Version konnte nicht geprüft werden.");
        var report = installed.Report;
        var pid = device.DeviceId == "pro-iso" ? "1C01" : "1C02";
        if (operation != FirmwareOperation.Update || report.DeviceId != device.DeviceId || report.BuildId == 0
            || installed.ObservedAt == default || installed.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(1)
            || !Regex.IsMatch(installed.UsbInstanceId ?? "", @"\AUSB\\VID_FEED&PID_" + pid + @"\\[^\\\r\n\x00]{1,256}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("Die Ausgangsfirmware gehört nicht zu diesem Gerät oder Vorgang.");
        if (report.Version >= FirmwareRelease.CurrentVersion)
            throw new InvalidDataException("Die Firmware ist bereits aktuell oder neuer als die angebotene Version.");
        var sources = device.UpdateSources.Where(source => FirmwareSemanticVersion.TryParse(source.Release.Split('-')[0], out var version) && version == report.Version).ToArray();
        if (sources.Length != 1) throw new InvalidDataException("Die Ausgangsfirmware ist nicht für dieses Update freigegeben.");
        return sources[0].Release;
    }

    /// <summary>Reads Windows PnP identity and driver status only. It neither consumes the plan nor opens a bootloader interface.</summary>
    public async Task<FirmwareAvailability> CheckBootloaderAsync(FirmwarePlan plan)
    {
        await _exclusive.WaitAsync();
        try
        {
            if (plan is null || !ReferenceEquals(plan, _prepared)) throw new InvalidOperationException("Zuerst den aktuellen Plan prüfen und genau diesen Vorgang bestätigen.");
            var environment = GetEnvironment(plan.DeviceId, plan.Operation);
            using var locks = LockArtifacts(environment.Files);
            _ = GetEnvironment(plan.DeviceId, plan.Operation);
            // Polling has no progress sink: a temporarily disconnected device must not flood the operation log.
            var identity = await RunProcessAsync(BuildIdentityCommand(environment.PowerShell, environment.Device));
            ValidateIdentity(identity, environment.Device, requireLocationPaths: true);
            return new(true, L.T("Bootloader erkannt."));
        }
        catch (Exception ex) { SaveDiagnostic("bootloader", error: ex); return new(false, FirmwareMessages.ForUser(ex.Message)); }
        finally { _exclusive.Release(); }
    }

    public async Task<FirmwareResult> ExecuteAsync(FirmwarePlan plan, bool explicitlyConfirmed, IProgress<string>? log = null)
    {
        var reconnectRevision = InvalidateReconnectTarget();
        await _exclusive.WaitAsync();
        var started = false;
        string? evidenceDirectory = null;
        string? runRoot = null;
        HashSet<string>? previousDirectories = null;
        try
        {
            if (!explicitlyConfirmed || !ReferenceEquals(plan, _prepared)) throw new InvalidOperationException("Zuerst den aktuellen Plan prüfen und genau diesen Vorgang bestätigen.");
            _prepared = null; // Every attempt needs a new plan; an uncertain flash is never retried automatically.
            var environment = GetEnvironment(plan.DeviceId, plan.Operation);
            using var locks = LockArtifacts(environment.Files);
            _ = GetEnvironment(plan.DeviceId, plan.Operation);
            var operationLockPath = Inside("data/firmware-operation.lock");
            AssertNoReparseAncestor(Path.GetDirectoryName(operationLockPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(operationLockPath)!);
            using var operationLock = new FileStream(operationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Report(log, L.T("Prüfe den zuvor identifizierten Bootloader und seinen Treiber …"));
            var identity = await RunProcessAsync(BuildIdentityCommand(environment.PowerShell, environment.Device));
            var reconnectPaths = ValidateIdentity(identity, environment.Device, requireLocationPaths: true);
            runRoot = RunRoot(plan.DeviceId, plan.Operation);
            AssertNoReparseAncestor(runRoot);
            previousDirectories = Directory.Exists(runRoot) ? Directory.GetDirectories(runRoot).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            Report(log, L.T("Bootloader und USB-Anschluss sind geprüft."));
            Report(log, L.T("Vorgang startet. USB-Kabel angeschlossen lassen und diese Anwendung geöffnet lassen."));
            started = true;
            var executeCommand = BuildWrapperCommand(environment.PowerShell, plan.DeviceId, plan.Operation, execute: true);
            if (plan.ExpectedSourceRelease is not null) executeCommand = executeCommand with { Arguments = [.. executeCommand.Arguments, "-ExpectedSourceRelease", plan.ExpectedSourceRelease] };
            var result = await RunProcessAsync(executeCommand);
            var newDirectories = Directory.Exists(runRoot) ? Directory.GetDirectories(runRoot).Where(path => !previousDirectories.Contains(path)).ToArray() : [];
            if (newDirectories.Length == 1) evidenceDirectory = newDirectories[0];
            if (result.ExitCode != 0)
            {
                var noWriteReason = evidenceDirectory is null ? FirmwareNoWriteReason.None : ProveNoWriteFailure(evidenceDirectory, environment.Device, plan.Operation, plan.ExpectedSourceRelease);
                var noWrite = noWriteReason != FirmwareNoWriteReason.None;
                var failure = noWriteReason switch { FirmwareNoWriteReason.UnsupportedSource => FirmwareMessages.ForUnsupportedSourceWithoutWrite(), FirmwareNoWriteReason.SourceChanged => FirmwareMessages.ForSourceChangedWithoutWrite(), _ => FirmwareMessages.ForExecutionFailure() };
                Report(log, failure);
                return new(false, failure, ArchiveEvidence(evidenceDirectory, plan, verified: false, log) ?? DiagnosticDirectory, true) { WriteNotAttemptedVerified = noWrite };
            }
            if (evidenceDirectory is null) throw new InvalidDataException("Kein eindeutiger neuer Prüfbericht gefunden. Ein Prozessende allein bestätigt kein Firmware-Ergebnis.");
            Report(log, L.T("Prüfe die gesicherten Daten und die vollständigen Rücklesungen …"));
            VerifyEvidence(evidenceDirectory, environment.Device, plan.Operation, plan.ExpectedSourceRelease);
            Report(log, L.T("Übertragung geprüft. Beide vollständigen Rücklesungen stimmen überein."));
            evidenceDirectory = ArchiveEvidence(evidenceDirectory, plan, verified: true, log);
            FirmwareReconnectTarget? reconnectTarget = null;
            lock (_reconnectSync)
            {
                if (reconnectRevision == _reconnectRevision)
                {
                    reconnectTarget = new(plan.Id, plan.DeviceId, plan.Operation, environment.Device.InstanceId, reconnectPaths);
                    _reconnectPowerShell = environment.PowerShell;
                    _reconnectTarget = reconnectTarget;
                }
            }
            var detail = plan.Operation switch { FirmwareOperation.Update => L.T(" Die 32 KiB mit bisherigen Einstellungen sind bytegleich erhalten."), FirmwareOperation.InstallFromOriginal => L.T(" INGLORIOUS startet mit Standardeinstellungen; Farben und Profile müssen anschließend separat übertragen werden."), _ => L.T(" Das intern gesicherte Originalabbild ist zurückgeschrieben.") };
            return new(true, L.T("Zwei vollständige Rücklesungen stimmen mit dem vorgesehenen Flash-Abbild überein.") + detail + L.T(" USB erst jetzt neu verbinden und die Funktion prüfen. Der normale Start ist hier noch nicht bestätigt."), evidenceDirectory, true, reconnectTarget);
        }
        catch (Exception ex)
        {
            InvalidateReconnectTarget();
            SaveDiagnostic("execution", error: ex);
            if (started)
            {
                // A runner failure can occur after it created logs but before returning an exit code.
                if (evidenceDirectory is null && runRoot is not null && previousDirectories is not null)
                {
                    try
                    {
                        var added = Directory.Exists(runRoot) ? Directory.GetDirectories(runRoot).Where(path => !previousDirectories.Contains(path)).ToArray() : [];
                        if (added.Length == 1) evidenceDirectory = added[0];
                    }
                    catch { /* The firmware outcome remains uncertain; evidence discovery must not hide it. */ }
                }
                evidenceDirectory = ArchiveEvidence(evidenceDirectory, plan, verified: false, log);
            }
            var message = started ? FirmwareMessages.ForExecutionFailure() : L.T("Es wurde kein Flash-Vorgang gestartet. ") + FirmwareMessages.ForUser(ex.Message);
            Report(log, message);
            return new(false, message, evidenceDirectory ?? DiagnosticDirectory, started);
        }
        finally { _exclusive.Release(); }
    }

    private string? ArchiveEvidence(string? source, FirmwarePlan plan, bool verified, IProgress<string>? log)
    {
        if (source is null) return null;
        try { return new FirmwareEvidenceStore(_evidenceRoot).Archive(source, plan.DeviceId, ScriptMode(plan.Operation), verified); }
        catch (Exception ex)
        {
            // The verified flash outcome is independent of copying its report. Keep the original evidence usable.
            SaveDiagnostic("archive", error: ex);
            Report(log, L.T("Prüfdateien konnten nicht archiviert werden; der ursprüngliche Prüfordner bleibt erhalten."));
            return source;
        }
    }

    private long InvalidateReconnectTarget()
    {
        lock (_reconnectSync)
        {
            _reconnectTarget = null; _reconnectPowerShell = null;
            return ++_reconnectRevision;
        }
    }

    /// <summary>Observes normal USB enumeration at the last verified operation's connection. Never opens HID or runs a firmware wrapper.</summary>
    public async Task<FirmwareAvailability> CheckNormalConnectionAsync(FirmwareReconnectTarget target)
    {
        await _exclusive.WaitAsync();
        try
        {
            string powerShell;
            lock (_reconnectSync)
            {
                if (target is null || !ReferenceEquals(target, _reconnectTarget) || _reconnectPowerShell is null)
                    throw new InvalidOperationException("Kein aktueller, vollständig geprüfter Firmware-Vorgang für diese Wiederverbindung.");
                powerShell = _reconnectPowerShell;
            }
            var result = await RunProcessAsync(BuildNormalConnectionCommand(powerShell));
            // A newly requested Prepare/Execute invalidates an in-flight observation immediately.
            var normalInstanceId = ValidateNormalConnection(result, target);
            lock (_reconnectSync)
            {
                if (!ReferenceEquals(target, _reconnectTarget)) throw new InvalidOperationException("Die Wiederverbindungsprüfung gehört nicht mehr zum aktuellen Vorgang.");
                return new(true, L.T("Gerät im Normalmodus am bisherigen USB-Anschluss erkannt.")) { NormalInstanceId = normalInstanceId };
            }
        }
        catch (Exception ex) { SaveDiagnostic("reconnect", error: ex); return new(false, FirmwareMessages.ForUser(ex.Message)); }
        finally { _exclusive.Release(); }
    }

    internal FirmwareCommand BuildNormalConnectionCommand(string powerShell)
    {
        // A constant PnP-only query: neither user values nor device paths become executable code.
        const string script = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
            try {
            # Only physical USB roots, not &MI_ interfaces or &LAMPARRAY software children.
            $devices = @(Get-PnpDevice -PresentOnly | Where-Object {
                $_.InstanceId -match '\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\[^\\\r\n\x00]+\z'
            })
            if($devices.Count -gt 4096){throw 'Windows meldet zu viele USB-Geräte.'}
            $properties = @()
            if($devices.Count -gt 0){
                # One provider call avoids repeating its startup for every USB device. Serialize
                # provider operations: concurrent CDXML results can repeat or misassociate nodes.
                $properties = @(Get-PnpDeviceProperty -InstanceId $devices.InstanceId -KeyName DEVPKEY_Device_LocationPaths,DEVPKEY_Device_ProblemCode -ThrottleLimit 1 | ForEach-Object {
                    [pscustomobject]@{ InstanceId = [string]$_.InstanceId; KeyName = [string]$_.KeyName; Data = $_.Data }
                })
            }
            $items = @(foreach($device in $devices){
                $own = @($properties | Where-Object InstanceId -eq $device.InstanceId)
                $locations = @($own | Where-Object KeyName -eq 'DEVPKEY_Device_LocationPaths')
                $problems = @($own | Where-Object KeyName -eq 'DEVPKEY_Device_ProblemCode')
                if($locations.Count -ne 1 -or $problems.Count -ne 1){throw 'Windows meldet keine eindeutigen USB-Geräteeigenschaften.'}
                $paths = @($locations[0].Data)
                $problem = @($problems[0].Data)
                [pscustomobject]@{ instanceId = [string]$device.InstanceId; status = [string]$device.Status; problemCode = $(if($problem.Count -eq 1){$problem[0]}else{$null}); locationPaths = $paths }
            })
            ConvertTo-Json -InputObject $items -Depth 4 -Compress
            } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
            """;
        return new(powerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], _workspace, FirmwareCommandKind.NormalConnectionCheck);
    }

    private static readonly Regex PhysicalUsbId = new(@"\AUSB\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})\\[^\\\r\n\x00]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static bool IsNormalConnectionRootId(string? instanceId) => instanceId is { Length: > 0 and <= 4096 } && PhysicalUsbId.IsMatch(instanceId);
    private static readonly Regex UsbLocationPath = new(@"\A[A-Z0-9_]+\([A-Z0-9_,.+:\-]+\)(?:#[A-Z0-9_]+\([A-Z0-9_,.+:\-]+\))*#USB\([0-9]+\)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Windows can provide the same physical USB connection as both a PCI/USB path and an
    // ACPI-only hierarchy. Preserve both complete paths; never turn either into a prefix/port match.
    private static readonly Regex AcpiLocationPath = new(@"\AACPI\(_SB_\)(?:#ACPI\([A-Z0-9_]{1,64}\)){2,15}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string[] ReadLocationPaths(JsonElement root, bool required)
    {
        if (!root.TryGetProperty("locationPaths", out var element) || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 16)
            throw new InvalidDataException("Windows meldet keine gültigen USB-Anschlusswege.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 4096 } path ||
                !(UsbLocationPath.IsMatch(path) || AcpiLocationPath.IsMatch(path)) || !paths.Add(path))
                throw new InvalidDataException("Windows meldet einen ungültigen oder mehrfachen USB-Anschlussweg.");
        }
        if (required && paths.Count == 0) throw new InvalidDataException("Der USB-Anschluss des Bootloaders konnte nicht gebunden werden. Es wird nichts geschrieben.");
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ValidateNormalConnection(FirmwareProcessResult result, FirmwareReconnectTarget target)
    {
        if (result.ExitCode != 0) throw new IOException("Die normale USB-Verbindung konnte nicht geprüft werden. " + LastMessage(result));
        using var json = ParseJson(result.StandardOutput, "Wiederverbindungsprüfung ohne gültige Geräteliste.");
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 4096) throw new InvalidDataException("Wiederverbindungsprüfung ohne gültige Geräteliste.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matching = 0;
        var validNormal = false;
        string? normalInstanceId = null;
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var instanceId = Text(item, "instanceId");
            var id = PhysicalUsbId.Match(instanceId);
            if (instanceId.Length > 4096 || !id.Success || instanceId[..instanceId.LastIndexOf('\\')].Contains("&MI_", StringComparison.OrdinalIgnoreCase) || !seen.Add(instanceId))
                throw new InvalidDataException("Wiederverbindungsprüfung enthält keine eindeutigen physischen USB-Geräte.");
            if (string.Equals(instanceId, target.BootloaderInstanceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Das Gerät befindet sich noch im Bootloader.");
            var paths = ReadLocationPaths(item, required: false);
            if (!paths.Any(path => target.LocationPaths.Contains(path, StringComparer.OrdinalIgnoreCase))) continue;
            matching++;
            normalInstanceId = instanceId;
            validNormal = string.Equals(id.Groups["vid"].Value, target.ExpectedVendorId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(id.Groups["pid"].Value, target.ExpectedProductId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Text(item, "status"), "OK", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("problemCode", out var problem) && problem.ValueKind == JsonValueKind.Number && problem.TryGetInt32(out var code) && code == 0;
        }
        if (matching != 1 || !validNormal) throw new InvalidDataException("Warte auf das Gerät im Normalmodus am bisherigen USB-Anschluss.");
        return normalInstanceId!;
    }

    private FirmwareEnvironment GetEnvironment(string deviceId, FirmwareOperation operation)
    {
        _ = DeviceName(deviceId); _ = OperationName(operation);
        var bindingPath = Inside("firmware/local-firmware-bindings.json");
        if (!File.Exists(bindingPath)) throw new FileNotFoundException("Die lokalen Freigaben und privaten Original-Sicherungen fehlen. Dieses Firmware-Werkzeug ist nur für die beiden hier geprüften Geräte eingerichtet; eine allgemeine Firmware-Installation wird nicht angeboten.");
        AssertNoReparseAncestor(bindingPath);
        using var bindingStream = new FileStream(bindingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (bindingStream.Length > 1024 * 1024) throw new InvalidDataException("Die lokale Firmware-Freigabe ist zu groß.");
        var bindingBytes = new byte[checked((int)bindingStream.Length)];
        bindingStream.ReadExactly(bindingBytes);
        if (Hash(bindingBytes) != _bindingHash) throw new InvalidDataException("Die lokale Firmware-Freigabe weicht von der geprüften Version ab.");
        var bindings = JsonSerializer.Deserialize<FirmwareBindings>(bindingBytes, JsonOptions) ?? throw new InvalidDataException("Leere lokale Firmware-Freigabe.");
        if (bindings.SchemaVersion != 1 || bindings.Devices.Count != 2) throw new InvalidDataException("Ungültige lokale Firmware-Freigabe.");
        var device = bindings.Devices.SingleOrDefault(d => d.DeviceId == deviceId) ?? throw new InvalidDataException("Dieses Gerät ist nicht lokal freigegeben.");
        if (!Regex.IsMatch(device.InstanceId, @"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\[A-Za-z0-9&]+\z") || !device.InstanceId.StartsWith(device.BootloaderPrefix, StringComparison.Ordinal) || !Regex.IsMatch(device.BootloaderPrefix, @"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\\z")) throw new InvalidDataException("Ungültige lokale Geräteidentität.");
        var operationFiles = operation switch { FirmwareOperation.Update => device.UpdateFiles, FirmwareOperation.InstallFromOriginal => device.InstallFiles, _ => [] };
        if (operation == FirmwareOperation.Update) ValidateUpdateSources(device);
        var files = bindings.Common.Concat(device.BaseFiles).Concat(operationFiles).Append(new("firmware/local-firmware-bindings.json", bindingBytes.Length, _bindingHash)).ToArray();
        if (!files.Any(file => file.Path == WrapperPath(deviceId)) || !files.Any(file => file.Path == "scripts/FirmwareUpdate.Helpers.ps1")) throw new InvalidDataException("Gepinnte Firmware-Skripte fehlen.");
        foreach (var file in files) CheckFile(file);
        var powerShell = _powerShellOverride ?? FindPowerShell();
        if (!File.Exists(powerShell)) throw new FileNotFoundException("PowerShell 7 ist nicht verfügbar. Es wird nichts automatisch installiert.");
        return new(device, powerShell, files);
    }

    internal FirmwareCommand BuildWrapperCommand(string powerShell, string deviceId, FirmwareOperation operation, bool execute)
    {
        _ = DeviceName(deviceId); _ = OperationName(operation);
        var arguments = new List<string> { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Inside(WrapperPath(deviceId)), "-Mode", ScriptMode(operation) };
        if (operation == FirmwareOperation.Update) arguments.AddRange(["-UpdateRelease", FirmwareRelease.ReleaseId, "-UpdateFrom", "auto"]);
        if (operation == FirmwareOperation.InstallFromOriginal) arguments.AddRange(["-InstallRelease", FirmwareRelease.ReleaseId]);
        if (execute) arguments.Add("-Execute");
        return new(powerShell, arguments, _workspace, execute ? FirmwareCommandKind.Execute : FirmwareCommandKind.Plan);
    }

    private FirmwareCommand BuildIdentityCommand(string powerShell, FirmwareDeviceBinding device)
    {
        // Only literals from the hash-pinned local binding are interpolated; the grammar above excludes PowerShell syntax.
        var script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding; try { $items=@(Get-PnpDevice -PresentOnly | Where-Object {$_.InstanceId -like '" + device.BootloaderPrefix + "*'}); if($items.Count -ne 1 -or $items[0].InstanceId -cne '" + device.InstanceId + "' -or $items[0].Status -ne 'OK'){throw 'Der zuvor identifizierte Bootloader ist nicht eindeutig am bekannten Anschluss vorhanden.'}; $driver=Get-PnpDeviceProperty -InstanceId '" + device.InstanceId + "' -KeyName DEVPKEY_Device_Service; if($driver.Data -ne 'WinUSB'){throw 'Der geprüfte WinUSB-Treiber fehlt.'}; $locations=@((Get-PnpDeviceProperty -InstanceId '" + device.InstanceId + "' -KeyName DEVPKEY_Device_LocationPaths).Data); @{present=$true;instanceId=$items[0].InstanceId;service=$driver.Data;locationPaths=$locations}|ConvertTo-Json -Depth 3 -Compress } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        return new(powerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], _workspace, FirmwareCommandKind.IdentityCheck);
    }

    private static string[] ValidateIdentity(FirmwareProcessResult identity, FirmwareDeviceBinding device, bool requireLocationPaths = false)
    {
        if (identity.ExitCode != 0) throw new IOException("Der erwartete Bootloader ist nicht bereit. " + LastMessage(identity));
        using var json = ParseJson(identity.StandardOutput, "Bootloader-Prüfung ohne eindeutiges Ergebnis.");
        var root = json.RootElement;
        if (!Bool(root, "present") || Text(root, "instanceId") != device.InstanceId) throw new InvalidDataException("Bootloader-Identität stimmt nicht mit dem lokal geprüften Gerät überein.");
        if (!string.Equals(Text(root, "service"), "WinUSB", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Der geprüfte WinUSB-Treiber fehlt.");
        return requireLocationPaths ? ReadLocationPaths(root, required: true) : [];
    }

    private void ValidateUpdateSources(FirmwareDeviceBinding device)
    {
        string[] releases = [device.DeviceId == "pro-iso" ? "0.2-fn-macros" : "0.3-wb32-bulk", "0.4-effects", "0.5-color-sequences", "0.6-standby", "0.7-lighting-test", "0.8-version-reporting", "0.9-lighting-test", "0.9.1-lighting-fix"];
        var name = device.DeviceId == "pro-iso" ? "pro" : "numpad";
        if (device.UpdateSources is null || device.UpdateSources.Count != releases.Length || device.UpdateSources.Any(source => source is null) || !device.UpdateSources.Select(source => source.Release).Order().SequenceEqual(releases.Order()))
            throw new InvalidDataException("Die freigegebenen Ausgangsfirmware-Versionen fehlen oder sind mehrdeutig.");
        foreach (var source in device.UpdateSources)
        {
            if (source.Path != $"firmware/releases/{source.Release}/{name}.bin" || source.Bytes <= 0 || source.Bytes > FlashBytes(device.DeviceId) - 32768 || !Regex.IsMatch(source.Sha256 ?? "", @"\A[0-9A-F]{64}\z") || device.UpdateFiles.Count(file => file.Path == source.Path && file.Bytes == source.Bytes && file.Sha256 == source.Sha256) != 1)
                throw new InvalidDataException("Die Ausgangsfirmware stimmt nicht mit den lokalen Dateifreigaben überein.");
            CheckFile(new(source.Path, source.Bytes, source.Sha256!));
        }
    }

    private void ValidateUpdateMetadata(JsonElement root, FirmwareDeviceBinding device, FirmwareUpdateSource? selected)
    {
        ValidateUpdateSources(device);
        var next = device.UpdateFiles.Single(file => file.Path == device.NewCodePath);
        if (Text(root, "updateRelease") != FirmwareRelease.ReleaseId || Text(root, "updateFrom") != "auto" || Text(root, "updateToCodeSha256") != next.Sha256 || root.GetProperty(device.DeviceId == "pro-iso" ? "updateToCodeBytes" : "codeBytes").GetInt64() != next.Bytes)
            throw new InvalidDataException("Der Plan verwendet andere Firmware-Versionen.");
        var sources = root.GetProperty("updateSources");
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() != device.UpdateSources.Count) throw new InvalidDataException("Die Quellliste im Plan weicht von der lokalen Freigabe ab.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in sources.EnumerateArray())
        {
            var release = Text(item, "release");
            var source = device.UpdateSources.SingleOrDefault(source => source.Release == release);
            if (!seen.Add(release) || source is null || Text(item, "path") != source.Path || item.GetProperty("bytes").GetInt64() != source.Bytes || Text(item, "sha256") != source.Sha256)
                throw new InvalidDataException("Die Quellliste im Plan weicht von der lokalen Freigabe ab.");
        }
        if (selected is null)
        {
            foreach (var field in new[] { "updateFromRelease", "updateFromCodeBytes", "updateFromCodeSha256" })
                if (root.GetProperty(field).ValueKind != JsonValueKind.Null) throw new InvalidDataException("Der Plan darf keine ungelesene Ausgangsfirmware voraussetzen.");
        }
        else if (Text(root, "updateFromRelease") != selected.Release || root.GetProperty("updateFromCodeBytes").GetInt64() != selected.Bytes || Text(root, "updateFromCodeSha256") != selected.Sha256)
            throw new InvalidDataException("Die ausgewählte Ausgangsfirmware stimmt nicht mit den vollständigen Vorher-Abbildern überein.");
    }

    private void ValidatePlan(JsonElement root, FirmwareDeviceBinding device, FirmwareOperation operation, bool executionRequested = false)
        => ValidatePlanCore(root, device, operation, executionRequested, null);

    private void ValidatePlanCore(JsonElement root, FirmwareDeviceBinding device, FirmwareOperation operation, bool executionRequested, FirmwareUpdateSource? selectedSource)
    {
        var pro = device.DeviceId == "pro-iso";
        if (Text(root, "mode") != ScriptMode(operation) || Bool(root, pro ? "executionRequested" : "execute") != executionRequested || Text(root, pro ? "device" : "targetInstance") != device.InstanceId || root.GetProperty(pro ? "bytes" : "internalFlashBytes").GetInt32() != FlashBytes(device.DeviceId)) throw new InvalidDataException("Der Skriptplan passt nicht zum ausgewählten Gerät oder Vorgang.");
        if (pro && (Text(root, "port") != device.Port || root.GetProperty("alternate").GetInt32() != 0 || root.GetProperty("interface").GetInt32() != 0)) throw new InvalidDataException("Der Plan verwendet einen anderen Bootloader-Anschluss.");
        if (operation == FirmwareOperation.Update)
        {
            ValidateUpdateMetadata(root, device, selectedSource);
        }
        else if (operation == FirmwareOperation.InstallFromOriginal)
        {
            var code = device.InstallFiles.Single(file => file.Path == device.NewCodePath);
            if (Text(root, "installRelease") != FirmwareRelease.ReleaseId || Bool(root, "settingsPreserved") || Text(root, "installCodeSha256") != code.Sha256 || root.GetProperty("installCodeBytes").GetInt64() != code.Bytes || !SamePath(Text(root, pro ? "payload" : "image"), Inside(device.InstallPath)) || Text(root, pro ? "payloadSha256" : "sha256") != device.InstallHash) throw new InvalidDataException("Der Installationsplan verwendet nicht die freigegebene neue Firmware mit leeren Einstellungen.");
        }
        else if (!SamePath(Text(root, pro ? "payload" : "image"), Inside(device.StockPath)) || Text(root, pro ? "payloadSha256" : "sha256") != device.StockHash) throw new InvalidDataException("Der Wiederherstellungsplan verwendet ein anderes Originalabbild.");
    }

    // A launched wrapper is not proof that it wrote. Only this complete, tightly bounded
    // pre-write transcript can establish the narrower no-write outcome; everything else stays uncertain.
    internal bool VerifyNoWriteFailure(string directory, FirmwareDeviceBinding device, FirmwareOperation operation)
        => ProveNoWriteFailure(directory, device, operation, null) != FirmwareNoWriteReason.None;

    internal FirmwareNoWriteReason ProveNoWriteFailure(string directory, FirmwareDeviceBinding device, FirmwareOperation operation, string? expectedSourceRelease)
    {
        try
        {
            if (operation != FirmwareOperation.Update || !SamePath(Path.GetDirectoryName(directory) ?? "", RunRoot(device.DeviceId, operation))) return FirmwareNoWriteReason.None;
            AssertNoReparseAncestor(directory);
            if (Directory.EnumerateFileSystemEntries(directory).Any(path => Regex.IsMatch(Path.GetFileName(path), @"\A(?:after|download|write)(?:[-.]|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return FirmwareNoWriteReason.None;
            var pro = device.DeviceId == "pro-iso";
            JsonDocument ReadReport(string name)
            {
                var path = Path.Combine(directory, name); AssertNoReparseAncestor(path);
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Fehlerbericht fehlt oder ist zu groß.");
                return ParseJson(File.ReadAllText(path), "Ungültiger Fehlerbericht.");
            }
            using var report = ReadReport(pro ? "FAILED.json" : "result.json");
            var root = report.RootElement;
            if (Text(root, "mode") != "Update" || string.IsNullOrWhiteSpace(Text(root, "error"))) return FirmwareNoWriteReason.None;
            var size = FlashBytes(device.DeviceId);
            byte[] ReadCapture(int number)
            {
                var path = Path.Combine(directory, pro ? $"before-read-{number}.bin" : $"before-{number}.bin"); AssertNoReparseAncestor(path);
                if (new FileInfo(path).Length != size) throw new InvalidDataException("Unvollständiges Vorher-Abbild.");
                return File.ReadAllBytes(path);
            }
            var first = ReadCapture(1); var second = ReadCapture(2);
            if (!first.AsSpan().SequenceEqual(second)) return FirmwareNoWriteReason.None;
            // The user-facing reason is also proven from the bytes, not inferred from stderr.
            ValidateUpdateSources(device);
            var candidates = device.UpdateSources.Where(source => Hash(first.AsSpan(0, checked((int)source.Bytes))) == source.Sha256 && first.AsSpan(checked((int)source.Bytes), size - 32768 - checked((int)source.Bytes)).IndexOfAnyExcept((byte)255) < 0).ToArray();
            var selected = candidates.Length == 1 ? candidates[0] : null;
            var reason = selected is null ? FirmwareNoWriteReason.UnsupportedSource : expectedSourceRelease is not null && selected.Release != expectedSourceRelease ? FirmwareNoWriteReason.SourceChanged : FirmwareNoWriteReason.None;
            if (reason == FirmwareNoWriteReason.None) return reason;
            void CheckExpected(JsonElement value)
            {
                if (expectedSourceRelease is not null && (device.UpdateSources.Count(source => source.Release == expectedSourceRelease) != 1 || Text(value, "expectedSourceRelease") != expectedSourceRelease)) throw new InvalidDataException("Abbruchbericht passt nicht zur vorbereiteten Ausgangsversion.");
            }
            if (pro)
            {
                if (Text(root, "status") != "failed-review-required" || Bool(root, "writeAttempted") || File.Exists(Path.Combine(directory, "manifest.json"))) return FirmwareNoWriteReason.None;
                using var plan = ReadReport("plan.json"); ValidatePlan(plan.RootElement, device, operation, executionRequested: true); CheckExpected(plan.RootElement);
            }
            else
            {
                if (Bool(root, "firmwareChanged") || Bool(root, "readbackVerified") || Bool(root, "externalStorageWritten") || Bool(root, "protectionChanged") || Bool(root, "resetSent") || root.TryGetProperty("writeStartedUtc", out _)) return FirmwareNoWriteReason.None;
                ValidatePlanCore(root, device, operation, true, selected); CheckExpected(root);
            }
            return VerifyReadCommands(root.GetProperty("commands"), directory, device) ? reason : FirmwareNoWriteReason.None;
        }
        catch { return FirmwareNoWriteReason.None; }
    }

    private bool VerifyReadCommands(JsonElement commands, string directory, FirmwareDeviceBinding device)
    {
        var pro = device.DeviceId == "pro-iso";
        if (commands.ValueKind != JsonValueKind.Array || commands.GetArrayLength() != 3) return false;
        var proTool = "tools/InGlorious.Backup/vendor/dfu-util-0.11-binaries/win64/dfu-util-static.exe";
        var padReader = "tools/InGlorious.Backup/bin/Release/net9.0/InGlorious.Backup.exe";
        var padWriter = "tools/InGlorious.Flash/wb32-no-unprotect.exe";
        for (var index = 0; index < 3; index++)
        {
            var command = commands[index];
            var tool = pro ? proTool : index == 0 ? padReader : padWriter;
            if (device.BaseFiles.Count(pin => pin.Path == tool) != 1 || !SamePath(Text(command, "executable"), Inside(tool)) || command.GetProperty("exitCode").GetInt32() != 0 || Bool(command, "timedOut")) return false;
            var arguments = command.GetProperty("arguments").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            string[] expected;
            var capture = Path.Combine(directory, pro ? $"before-read-{index}.bin" : $"before-{index}.bin");
            if (pro)
            {
                if (Bool(command, "write") || Text(command, "name") != (index == 0 ? "before-descriptors" : $"before-read-{index}") || command.GetProperty("error").ValueKind != JsonValueKind.Null) return false;
                var match = PhysicalUsbId.Match(device.InstanceId);
                if (!match.Success) return false;
                string[] prefix = ["-d", match.Groups["vid"].Value.ToLowerInvariant() + ":" + match.Groups["pid"].Value.ToLowerInvariant(), "-S", device.InstanceId[(device.InstanceId.LastIndexOf('\\') + 1)..], "-p", device.Port, "-c", "1", "-i", "0", "-a", "0"];
                expected = index == 0 ? [.. prefix, "-l"] : [.. prefix, "-s", "0x08000000:262144", "-U", capture];
            }
            else expected = index == 0 ? ["info", "--read-only"] : ["-U", capture, "-Z", "131072"];
            if (index > 0)
            {
                var pathIndex = pro ? 15 : 1;
                if (arguments.Length != expected.Length || !SamePath(arguments[pathIndex], capture)) return false;
                arguments[pathIndex] = capture;
            }
            if (!arguments.SequenceEqual(expected, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    internal void VerifyEvidence(string directory, FirmwareDeviceBinding device, FirmwareOperation operation, string? expectedSourceRelease = null)
    {
        var runRoot = RunRoot(device.DeviceId, operation);
        if (!SamePath(Path.GetDirectoryName(directory) ?? "", runRoot)) throw new InvalidDataException("Der Prüfbericht liegt außerhalb des vorgesehenen Protokollordners.");
        AssertNoReparseAncestor(directory);
        var pro = device.DeviceId == "pro-iso";
        var reportPath = Path.Combine(directory, pro ? "manifest.json" : "result.json");
        AssertNoReparseAncestor(reportPath);
        if (!File.Exists(reportPath) || new FileInfo(reportPath).Length > 1024 * 1024) throw new InvalidDataException("Prüfbericht fehlt oder ist zu groß.");
        using var report = ParseJson(File.ReadAllText(reportPath), "Prüfbericht ist ungültig.");
        var root = report.RootElement;
        if (Text(root, "mode") != ScriptMode(operation) || Text(root, pro ? "device" : "targetInstance") != device.InstanceId) throw new InvalidDataException("Prüfbericht gehört zu einem anderen Vorgang oder Gerät.");
        if (pro ? Text(root, "status") != "two-readback-verified" || !Bool(root, "writeAttempted") || Bool(root, "optionBytesWritten") || Bool(root, "protectionChanged") || Bool(root, "automaticResetOrLeave") : !Bool(root, "readbackVerified") || !Bool(root, "firmwareChanged") || Bool(root, "externalStorageWritten") || Bool(root, "protectionChanged") || Bool(root, "resetSent")) throw new InvalidDataException("Das Skript hat keinen vollständig geprüften Vorgang gemeldet.");
        var size = FlashBytes(device.DeviceId);
        byte[] Capture(string phase, int number)
        {
            var path = Path.Combine(directory, pro ? $"{phase}-read-{number}.bin" : $"{phase}-{number}.bin");
            AssertNoReparseAncestor(path);
            if (!File.Exists(path) || new FileInfo(path).Length != size) throw new InvalidDataException("Ein vollständiges Vorher-/Nachher-Abbild fehlt.");
            return File.ReadAllBytes(path);
        }
        var first = Capture("before", 1); var second = Capture("before", 2);
        if (!first.AsSpan().SequenceEqual(second)) throw new InvalidDataException("Die beiden Vorher-Abbilder stimmen nicht überein.");
        byte[] expected;
        if (operation == FirmwareOperation.Update)
        {
            ValidateUpdateSources(device);
            var next = device.UpdateFiles.Single(file => file.Path == device.NewCodePath);
            var storage = size - 32768;
            var candidates = device.UpdateSources.Where(source => Hash(first.AsSpan(0, checked((int)source.Bytes))) == source.Sha256 && first.AsSpan(checked((int)source.Bytes), storage - checked((int)source.Bytes)).IndexOfAnyExcept((byte)255) < 0).ToArray();
            if (candidates.Length != 1 || next.Bytes <= 0 || next.Bytes > storage) throw new InvalidDataException("Vorher-Abbild enthält nicht genau eine freigegebene Ausgangsfirmware und leeren Zwischenraum.");
            if (expectedSourceRelease is not null && (candidates[0].Release != expectedSourceRelease || Text(pro ? root.GetProperty("update") : root, "expectedSourceRelease") != expectedSourceRelease)) throw new InvalidDataException("Die Ausgangsfirmware hat sich seit der Vorbereitung geändert.");
            ValidateUpdateMetadata(pro ? root.GetProperty("update") : root, device, candidates[0]);
            CheckFile(next);
            expected = new byte[size]; Array.Fill(expected, (byte)255);
            File.ReadAllBytes(Inside(next.Path)).CopyTo(expected, 0);
            first.AsSpan(storage).CopyTo(expected.AsSpan(storage));
            var preparedPath = Path.Combine(directory, "update-image.bin"); AssertNoReparseAncestor(preparedPath);
            if (!File.Exists(preparedPath) || new FileInfo(preparedPath).Length != size || !File.ReadAllBytes(preparedPath).AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Das vorbereitete Update enthält abweichende Code- oder Einstellungsbytes.");
        }
        else if (operation == FirmwareOperation.InstallFromOriginal)
        {
            if (Hash(first) != device.StockHash) throw new InvalidDataException("Installation benötigt zwei frische Abbilder des exakt gesicherten Originalzustands.");
            if (Text(root, "installRelease") != FirmwareRelease.ReleaseId || Bool(root, "settingsPreserved")) throw new InvalidDataException("Installationsbericht bestätigt keine neue Konfiguration mit Standardeinstellungen.");
            var code = device.InstallFiles.Single(file => file.Path == device.NewCodePath);
            if (code.Bytes <= 0 || code.Bytes > size - 32768) throw new InvalidDataException("Installationscode liegt außerhalb des vorgesehenen Flashbereichs.");
            CheckFile(code);
            expected = new byte[size]; Array.Fill(expected, (byte)255);
            File.ReadAllBytes(Inside(code.Path)).CopyTo(expected, 0);
            var image = device.InstallFiles.Single(file => file.Path == device.InstallPath);
            CheckFile(image);
            if (image.Bytes != size || image.Sha256 != device.InstallHash || Hash(expected) != device.InstallHash || !File.ReadAllBytes(Inside(image.Path)).AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Installationsabbild muss ausschließlich freigegebenen Code und FF bis zum Flashende enthalten.");
        }
        else
        {
            expected = File.ReadAllBytes(Inside(device.StockPath));
            if (expected.Length != size || Hash(expected) != device.StockHash) throw new InvalidDataException("Die ursprüngliche Sicherung stimmt nicht mehr mit der Freigabe überein.");
        }
        foreach (var number in new[] { 1, 2 }) if (!Capture("after", number).AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Eine vollständige Rücklesung weicht vom freigegebenen Flash-Abbild ab.");
        if (Text(root, pro ? "payloadSha256" : "afterSha256") != Hash(expected)) throw new InvalidDataException("Die Prüfsumme im Abschlussbericht stimmt nicht mit den Rücklesedaten überein.");
    }

    private string RunRoot(string deviceId, FirmwareOperation operation) => Inside(deviceId == "pro-iso" ? $"artifacts/install/{FirmwareRelease.ReleaseId}/pro-runs" : "artifacts/flash");
    private static string WrapperPath(string deviceId) => deviceId == "pro-iso" ? "scripts/Invoke-ProFirmware.ps1" : deviceId == "numpad" ? "scripts/Invoke-NumpadFirmware.ps1" : throw new InvalidDataException("Unbekanntes Gerät.");
    private string Inside(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Ein Firmware-Pfad liegt außerhalb des lokalen Arbeitsordners.");
        var path = Path.GetFullPath(Path.Combine(_workspace, relative));
        if (!path.StartsWith(_workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ein Firmware-Pfad verlässt den lokalen Arbeitsordner.");
        return path;
    }
    private void CheckFile(FirmwareFilePin pin)
    {
        var path = Inside(pin.Path); AssertNoReparseAncestor(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Erforderliche lokale Datei fehlt: " + pin.Path + ". Es wird nichts automatisch heruntergeladen.");
        using var stream = File.OpenRead(path);
        if (stream.Length != pin.Bytes || Convert.ToHexString(SHA256.HashData(stream)) != pin.Sha256) throw new InvalidDataException("Datei weicht von der geprüften Version ab: " + pin.Path);
    }
    private IDisposable LockArtifacts(IEnumerable<FirmwareFilePin> files)
    {
        var locks = new List<FileStream>();
        try { foreach (var file in files.DistinctBy(file => file.Path)) locks.Add(new(Inside(file.Path), FileMode.Open, FileAccess.Read, FileShare.Read)); return new FileLocks(locks); }
        catch { foreach (var stream in locks) stream.Dispose(); throw; }
    }
    private sealed class FileLocks(List<FileStream> streams) : IDisposable { public void Dispose() { foreach (var stream in streams) stream.Dispose(); } }
    private static void AssertNoReparseAncestor(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Verknüpfte Pfade sind für Firmware-Dateien nicht zugelassen.");
    }
    private static string FindPowerShell()
    {
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", "pwsh.exe") };
        return candidates.FirstOrDefault(path => File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileMajorPart >= 7) ?? throw new FileNotFoundException("PowerShell 7 wurde in den unterstützten lokalen Installationsordnern nicht gefunden. Die Firmware-Funktion bleibt gesperrt.");
    }
    private static JsonDocument ParseJson(string text, string error) { try { return JsonDocument.Parse(text); } catch (JsonException ex) { throw new InvalidDataException(error, ex); } }
    private static bool Bool(JsonElement root, string name) => root.GetProperty(name).GetBoolean();
    private static string Text(JsonElement root, string name) => root.GetProperty(name).GetString() ?? "";
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string LastMessage(FirmwareProcessResult result)
    {
        if (result.TimedOut) return "Windows-Gerätesuche dauerte zu lange.";
        return FirmwareMessages.ExtractDiagnosticText(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
    }

    private async Task<FirmwareProcessResult> RunProcessAsync(FirmwareCommand command)
    {
        try
        {
            // Process output is diagnostic data, never a progress message. The UI receives only Report calls.
            var result = await _runner.RunAsync(command, null);
            SaveDiagnostic(command.Kind.ToString(), command, result);
            return result;
        }
        catch (Exception ex) { SaveDiagnostic(command.Kind.ToString(), command, error: ex); throw; }
    }

    private static void Report(IProgress<string>? log, string message)
    {
        try { log?.Report(message); }
        catch { /* A display failure must not change a firmware outcome or trigger another operation. */ }
    }

    private void SaveDiagnostic(string kind, FirmwareCommand? command = null, FirmwareProcessResult? result = null, Exception? error = null)
    {
        try
        {
            var content = JsonSerializer.Serialize(new { kind, command, result, error = error?.ToString() }, new JsonSerializerOptions { WriteIndented = true });
            var hash = Hash(Encoding.UTF8.GetBytes(content));
            lock (_diagnosticSync)
            {
                if (_lastDiagnosticHashes.TryGetValue(kind, out var previous) && previous == hash) return;
                var directory = Path.Combine(_evidenceRoot, "Diagnose");
                AssertNoReparseAncestor(directory);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + kind + "-" + Guid.NewGuid().ToString("N") + ".json");
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
                DiagnosticDirectory = directory;
                _lastDiagnosticHashes[kind] = hash;
            }
        }
        catch { /* Diagnostic storage must never change a verified result or retry an operation. */ }
    }
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record FirmwareEnvironment(FirmwareDeviceBinding Device, string PowerShell, IReadOnlyList<FirmwareFilePin> Files);
}

internal sealed record FirmwareFilePin(string Path, long Bytes, string Sha256);
internal sealed record FirmwareUpdateSource(string Release, string Path, long Bytes, string Sha256);
internal sealed class FirmwareBindings { public int SchemaVersion { get; set; } public List<FirmwareFilePin> Common { get; set; } = []; public List<FirmwareDeviceBinding> Devices { get; set; } = []; }
internal sealed class FirmwareDeviceBinding
{
    public string DeviceId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string BootloaderPrefix { get; set; } = "";
    public string Port { get; set; } = "";
    public string StockPath { get; set; } = "";
    public string StockHash { get; set; } = "";
    public string NewCodePath { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string InstallHash { get; set; } = "";
    public List<FirmwareFilePin> BaseFiles { get; set; } = [];
    public List<FirmwareFilePin> UpdateFiles { get; set; } = [];
    public List<FirmwareUpdateSource> UpdateSources { get; set; } = [];
    public List<FirmwareFilePin> InstallFiles { get; set; } = [];
}
internal enum FirmwareCommandKind { Plan, IdentityCheck, NormalConnectionCheck, Execute }
internal sealed record FirmwareCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, FirmwareCommandKind Kind);
internal sealed record FirmwareProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false);
internal interface IFirmwareProcessRunner { Task<FirmwareProcessResult> RunAsync(FirmwareCommand command, IProgress<string>? log); }
internal sealed class FirmwareProcessRunner : IFirmwareProcessRunner
{
    internal static bool IsReadOnlyQuery(FirmwareCommandKind kind) => kind is FirmwareCommandKind.IdentityCheck or FirmwareCommandKind.NormalConnectionCheck;
    internal static ProcessStartInfo BuildStartInfo(FirmwareCommand command)
    {
        var start = new ProcessStartInfo(command.Executable) { WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        return start;
    }
    public async Task<FirmwareProcessResult> RunAsync(FirmwareCommand command, IProgress<string>? log)
    {
        var start = BuildStartInfo(command);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("PowerShell konnte nicht gestartet werden.");
        async Task<string> Drain(StreamReader reader)
        {
            var output = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer)) != 0)
                if (output.Length < 4 * 1024 * 1024) output.Append(buffer, 0, Math.Min(count, 4 * 1024 * 1024 - output.Length));
            return output.ToString();
        }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        if (IsReadOnlyQuery(command.Kind))
        {
            // Only this PnP-only query may be stopped. A stalled query must not block later polling forever.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
                return new(-1, await stdout, await stderr, TimedOut: true);
            }
        }
        else
        {
            // The wrapper owns its bounded operation. This host never kills, cancels or retries a flash process.
            await process.WaitForExitAsync();
        }
        return new(process.ExitCode, await stdout, await stderr);
    }
}
