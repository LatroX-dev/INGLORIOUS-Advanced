using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InGlorious.Core;

public record DetectedDevice(string DeviceId, string Name, string VendorId, string ProductId, string UsbRelease, string InstanceId, string LayoutEvidence, string RevisionStatus)
{
    public bool IsSupported => int.TryParse(VendorId, System.Globalization.NumberStyles.HexNumber, null, out var vid) &&
        int.TryParse(ProductId, System.Globalization.NumberStyles.HexNumber, null, out var pid) &&
        ((BoardDescriptorRegistry.TryGetStockUsb(vid, pid, out var stock) && stock.DeviceId == DeviceId) ||
         (BoardDescriptorRegistry.TryGetRuntimeUsb(vid, pid, out var runtime) && runtime.DeviceId == DeviceId));
}

/// <summary>Reads Windows PnP properties only. Never opens a HID handle or sends a report.</summary>
public static class DeviceScanner
{
    // Physical USB devnodes only; composite MI interfaces and HID children are excluded.
    // This is a device-class filter, never a model, serial-number or stored-port allowlist.
    internal const string PresentUsbInstanceIdsScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $result = @(Get-PnpDevice -PresentOnly | Where-Object {
            $_.InstanceId -match '\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&[A-Z0-9_]+)*\\[^\\\r\n\x00]+\z' -and
            $_.InstanceId -notmatch '\AUSB\\[^\\]*&MI_'
        } | ForEach-Object { [string]$_.InstanceId })
        ConvertTo-Json -InputObject $result -Depth 2 -Compress
        """;

    public static async Task<IReadOnlyList<string>> ScanPresentUsbInstanceIdsAsync() =>
        ParsePresentUsbInstanceIds(await RunPnpScriptAsync(PresentUsbInstanceIdsScript));

    internal static bool IsPhysicalUsbInstanceId(string? instanceId) =>
        instanceId is { Length: > 0 and <= 4096 } &&
        Regex.IsMatch(instanceId, @"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&[A-Z0-9_]+)*\\[^\\\r\n\x00]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !Regex.IsMatch(instanceId, @"\AUSB\\[^\\]*&MI_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> ParsePresentUsbInstanceIds(string output)
    {
        using var json = JsonDocument.Parse(output);
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Windows-Gerätesuche lieferte keine gültige Geräteliste.");
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !IsPhysicalUsbInstanceId(item.GetString()))
                throw new InvalidDataException("Windows-Gerätesuche lieferte eine ungültige USB-Gerätekennung.");
            results.Add(item.GetString()!);
        }
        return results.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<IReadOnlyList<DetectedDevice>> ScanAsync()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $result = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match '^USB\\(VID_320F&PID_(5046|5093|5088)|VID_FEED&PID_1C0[1-9A-F])\\' } | ForEach-Object {
                $device = $_
                $properties = Get-PnpDeviceProperty -InstanceId $device.InstanceId
                $desc = ($properties | Where-Object KeyName -eq 'DEVPKEY_Device_BusReportedDeviceDesc').Data
                $hardware = ($properties | Where-Object KeyName -eq 'DEVPKEY_Device_HardwareIds').Data
                $rev = 'unknown'
                foreach ($item in $hardware) { if ($item -match 'REV_([0-9A-Fa-f]{4})') { $rev = $Matches[1]; break } }
                $null = $device.InstanceId -match 'VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})'
                [pscustomobject]@{ name = [string]$desc; vendorId = $Matches[1]; productId = $Matches[2]; release = $rev; instanceId = $device.InstanceId }
            })
            ConvertTo-Json -InputObject $result -Depth 4 -Compress
            """;
        var output = await RunPnpScriptAsync(script);
        return ParseDevices(output);
    }

    internal static IReadOnlyList<DetectedDevice> ParseDevices(string output)
    {
        using var json = JsonDocument.Parse(output);
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Windows-Gerätesuche lieferte keine gültige Geräteliste.");
        var results = new List<DetectedDevice>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var pid = item.GetProperty("productId").GetString()!.ToUpperInvariant();
            var vid = item.GetProperty("vendorId").GetString()!.ToUpperInvariant();
            if (!ushort.TryParse(vid, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var vendor) ||
                !ushort.TryParse(pid, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var product))
                throw new InvalidDataException("Windows-Gerätesuche lieferte eine ungültige USB-Gerätekennung.");
            var instance = item.GetProperty("instanceId").GetString()!;
            if (!IsPhysicalUsbInstanceId(instance) || !instance.StartsWith($"USB\\VID_{vid}&PID_{pid}\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Windows-Gerätesuche lieferte eine ungültige USB-Gerätekennung.");
            if (!BoardDescriptorRegistry.TryGetRuntimeUsb(vendor, product, out var board) && !BoardDescriptorRegistry.TryGetStockUsb(vendor, product, out board))
            {
                // Recognized vendor candidate only. No editor alias, protocol board or flash permission.
                if (vendor == 0x320F && product == 0x5093)
                    results.Add(new("unverified-usb-320f-5093", "Glorious USB 320F:5093", vid, pid, item.GetProperty("release").GetString()!, instance,
                        "USB-Kennung erkannt; Platinenvariante nicht freigegeben.", "Die USB-ID allein bestimmt die Platinenrevision nicht; siehe gerätebezogenen Sicherungsbericht."));
                continue;
            }
            var id = board.DeviceId;
            results.Add(new(id, DeviceCatalog.Get(id).Name, vid, pid, item.GetProperty("release").GetString()!, item.GetProperty("instanceId").GetString()!, vid == "FEED" ? "INGLORIOUS-USB-Kennung erkannt; Protokollprüfung erfolgt im Übertragungsdialog." : "USB-ID und installierte CORE-Gerätedaten stimmen überein.", "Die USB-ID allein bestimmt die Platinenrevision nicht; siehe gerätebezogenen Sicherungsbericht."));
        }
        return results;
    }

    private static async Task<string> RunPnpScriptAsync(string script)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var start = new ProcessStartInfo(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new IOException("Windows-Gerätesuche konnte nicht gestartet werden.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);
            throw new IOException("Windows-Gerätesuche dauerte zu lange. Bitte erneut versuchen.");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new IOException("Windows-Gerätesuche fehlgeschlagen: " + error[..Math.Min(300, error.Length)]);
        return output;
    }
}

