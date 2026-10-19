using System.Diagnostics;
using System.IO;

namespace InGlorious.Core;

public enum StartupDeviceState { Loaded, NotConnected, Ambiguous, ReadFailed, OfflineOnly }

public sealed record StartupDeviceResult(string DeviceId, StartupDeviceState State, AppProject? Project = null, OnboardInfo? Info = null,
    OnboardHardwareProfileSnapshot? ProfileSnapshot = null)
{
    public bool Loaded => State == StartupDeviceState.Loaded && Project is not null && Info is not null;
    public bool StorageHealthy => ProfileSnapshot?.Status.StorageHealthy ?? Info?.Persistent == true;
    public string Message => L.T(State switch
    {
        StartupDeviceState.Loaded when !StorageHealthy => "Konfiguration geladen. Der Gerätespeicher ist nicht verfügbar.",
        StartupDeviceState.Loaded => "Konfiguration von Tastatur geladen",
        StartupDeviceState.OfflineOnly => "Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar.",
        StartupDeviceState.NotConnected => "Kein Gerät mit INGLORIOUS verbunden. Lokales Profil geöffnet.",
        StartupDeviceState.Ambiguous => "Mehrere passende Geräte verbunden. Wähle unter „Von Tastatur laden“ ein Gerät.",
        _ => "Tastatur konnte nicht gelesen werden. Lokales Profil geöffnet. Prüfe die USB-Verbindung und wähle „Von Tastatur laden“."
    });
}

/// <summary>Reads the current configuration once per unambiguous device at startup. Never writes to a device or profile.</summary>
public static class StartupDeviceLoader
{
    public static Task<IReadOnlyList<StartupDeviceResult>> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(ct => Task.Run(OnboardClient.FindDevices, ct), OnboardClient.Open, cancellationToken);

    public static Task<IReadOnlyList<StartupDeviceResult>> LoadDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken = default) =>
        LoadAsync(ct => Task.Run(OnboardClient.FindDevices, ct), OnboardClient.Open, cancellationToken, deviceIds);

    internal static async Task<IReadOnlyList<StartupDeviceResult>> LoadAsync(
        Func<CancellationToken, Task<IReadOnlyList<OnboardDevice>>> discover,
        Func<OnboardDevice, OnboardClient> open, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? deviceIds = null)
    {
        var definitions = DeviceCatalog.All.Where(device => deviceIds is null || deviceIds.Contains(device.Id)).ToArray();
        if (definitions.Length == 0) return [];
        IReadOnlyList<OnboardDevice> devices;
        try { devices = await discover(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Trace.TraceWarning("Startup device discovery failed: {0}", ex);
            return definitions.Select(d => new StartupDeviceResult(d.Id, d.IsOfflineOnly ? StartupDeviceState.OfflineOnly : StartupDeviceState.ReadFailed)).ToArray();
        }
        var results = new List<StartupDeviceResult>();
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition.IsOfflineOnly) { results.Add(new(definition.Id, StartupDeviceState.OfflineOnly)); continue; }
            var candidates = devices.Where(d => d.DeviceId == definition.Id).ToArray();
            if (candidates.Length != 1)
            {
                results.Add(new(definition.Id, candidates.Length == 0 ? StartupDeviceState.NotConnected : StartupDeviceState.Ambiguous));
                continue;
            }
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                StartupDeviceResult loaded;
                await using (var client = open(candidates[0]))
                {
                    var before = await client.GetInfoAsync(deadline.Token).ConfigureAwait(false);
                    if ((before.Flags & 4) != 0 || before.StagingBytes != 0) throw new IOException("Another configuration transfer is active.");
                    var profile = await client.GetHardwareProfileStatusAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
                    OnboardHardwareProfileSnapshot? snapshot = null;
                    byte[] bytes;
                    if (profile is not null)
                    {
                        if (profile.ActiveSlot == 0 || profile.SwitchPending || profile.UploadOpen) throw new IOException("Hardware profile is changing.");
                        snapshot = await client.ReadHardwareProfileAsync(profile.ActiveSlot, deadline.Token).ConfigureAwait(false);
                        var current = await client.GetHardwareProfileStatusAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
                        if (current is null || current.ActiveSlot != profile.ActiveSlot || current.SwitchPending || current.UploadOpen ||
                            current.Generation != snapshot.Status.Generation || current.Crc32 != snapshot.Status.Crc32 ||
                            snapshot.InstallationId != await client.GetInstallationIdAsync(deadline.Token).ConfigureAwait(false))
                            throw new InvalidDataException("Hardware profile changed during startup read.");
                        bytes = snapshot.Payload;
                    }
                    else bytes = await client.ReadAsync(deadline.Token).ConfigureAwait(false);
                    var after = await client.GetInfoAsync(deadline.Token).ConfigureAwait(false);
                    if ((snapshot is null && before != after) || after.DeviceId != definition.Id || OnboardProtocol.Crc32(bytes) != after.ActiveCrc)
                        throw new InvalidDataException("Device configuration changed during startup read.");
                    if (snapshot is not null)
                    {
                        var final = await client.GetHardwareProfileStatusAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
                        if (final is null || final.ActiveSlot != snapshot.Binding.Slot || final.SwitchPending || final.UploadOpen ||
                            final.Generation != snapshot.Binding.Generation || final.Crc32 != snapshot.Binding.Crc32)
                            throw new InvalidDataException("Hardware selector changed after startup verification.");
                    }
                    var project = OnboardSerializer.Decode(bytes);
                    if (project.DeviceId != definition.Id) throw new InvalidDataException("Device configuration belongs to another model.");
                    loaded = new(definition.Id, StartupDeviceState.Loaded, project, after, snapshot);
                }
                results.Add(loaded);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Trace.TraceWarning("Startup read failed for {0}: {1}", definition.Id, ex);
                results.Add(new(definition.Id, StartupDeviceState.ReadFailed));
            }
        }
        if (results.Any(result => result.Loaded))
        {
            try
            {
                // A device can disconnect after its final INFO but before another model
                // finishes reading. Never adopt that old read for a later USB connection.
                var current = await discover(cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < results.Count; index++)
                {
                    var result = results[index];
                    if (!result.Loaded) continue;
                    var before = devices.Where(device => device.DeviceId == result.DeviceId).ToArray();
                    var after = current.Where(device => device.DeviceId == result.DeviceId).ToArray();
                    if (before.Length != 1 || after.Length != 1 || !string.Equals(before[0].Path, after[0].Path, StringComparison.Ordinal))
                        results[index] = new(result.DeviceId, StartupDeviceState.ReadFailed);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Trace.TraceWarning("Connection verification after configuration read failed: {0}", ex);
                for (var index = 0; index < results.Count; index++)
                    if (results[index].Loaded) results[index] = new(results[index].DeviceId, StartupDeviceState.ReadFailed);
            }
        }
        return results;
    }

    public static string SelectDevice(string preferredId, IReadOnlyList<StartupDeviceResult> results) =>
        results.FirstOrDefault(r => r.DeviceId == preferredId && r.Loaded)?.DeviceId
        ?? results.FirstOrDefault(r => r.Loaded)?.DeviceId ?? preferredId;
}
