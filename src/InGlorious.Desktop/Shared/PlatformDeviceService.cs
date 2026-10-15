using System.Security.Cryptography;
using System.Text.Json;
using HidSharp;

namespace InGlorious.Core;

public sealed record VerifiedDeviceProject(AppProject Project, byte[] Payload, OnboardInfo Info);
public sealed record PortableTransferResult(OnboardTransferResult Transfer, string? UsbNotice);

/// <summary>Presence discovery reads USB metadata only. Configuration operations use the separately verified RAW-HID transport.</summary>
public static class PlatformDeviceService
{
    public static string? ConnectedModelFromUsbMetadata(int vendorId, int productId)
    {
        if (BoardDescriptorRegistry.TryGetRuntimeUsb(vendorId, productId, out var runtime)) return runtime.DeviceId;
        // Stock firmware is a setup candidate, not an editable INGLORIOUS runtime.
        return null;
    }

    public static Task<IReadOnlyList<string>> FindConnectedModelsAsync() => Task.Run<IReadOnlyList<string>>(() =>
        DeviceList.Local.GetHidDevices()
            .Select(device => ConnectedModelFromUsbMetadata(device.VendorID, device.ProductID))
            .Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray());

    public static Task<IReadOnlyList<OnboardDevice>> FindAsync() => Task.Run(OnboardClient.FindDevices);
    public static async Task<OnboardInfo> GetInfoAsync(OnboardDevice device)
    {
        _ = BoardDescriptorRegistry.Get(device.DeviceId);
        await using var client = OnboardClient.Open(device);
        return await client.GetInfoAsync();
    }
    public static async Task<OnboardExtensions?> GetExtensionsAsync(OnboardDevice device)
    {
        _ = BoardDescriptorRegistry.Get(device.DeviceId);
        await using var client = OnboardClient.Open(device);
        return await ReadExtensionsAsync(client);
    }
    private static async Task<OnboardExtensions?> ReadExtensionsAsync(OnboardClient client)
    {
        try { return await client.GetExtensionsAsync(); }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { return null; }
    }
    public static string? UnsupportedReason(OnboardInfo info, OnboardImage image, OnboardExtensions? extensions = null)
    {
        if (!BoardDescriptorRegistry.TryGet(info.DeviceId, out var board) || !BoardDescriptorRegistry.TryGet(image.DeviceId, out _)) return "Unbekanntes Gerätemodell.";
        if (board.IsEngineeringOnly) return "Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar.";
        if (info.DeviceId != image.DeviceId) return "Das Profil passt nicht zum ausgewählten Gerät.";
        if (image.RequiresScreensaver && extensions?.SupportsScreensaver != true) return "Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen.";
        if (image.RequiresStandby && !info.SupportsStandby) return "Standby benötigt passende INGLORIOUS-Firmware.";
        if (image.RequiresColorSequences && !info.SupportsColorSequences) return "Eigene Farbfolgen benötigen ein Firmware-Update. Es wird nichts übertragen.";
        if (image.SchemaVersion >= 2 && !info.SupportsEffects && !info.SupportsTiming) return "Die installierte Firmware unterstützt dieses Konfigurationsformat nicht. Ein Firmware-Update ist erforderlich.";
        if (image.RequiresEffects && !info.SupportsEffects) return "Die installierte Firmware unterstützt keine Beleuchtungseffekte. Für diese Konfiguration ist ein Firmware-Update erforderlich.";
        if (image.RequiresTiming && !info.SupportsTiming) return "Die installierte Firmware unterstützt keine geänderten USB- oder Entprellzeiten. Für diese Konfiguration ist ein Firmware-Update erforderlich.";
        return null;
    }
    public static async Task<VerifiedDeviceProject> ReadAsync(OnboardDevice device)
    {
        _ = BoardDescriptorRegistry.Get(device.DeviceId);
        await using var client = OnboardClient.Open(device);
        return await ReadWithClient(client, device.DeviceId);
    }
    private static async Task<VerifiedDeviceProject> ReadWithClient(OnboardClient client, string expected)
    {
        var before = await client.GetInfoAsync();
        var bytes = await client.ReadAsync();
        var after = await client.GetInfoAsync();
        if (before != after || before.DeviceId != expected || before.ActiveCrc != OnboardProtocol.Crc32(bytes))
            throw new IOException(L.T("Der Gerätestand hat sich beim Lesen geändert. Bitte erneut lesen."));
        var project = OnboardSerializer.Decode(bytes);
        if (project.DeviceId != expected) throw new InvalidDataException(L.T("Die gelesene Konfiguration passt nicht zum gewählten Gerät."));
        return new(project, bytes, after);
    }
    public static async Task<PortableTransferResult> ApplyAsync(OnboardDevice device, OnboardImage image, IProgress<int>? progress, Action markWriteStarted)
    {
        _ = BoardDescriptorRegistry.Get(device.DeviceId);
        _ = BoardDescriptorRegistry.Get(image.DeviceId);
        if (device.DeviceId != image.DeviceId) throw new InvalidDataException(L.T("Das Profil passt nicht zum ausgewählten Gerät."));
        await using var client = OnboardClient.Open(device);
        var backup = await ReadWithClient(client, device.DeviceId);
        var extensions = image.RequiresScreensaver ? await ReadExtensionsAsync(client) : null;
        if (UnsupportedReason(backup.Info, image, extensions) is { } unsupported) throw new FirmwareUpdateRequiredException(L.T(unsupported));
        if (!backup.Info.Persistent) throw new IOException("Persistent device storage is unavailable.");
        var folder = Path.Combine(Paths.DataRoot, "device-backups"); Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, device.DeviceId + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
        await using (var stream = new FileStream(stem + ".bin", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await stream.WriteAsync(backup.Payload); stream.Flush(true); }
        if (!(await File.ReadAllBytesAsync(stem + ".bin")).AsSpan().SequenceEqual(backup.Payload)) throw new IOException("Device backup verification failed.");
        ProjectService.Save(stem + ".json", backup.Project);
        // Metadata is diagnostic; the durable, byte-verified raw image above is the lossless backup.
        await File.WriteAllTextAsync(stem + ".audit.json", JsonSerializer.Serialize(new { device.DeviceId, backup.Info.Generation, backup.Info.ActiveCrc, Sha256 = Convert.ToHexString(SHA256.HashData(backup.Payload)) }));
        markWriteStarted();
        var result = await client.ApplyAsync(image, progress, expectedGeneration: backup.Info.Generation);
        var finalInfo = await client.GetInfoAsync();
        if (!finalInfo.Persistent || finalInfo.Generation != result.Generation || finalInfo.ActiveCrc != result.Crc32)
            throw new IOException(L.T("Der gespeicherte Zustand wurde nicht bestätigt."));
        string? usbNotice = null;
        if (result.ReadbackVerified && backup.Info.SupportsTiming)
        {
            try
            {
                await client.ReconnectUsbAsync(result.Generation, result.Crc32);
            }
            catch (Exception ex)
            {
                // The configuration readback already succeeded. Do not relabel a reconnect failure as a failed COMMIT.
                System.Diagnostics.Trace.TraceError("Configuration saved, USB reconnect not confirmed: {0}", ex);
                usbNotice = L.T("Bitte verbinde das USB-Kabel erneut, damit die neue Abfragerate aktiv wird.");
            }
        }
        return new(result, usbNotice);
    }
}

public sealed class FirmwareUpdateRequiredException(string message) : IOException(message);
