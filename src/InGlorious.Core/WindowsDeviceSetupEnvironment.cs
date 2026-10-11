using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InGlorious.Core;

internal sealed class WindowsDeviceSetupEnvironment : IDeviceSetupEnvironment
{
    internal const string DiscoveryScript = """
        $ErrorActionPreference='Stop'
        $ProgressPreference='SilentlyContinue'
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $result=@(Get-PnpDevice -PresentOnly | Where-Object {
            $_.InstanceId -match '\AUSB\\VID_(320F|342D|0483|FEED)&PID_[0-9A-F]{4}\\[^\\]+\z'
        } | ForEach-Object {
            $device=$_
            $properties=@(Get-PnpDeviceProperty -InstanceId $device.InstanceId)
            $null=$device.InstanceId -match 'VID_([0-9A-F]{4})&PID_([0-9A-F]{4})'
            $vid=[Convert]::ToUInt16($Matches[1],16)
            $deviceProductId=[Convert]::ToUInt16($Matches[2],16)
            $name=($properties|Where-Object KeyName -eq 'DEVPKEY_Device_BusReportedDeviceDesc').Data
            $locations=@(($properties|Where-Object KeyName -eq 'DEVPKEY_Device_LocationPaths').Data)
            $service=($properties|Where-Object KeyName -eq 'DEVPKEY_Device_Service').Data
            if($device.Status -ne 'OK'){$service='Unavailable'}
            [pscustomobject]@{instanceId=[string]$device.InstanceId;vendorId=$vid;productId=$deviceProductId;name=[string]$name;service=[string]$service;locationPaths=$locations}
        })
        ConvertTo-Json -InputObject $result -Depth 4 -Compress
        """;
    public async Task<IReadOnlyList<SetupUsbNode>> ScanAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new SetupException(L.T("Die Firmware-Einrichtung ist derzeit unter Windows verfügbar."));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(DiscoveryScript)));
        using var process = Process.Start(start) ?? throw new IOException("USB discovery failed.");
        var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw new IOException("USB discovery timed out."); }
        var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0 || output.Length > 1024 * 1024) throw new IOException("USB discovery failed.");
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid USB inventory.");
        var result = new List<SetupUsbNode>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var instance = item.GetProperty("instanceId").GetString()!;
            var vid = item.GetProperty("vendorId").GetUInt16(); var pid = item.GetProperty("productId").GetUInt16();
            if (!DeviceScanner.IsPhysicalUsbInstanceId(instance) || !instance.StartsWith($"USB\\VID_{vid:X4}&PID_{pid:X4}\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Inconsistent USB inventory.");
            var locations = item.GetProperty("locationPaths").EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).Where(value => value.Length is > 0 and < 4096).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            result.Add(new(instance, vid, pid, item.GetProperty("name").GetString() ?? "", item.GetProperty("service").GetString() ?? "", locations));
        }
        if (result.Select(node => node.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new InvalidDataException("Duplicate USB inventory.");
        return result;
    }
    public Task<bool?> IsCoreRunningAsync() => Task.FromResult(FirmwareService.IsGloriousCoreRunning());

    public async Task<JsonElement> InvokeAdapterAsync(IReadOnlyDictionary<string, object?> request, string evidenceDirectory)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Tools", "FirmwareAdapter");
        DeviceSetupService.AssertNoLinks(root);
        var locks = new List<FileStream>();
        try
        {
            // This resource is generated from the reviewed adapter publish, independently of device data.
            var assembly = typeof(WindowsDeviceSetupEnvironment).Assembly;
            var resource = assembly.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith(".SetupToolCatalog.json", StringComparison.Ordinal));
            if (resource is null) throw new SetupException(L.T("Die Firmware-Werkzeuge fehlen. Installiere die vollständige App erneut."));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var manifest = JsonDocument.Parse(stream);
            var expectedFiles = manifest.RootElement.GetProperty("files").EnumerateArray()
                .Select(item => item.GetProperty("path").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
            if (actualFiles.Length != expectedFiles.Count || !actualFiles.All(expectedFiles.Contains))
                throw new SetupException(L.T("Die Firmware-Werkzeuge sind beschädigt. Installiere die App erneut."));
            foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var path = DeviceSetupService.ContainedFile(root, item.GetProperty("path").GetString()!);
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                locks.Add(file);
                if (file.Length != item.GetProperty("bytes").GetInt64() ||
                    Convert.ToHexString(SHA256.HashData(file)) != item.GetProperty("sha256").GetString())
                    throw new SetupException(L.T("Die Firmware-Werkzeuge sind beschädigt. Installiere die App erneut."));
            }
            var callId = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(evidenceDirectory, "request-" + callId + ".json");
            var responsePath = Path.Combine(evidenceDirectory, "response-" + callId + ".json");
            DeviceSetupService.AssertNoLinks(evidenceDirectory);
            using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(request)); file.Flush(true); }
            var start = new ProcessStartInfo(Path.Combine(root, "InGlorious.FirmwareAdapter.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add("--response"); start.ArgumentList.Add(responsePath);
            using var process = Process.Start(start) ?? throw new IOException("Firmware adapter could not start.");
            // The adapter bounds each native operation itself. Never kill an in-progress flash on a UI timer.
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var outText = await stdout; var errText = await stderr;
            if (outText.Length + errText.Length > 4 * 1024 * 1024) throw new IOException("Firmware adapter output exceeded its limit.");
            DeviceSetupService.AssertNoLinks(responsePath);
            if (!File.Exists(responsePath) || new FileInfo(responsePath).Length > 1024 * 1024) throw new IOException("Firmware adapter returned no usable result.");
            using var response = JsonDocument.Parse(File.ReadAllBytes(responsePath));
            if (response.RootElement.GetProperty("schema").GetInt32() != 1 ||
                response.RootElement.GetProperty("operation").GetString() != (string)request["operation"]!) throw new InvalidDataException("Firmware adapter operation mismatch.");
            if (process.ExitCode != 0 && response.RootElement.GetProperty("status").GetString() is "ok" or "verified")
                throw new InvalidDataException("Firmware adapter status mismatch.");
            return response.RootElement.Clone();
        }
        finally { foreach (var item in locks) item.Dispose(); }
    }
}
