using System.Diagnostics;
using System.IO;

namespace InGlorious.Core;

public enum StartupDeviceState { Loaded, NotConnected, Ambiguous, ReadFailed, OfflineOnly }

public sealed record StartupDeviceResult(string DeviceId, StartupDeviceState State, AppProject? Project = null, OnboardInfo? Info = null)
{
    public bool Loaded => State == StartupDeviceState.Loaded && Project is not null && Info is not null;
    public string Message => L.T(State switch
    {
        StartupDeviceState.Loaded when Info?.Persistent == false => "Konfiguration geladen. Der Gerätespeicher ist nicht verfügbar.",
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

    internal static async Task<IReadOnlyList<StartupDeviceResult>> LoadAsync(
        Func<CancellationToken, Task<IReadOnlyList<OnboardDevice>>> discover,
        Func<OnboardDevice, OnboardClient> open, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OnboardDevice> devices;
        try { devices = await discover(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Trace.TraceWarning("Startup device discovery failed: {0}", ex);
            return DeviceCatalog.All.Select(d => new StartupDeviceResult(d.Id, d.IsOfflineOnly ? StartupDeviceState.OfflineOnly : StartupDeviceState.ReadFailed)).ToArray();
        }
        var results = new List<StartupDeviceResult>();
        foreach (var definition in DeviceCatalog.All)
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
                    var bytes = await client.ReadAsync(deadline.Token).ConfigureAwait(false);
                    var after = await client.GetInfoAsync(deadline.Token).ConfigureAwait(false);
                    if (before != after || after.DeviceId != definition.Id || OnboardProtocol.Crc32(bytes) != after.ActiveCrc)
                        throw new InvalidDataException("Device configuration changed during startup read.");
                    var project = OnboardSerializer.Decode(bytes);
                    if (project.DeviceId != definition.Id) throw new InvalidDataException("Device configuration belongs to another model.");
                    loaded = new(definition.Id, StartupDeviceState.Loaded, project, after);
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
        return results;
    }

    public static string SelectDevice(string preferredId, IReadOnlyList<StartupDeviceResult> results) =>
        results.FirstOrDefault(r => r.DeviceId == preferredId && r.Loaded)?.DeviceId
        ?? results.FirstOrDefault(r => r.Loaded)?.DeviceId ?? preferredId;
}