internal enum UsbConnectionState { Unknown, Connected, Disconnected }

/// <summary>In-memory observation of one runtime-selected devnode, not a firmware identity or permission.</summary>
internal sealed class UsbConnectionTracker
{
    private readonly string _vendorProductPrefix;
    internal UsbConnectionTracker(string instanceId)
    {
        if (!DeviceScanner.IsPhysicalUsbInstanceId(instanceId)) throw new InvalidDataException("Keine gültige physische USB-Gerätekennung ausgewählt.");
        InstanceId = instanceId;
        _vendorProductPrefix = Regex.Match(instanceId, @"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Value;
    }

    internal string InstanceId { get; }
    internal UsbConnectionState State { get; private set; } = UsbConnectionState.Unknown;
    internal bool ObservedConnected { get; private set; }

    internal UsbConnectionState ObserveSnapshot(IReadOnlyList<string> instanceIds)
    {
        // Reject the whole malformed snapshot before testing absence. Silently skipping entries
        // could turn a query/format failure into an incorrect physical-disconnection claim.
        if (instanceIds is null || instanceIds.Any(id => !DeviceScanner.IsPhysicalUsbInstanceId(id))) return ObserveFailure();
        if (instanceIds.Contains(InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            ObservedConnected = true;
            State = UsbConnectionState.Connected;
        }
        else State = ObservedConnected && !instanceIds.Any(id => id.StartsWith(_vendorProductPrefix, StringComparison.OrdinalIgnoreCase))
            ? UsbConnectionState.Disconnected : UsbConnectionState.Unknown;
        return State;
    }

    internal UsbConnectionState ObserveFailure() => State = UsbConnectionState.Unknown;
}
