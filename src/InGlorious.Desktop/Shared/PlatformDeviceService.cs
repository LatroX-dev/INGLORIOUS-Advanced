using System.Security.Cryptography;
using System.Text.Json;
using HidSharp;

namespace InGlorious.Core;

public sealed record VerifiedDeviceProject(AppProject Project, byte[] Payload, OnboardInfo Info,
    OnboardHardwareProfileSnapshot? ProfileSnapshot = null);
public sealed record PortableTransferResult(OnboardTransferResult? Transfer, string? UsbNotice,
    OnboardHardwareProfileSnapshot? ProfileSnapshot = null)
{
    public bool ReadbackVerified => ProfileSnapshot is not null || Transfer?.ReadbackVerified == true;
}

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
    public static async Task<OnboardKnobStatus?> GetKnobStatusAsync(OnboardDevice device, CancellationToken cancellationToken = default)
    {
        _ = BoardDescriptorRegistry.Get(device.DeviceId);
        await using var client = OnboardClient.Open(device);
        return await client.GetKnobStatusAsync(cancellationToken);
    }
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
        if (image.RequiresKnobCycle && extensions?.SupportsKnobCycle != true || image.RequiresKnobOrder && extensions?.SupportsKnobOrder != true || image.RequiresKnobPress && extensions?.SupportsKnobPress != true) return "Diese Drehregler-Einstellungen benötigen ein Firmware-Update. Es wird nichts übertragen.";
        if (image.RequiresScreensaver && extensions?.SupportsScreensaver != true) return "Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen.";
        if (image.RequiresStandby && !info.SupportsStandby) return "Standby benötigt passende INGLORIOUS-Firmware.";
        if (image.RequiresColorSequences && !info.SupportsColorSequences) return "Eigene Farbfolgen benötigen ein Firmware-Update. Es wird nichts übertragen.";
        if (image.SchemaVersion >= 2 && !info.SupportsEffects && !info.SupportsTiming) return "Die installierte Firmware unterstützt dieses Konfigurationsformat nicht. Ein Firmware-Update ist erforderlich.";
        if (image.RequiresEffects && !info.SupportsEffects) return "Die installierte Firmware unterstützt keine Beleuchtungseffekte. Für diese Konfiguration ist ein Firmware-Update erforderlich.";
        if (image.RequiresTiming && !info.SupportsTiming) return "Die installierte Firmware unterstützt keine geänderten USB- oder Entprellzeiten. Für diese Konfiguration ist ein Firmware-Update erforderlich.";
        return null;
    }
    public static async Task<VerifiedDeviceProject> ReadAsync(OnboardDevice device, OnboardProfileBinding? binding = null)
    {
        _ = BoardDescriptorRegistry.Get(device.DeviceId);
        await using var client = OnboardClient.Open(device);
        return await ReadWithClient(client, device.DeviceId, binding);
    }
    private static async Task<VerifiedDeviceProject> ReadWithClient(OnboardClient client, string expected, OnboardProfileBinding? binding = null)
    {
        var before = await client.GetInfoAsync();
        var hardware = await client.GetHardwareProfileStatusAsync(binding?.Slot ?? 0);
        if (hardware is not null)
        {
            if (binding is not null && binding.DeviceId != expected || hardware.UploadOpen ||
                binding is null && (hardware.ActiveSlot == 0 || hardware.SwitchPending))
                throw new IOException(L.T("Das Profil auf der Tastatur hat sich geändert. Bitte erneut auslesen."));
            byte slot = binding?.Slot ?? hardware.ActiveSlot;
            var profile = await client.ReadHardwareProfileAsync(slot);
            if (binding is not null && profile.InstallationId != binding.InstallationId)
                throw new IOException(L.T("Die gelesene Konfiguration gehört zu einem anderen Gerät."));
            var current = await client.GetHardwareProfileStatusAsync(binding?.Slot ?? 0);
            if (current is null || current.UploadOpen || current.Slot != slot ||
                current.Generation != profile.Status.Generation || current.Crc32 != profile.Status.Crc32 ||
                binding is null && (current.SwitchPending || current.ActiveSlot != slot) ||
                await client.GetInstallationIdAsync() != profile.InstallationId)
                throw new IOException(L.T("Das Profil auf der Tastatur hat sich geändert. Bitte erneut auslesen."));
            var profileProject = OnboardSerializer.Decode(profile.Payload);
            if (profileProject.DeviceId != expected) throw new InvalidDataException(L.T("Die gelesene Konfiguration passt nicht zum gewählten Gerät."));
            // INFO still describes the live active profile; preserve it honestly.
            // Bank consumers use ProfileSnapshot for slot/generation/CRC instead.
            return new(profileProject, profile.Payload, before, profile);
        }
        if (binding is not null) throw new IOException(L.T("Das Profil auf der Tastatur hat sich geändert. Bitte erneut auslesen."));
        var bytes = await client.ReadAsync();
        var after = await client.GetInfoAsync();
        if (before != after || before.DeviceId != expected || before.ActiveCrc != OnboardProtocol.Crc32(bytes))
            throw new IOException(L.T("Der Gerätestand hat sich beim Lesen geändert. Bitte erneut lesen."));
        var project = OnboardSerializer.Decode(bytes);
        if (project.DeviceId != expected) throw new InvalidDataException(L.T("Die gelesene Konfiguration passt nicht zum gewählten Gerät."));
        return new(project, bytes, after);
    }
    public static async Task<PortableTransferResult> ApplyAsync(OnboardDevice device, OnboardImage image, IProgress<int>? progress,
        Action markWriteStarted, OnboardProfileBinding? profileBinding = null)
    {
        _ = BoardDescriptorRegistry.Get(device.DeviceId);
        _ = BoardDescriptorRegistry.Get(image.DeviceId);
        if (device.DeviceId != image.DeviceId) throw new InvalidDataException(L.T("Das Profil passt nicht zum ausgewählten Gerät."));
        await using var client = OnboardClient.Open(device);
        image = image with { Payload = image.Payload.ToArray() };
        var backup = await ReadWithClient(client, device.DeviceId, profileBinding);
        if (backup.ProfileSnapshot is { } previousProfile && (profileBinding is null || previousProfile.Binding != profileBinding))
            throw new IOException(L.T("Das Profil auf der Tastatur hat sich geändert. Bitte erneut auslesen."));
        var extensions = image.RequiresScreensaver || image.RequiresKnobCycle ? await ReadExtensionsAsync(client) : null;
        if (UnsupportedReason(backup.Info, image, extensions) is { } unsupported) throw new FirmwareUpdateRequiredException(L.T(unsupported));
        if (!(backup.ProfileSnapshot?.Status.StorageHealthy ?? backup.Info.Persistent)) throw new IOException("Persistent device storage is unavailable.");
        var folder = Path.Combine(Paths.DataRoot, "device-backups"); Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, device.DeviceId + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
        await using (var stream = new FileStream(stem + ".bin", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await stream.WriteAsync(backup.Payload); stream.Flush(true); }
        if (!(await File.ReadAllBytesAsync(stem + ".bin")).AsSpan().SequenceEqual(backup.Payload)) throw new IOException("Device backup verification failed.");
        ProjectService.Save(stem + ".json", backup.Project);
        // Metadata is diagnostic; the durable, byte-verified raw image above is the lossless backup.
        await File.WriteAllTextAsync(stem + ".audit.json", JsonSerializer.Serialize(new { device.DeviceId,
            LegacyGeneration = backup.ProfileSnapshot is null ? (uint?)backup.Info.Generation : null,
            PayloadCrc32 = backup.ProfileSnapshot?.Status.Crc32 ?? backup.Info.ActiveCrc,
            HardwareProfile = backup.ProfileSnapshot?.Binding, Sha256 = Convert.ToHexString(SHA256.HashData(backup.Payload)) }));
        markWriteStarted();
        if (backup.ProfileSnapshot is not null)
        {
            var applied = await client.ApplyHardwareProfileAsync(image, profileBinding!, progress, requireActiveSlot: true);
            var status = await client.GetHardwareProfileStatusAsync(profileBinding!.Slot);
            if (!applied.ReadbackVerified || status is null || status.Bind(applied.Binding.InstallationId) != applied.Binding ||
                await client.GetInstallationIdAsync() != applied.Binding.InstallationId ||
                status.Crc32 != OnboardProtocol.Crc32(image.Payload))
                throw new IOException(L.T("Der gespeicherte Zustand wurde nicht bestätigt."));
            var saved = new OnboardHardwareProfileSnapshot(applied.Binding.InstallationId, status, image.Payload.ToArray());
            // Only the separately verified GUID/slot/full-generation binding can
            // request a USB restart. A failed acknowledgement does not undo the
            // already verified persistent save or cause an automatic retry.
            return new(null, await ConfirmHardwareProfileTiming(client, saved.Binding), saved);
        }
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

    public static async Task<string?> ConfirmHardwareProfileTimingAsync(OnboardDevice device, OnboardHardwareProfileSnapshot snapshot)
    {
        try
        {
            if (device.DeviceId != snapshot.Binding.DeviceId) throw new InvalidDataException("Hardware profile model mismatch.");
            await using var client = OnboardClient.Open(device);
            return await ConfirmHardwareProfileTiming(client, snapshot.Binding);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Verified hardware profile saved; USB timing acknowledgement unavailable: {0}", error);
            return L.T("Die Einstellungen sind gespeichert. Die aktive Abfragerate konnte nicht bestätigt werden.");
        }
    }

    private static async Task<string?> ConfirmHardwareProfileTiming(OnboardClient client, OnboardProfileBinding binding)
    {
        try
        {
            await client.ReconnectHardwareProfileUsbAsync(binding);
            return null;
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Verified hardware profile saved; USB timing acknowledgement unavailable: {0}", error);
            return L.T("Die Einstellungen sind gespeichert. Die aktive Abfragerate konnte nicht bestätigt werden.");
        }
    }
}

public sealed class FirmwareUpdateRequiredException(string message) : IOException(message);
