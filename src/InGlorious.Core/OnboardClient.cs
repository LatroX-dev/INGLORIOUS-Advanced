using System.Buffers.Binary;
using System.IO;

namespace InGlorious.Core;

public static class OnboardIdentity
{
    public static bool MatchesNames(string deviceId, string manufacturer, string product)
    {
        if (!BoardDescriptorRegistry.TryGet(deviceId, out var board)) return false;
        var suffix = board.ProductSuffix;
        return ((deviceId is "pro-iso" or "numpad" && manufacturer == "InGlorious" && product == "InGlorious" + suffix) ||
            (manufacturer == "INGLORIOUS" && product == "INGLORIOUS" + suffix));
    }
}

public interface IOnboardTransport : IAsyncDisposable
{
    Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken);
}
public sealed record OnboardInfo(string DeviceId, byte Flags, int BlobSize, uint Generation, uint ActiveCrc, int StagingBytes, int SchemaVersion = 1)
{
    public bool Persistent => (Flags & 1) != 0;
    public bool SupportsEffects => (Flags & 8) != 0;
    public bool SupportsTiming => (Flags & 16) != 0;
    public bool SupportsColorSequences => (Flags & 32) != 0;
    public bool SupportsRuntimeBrightness => (Flags & 64) != 0;
    public bool SupportsStandby => (Flags & 128) != 0;
}
public sealed record OnboardTransferResult(uint Generation, uint Crc32, int Bytes, bool ReadbackVerified);
public sealed record OnboardReconnectResult(int ActiveIntervalMs, int SavedIntervalMs, bool ReconnectQueued);
public sealed record OnboardRuntimeBrightness(byte Brightness, byte SliderMode);
public sealed record OnboardExtensions(ushort Version, ushort Flags, ushort LightingTestDurationMs)
{
    public const ushort MaximumLightingTestDurationMs = 60000;
    public bool SupportsLightingTest => Version == 1 && (Flags & 1) != 0;
    public bool SupportsScreensaver => Version == 1 && (Flags & 2) != 0;
    public bool SupportsDeviceTest => Version == 1 && (Flags & 4) != 0;
}
public sealed record OnboardLightingTestStatus(bool Active, byte Pattern, ushort RemainingMs);

/// <summary>Configuration and acknowledged USB re-enumeration only. No flashing, memory addresses or bootloader commands.</summary>
public sealed partial class OnboardClient(IOnboardTransport transport, string expectedDeviceId, bool allowEngineering = false) : IAsyncDisposable
{
    private readonly bool _allowEngineering = ValidateEngineeringAccess(expectedDeviceId, allowEngineering);
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private byte _sequence;
    private ushort? _lightingTestDurationMs;
    private static bool ValidateEngineeringAccess(string deviceId, bool allowEngineering)
    {
        if (BoardDescriptorRegistry.Get(deviceId).IsEngineeringOnly && !EngineeringConfigurationPolicy.IsEnabled(deviceId, allowEngineering))
            throw new InvalidDataException("This device requires an explicit local engineering configuration opt-in.");
        return allowEngineering;
    }
    public static IReadOnlyList<OnboardDevice> FindDevices() => OnboardHidTransport.FindDevices();
    public static OnboardClient Open(OnboardDevice device) => new(OnboardHidTransport.Open(device), device.DeviceId);

    public async Task<OnboardInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        await _exclusive.WaitAsync(cancellationToken);
        try { return await InfoCore(cancellationToken); }
        finally { _exclusive.Release(); }
    }
    public async Task<OnboardFirmwareVersion?> GetFirmwareVersionAsync(CancellationToken cancellationToken = default)
    {
        await _exclusive.WaitAsync(cancellationToken);
        try
        {
            byte[] data;
            try { data = await Send(OnboardProtocol.GetFirmwareVersion, [], cancellationToken); }
            catch (OnboardCommandException ex) when (ex.Status == 9) { return null; }
            return ParseFirmwareVersion(data, expectedDeviceId);
        }
        finally { _exclusive.Release(); }
    }
    internal static OnboardFirmwareVersion ParseFirmwareVersion(byte[] data, string expectedDeviceId)
    {
        var board = OnboardSerializer.Shape(expectedDeviceId).Board;
        if (data.Length != 16 || Get16(data, 0) != 1 || data[2] != board || data[3] != 0 || Get16(data, 10) != 0 || Get32(data, 12) == 0)
            throw new InvalidDataException(L.T("Die Firmware-Version konnte nicht geprüft werden."));
        return new(expectedDeviceId, new(Get16(data, 4), Get16(data, 6), Get16(data, 8)), Get32(data, 12));
    }
    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _exclusive.WaitAsync(cancellationToken);
        try { return await ReadCore(await InfoCore(cancellationToken), cancellationToken); }
        finally { _exclusive.Release(); }
    }
    public async Task<OnboardExtensions> GetExtensionsAsync(CancellationToken cancellationToken = default)
    {
        await _exclusive.WaitAsync(cancellationToken);
        try { _ = await InfoCore(cancellationToken); return await ExtensionsCore(cancellationToken); }
        finally { _exclusive.Release(); }
    }
    private async Task<OnboardExtensions> ExtensionsCore(CancellationToken ct)
    {
        byte[] data;
        try { data = await Send(OnboardProtocol.GetExtensions, [], ct); }
        catch (OnboardCommandException ex) when (ex.Status == 9) { _lightingTestDurationMs = 0; return new(0, 0, 0); } // Old firmware: read-only unsupported-command response.
        if (data.Length != 8 || Get16(data, 0) != 1 || (Get16(data, 2) & ~7) != 0 || Get16(data, 6) != 0 ||
            ((Get16(data, 2) & 1) != 0 ? Get16(data, 4) is < 1 or > OnboardExtensions.MaximumLightingTestDurationMs : Get16(data, 4) != 0))
            throw new InvalidDataException(L.T("Ungültige Antwort zu den Firmware-Erweiterungen."));
        _lightingTestDurationMs = Get16(data, 4);
        return new(Get16(data, 0), Get16(data, 2), Get16(data, 4));
    }
    public Task<OnboardLightingTestStatus> GetLightingTestStatusAsync(CancellationToken cancellationToken = default) => LightingTestCore(0, cancellationToken);
    public Task<OnboardLightingTestStatus> StartLightingTestAsync(CancellationToken cancellationToken = default) => LightingTestCore(1, cancellationToken);
    public Task<OnboardLightingTestStatus> StopLightingTestAsync(CancellationToken cancellationToken = default) => LightingTestCore(2, cancellationToken);
    private async Task<OnboardLightingTestStatus> LightingTestCore(byte action, CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct);
        try
        {
            if (action == 1)
            {
                var info = await InfoCore(ct);
                if (!info.Persistent || (info.Flags & 4) != 0 || info.StagingBytes != 0) throw new IOException(L.T("Der Gerätespeicher ist nicht bereit oder eine Übertragung läuft. Der Beleuchtungstest wird nicht gestartet."));
                if (!(await ExtensionsCore(ct)).SupportsLightingTest) throw new NotSupportedException(L.T("Für den Beleuchtungstest ist ein Firmware-Update erforderlich."));
            }
            var response = await Send(OnboardProtocol.LightingTest, [action], ct);
            var expectedPattern = BoardDescriptorRegistry.Get(expectedDeviceId).LightingTestPattern;
            if (response.Length != 4 || response[0] > 1 ||
                (response[0] == 0 && (response[1] != 0 || Get16(response, 2) != 0)) ||
                (response[0] == 1 && (response[1] != expectedPattern || Get16(response, 2) < 1 || Get16(response, 2) > (_lightingTestDurationMs ?? OnboardExtensions.MaximumLightingTestDurationMs))) ||
                (action == 1 && response[0] != 1) || (action == 2 && response[0] != 0))
                throw new InvalidDataException(L.T("Ungültige Antwort zum Beleuchtungstest."));
            return new(response[0] == 1, response[1], Get16(response, 2));
        }
        finally { _exclusive.Release(); }
    }
    public async Task<OnboardRuntimeBrightness> GetRuntimeBrightnessAsync(CancellationToken cancellationToken = default)
    {
        await _exclusive.WaitAsync(cancellationToken);
        try
        {
            var info = await InfoCore(cancellationToken);
            if (!info.SupportsRuntimeBrightness) throw new InvalidDataException(L.T("Diese Firmware unterstützt keine flüchtige Helligkeitssteuerung."));
            var value = await Send(OnboardProtocol.ReadRuntimeBrightness, [], cancellationToken);
            if (value.Length != 4 || value[1] > 3 || value[2] != 0 || value[3] != 0 || (!BoardDescriptorRegistry.Get(expectedDeviceId).HasSlider && value[1] != 0)) throw new InvalidDataException(L.T("Ungültige Antwort zur Laufzeithelligkeit."));
            return new(value[0], value[1]);
        }
        finally { _exclusive.Release(); }
    }
    public async Task<byte> SetRuntimeBrightnessAsync(byte brightness, CancellationToken cancellationToken = default)
    {
        await _exclusive.WaitAsync(cancellationToken);
        try
        {
            var info = await InfoCore(cancellationToken);
            if (!info.SupportsRuntimeBrightness) throw new InvalidDataException(L.T("Diese Firmware unterstützt keine flüchtige Helligkeitssteuerung."));
            if ((info.Flags & 4) != 0) throw new IOException(L.T("Eine Übertragung läuft. Die Laufzeithelligkeit wird nicht geändert."));
            var value = await Send(OnboardProtocol.SetRuntimeBrightness, [brightness], cancellationToken);
            if (value.Length != 1 || value[0] != brightness) throw new InvalidDataException(L.T("Die Laufzeithelligkeit wurde nicht bestätigt."));
            return value[0];
        }
        finally { _exclusive.Release(); }
    }
    public async Task<OnboardReconnectResult> ReconnectUsbAsync(uint expectedGeneration, uint expectedCrc, CancellationToken cancellationToken = default)
    {
        await _exclusive.WaitAsync(cancellationToken);
        try
        {
            var info = await InfoCore(cancellationToken);
            if (!info.SupportsTiming) throw new InvalidDataException("Diese Firmware unterstützt keine USB-Neuanmeldung aus der App.");
            if (!info.Persistent || info.Generation != expectedGeneration || info.ActiveCrc != expectedCrc || (info.Flags & 4) != 0)
                throw new IOException("Der gespeicherte Gerätestand ist verändert oder eine Übertragung läuft. USB wird nicht neu angemeldet.");
            // Re-read under this client's lock; reconnect is never sent after an unverified configuration.
            var data = await ReadCore(info, cancellationToken);
            var savedInterval = OnboardSerializer.Decode(data, _allowEngineering).DeviceSettings.UsbPollingIntervalMs;
            var response = await Send(OnboardProtocol.ReconnectUsb, [], cancellationToken);
            if (response.Length != 4 || response[0] is not (1 or 2 or 4 or 8) || response[1] != savedInterval || response[2] > 1 || response[3] != 0 || (response[2] == 1) != (response[0] != response[1]))
                throw new InvalidDataException("USB-Neuanmeldung nicht eindeutig bestätigt. Gespeicherte Konfiguration bleibt erhalten; bei Bedarf USB-Kabel neu verbinden.");
            return new(response[0], response[1], response[2] == 1);
        }
        finally { _exclusive.Release(); }
    }
    public async Task<OnboardTransferResult> ApplyAsync(OnboardImage image, IProgress<int>? progress = null, CancellationToken cancellationToken = default, uint? expectedGeneration = null)
    {
        if (image.DeviceId != expectedDeviceId) throw new InvalidDataException("Das Profil passt nicht zum ausgewählten Gerät.");
        // Freeze caller-owned data before any asynchronous work; reject forged images through a semantic decode.
        var frozen = image.Payload.ToArray();
        var decoded = OnboardSerializer.Decode(frozen, _allowEngineering);
        if (decoded.DeviceId != expectedDeviceId) throw new InvalidDataException("Konfigurationsplatine stimmt nicht überein.");
        var imageSchema = Get16(frozen, 4);
        var crc = OnboardProtocol.Crc32(frozen);
        await _exclusive.WaitAsync(cancellationToken);
        var began = false; var commitSent = false;
        try
        {
            var info = await InfoCore(cancellationToken);
            if (expectedGeneration.HasValue && info.Generation != expectedGeneration.Value) throw new IOException("Die Gerätekonfiguration hat sich seit deiner Sicherung geändert. Bitte erneut auslesen und sichern.");
            if (!info.Persistent) throw new IOException("Das Gerät meldet keinen verfügbaren dauerhaften Speicher.");
            if (imageSchema == 3 && !info.SupportsColorSequences) throw new InvalidDataException(L.T("Eigene Farbfolgen benötigen ein Firmware-Update. Es wird nichts übertragen."));
            if (imageSchema == 4 && !info.SupportsStandby) throw new InvalidDataException(L.T(decoded.Standby.Mode == 2
                ? "Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen."
                : "Standby benötigt ein Firmware-Update. Es wird nichts übertragen."));
            if (imageSchema >= 2 && !info.SupportsEffects && !info.SupportsTiming) throw new InvalidDataException("Die installierte Firmware unterstützt dieses Konfigurationsformat nicht. Ein Firmware-Update ist erforderlich.");
            if (imageSchema >= 2 && OnboardSerializer.HasEffects(frozen) && !info.SupportsEffects) throw new InvalidDataException("Die installierte Firmware unterstützt keine Beleuchtungseffekte. Für diese Konfiguration ist ein Firmware-Update erforderlich.");
            if ((decoded.DeviceSettings.UsbPollingIntervalMs != 1 || decoded.DeviceSettings.DebounceMs != 5) && !info.SupportsTiming) throw new InvalidDataException("Die installierte Firmware unterstützt keine geänderten USB- oder Entprellzeiten. Für diese Konfiguration ist ein Firmware-Update erforderlich.");
            // INFO describes the active configuration. Effects-capable firmware accepts both fixed layouts.
            if (frozen.Length != OnboardSerializer.Shape(expectedDeviceId, imageSchema).Size) throw new InvalidDataException("Konfigurationsgröße passt nicht zur Firmware.");
            // Capture the previous configuration before touching staging; the UI additionally saves this as a recovery file.
            _ = await ReadCore(info, cancellationToken);
            if (imageSchema == 4 && decoded.Standby.Mode == 2)
            {
                // Always query again, immediately before staging; a prior preview/capability
                // read is not permission to send mode 2 to firmware that rejects that byte.
                OnboardExtensions extensions;
                try { extensions = await ExtensionsCore(cancellationToken); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                { throw new InvalidDataException(L.T("Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen."), ex); }
                if (!extensions.SupportsScreensaver) throw new InvalidDataException(L.T("Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen."));
            }
            var begin = new byte[10]; Put16(begin, 0, frozen.Length); Put32(begin, 2, info.Generation); Put32(begin, 6, crc);
            var response = await Send(OnboardProtocol.Begin, begin, cancellationToken);
            if (response.Length != 2 || Get16(response, 0) != 0) throw new InvalidDataException("Ungültige Upload-Bestätigung.");
            began = true;
            for (var offset = 0; offset < frozen.Length;)
            {
                var count = Math.Min(20, frozen.Length - offset);
                var data = new byte[4 + count]; Put16(data, 0, offset); data[2] = (byte)count; frozen.AsSpan(offset, count).CopyTo(data.AsSpan(4));
                response = await Send(OnboardProtocol.Write, data, cancellationToken);
                offset += count;
                if (response.Length != 2 || Get16(response, 0) != offset) throw new InvalidDataException("Das Gerät hat einen falschen Schreibfortschritt bestätigt.");
                progress?.Report(offset * 70 / frozen.Length);
            }
            var commit = new byte[8]; Put32(commit, 0, crc); Put32(commit, 4, info.Generation);
            commitSent = true;
            // A lost COMMIT response is deliberately not retried: persistence may have completed.
            response = await Send(OnboardProtocol.Commit, commit, cancellationToken);
            var expectedNewGeneration = info.Generation == uint.MaxValue ? 1u : info.Generation + 1;
            if (response.Length != 8 || Get32(response, 0) != expectedNewGeneration || Get32(response, 4) != crc)
                throw new InvalidDataException("Unklare Speicherbestätigung. Gerät erneut auslesen; nicht automatisch wiederholen.");
            var after = await InfoCore(cancellationToken);
            if (after.Generation != Get32(response, 0) || after.ActiveCrc != crc || !after.Persistent || after.SchemaVersion != imageSchema || after.BlobSize != frozen.Length)
                throw new InvalidDataException("Der gespeicherte Zustand wurde nicht bestätigt.");
            progress?.Report(80);
            var readback = await ReadCore(after, cancellationToken);
            if (!readback.AsSpan().SequenceEqual(frozen)) throw new IOException("Geräte-Rücklesen weicht vom Profil ab. Übertragung nicht bestätigt.");
            progress?.Report(100);
            return new(after.Generation, crc, frozen.Length, true);
        }
        catch (Exception ex) when (commitSent)
        {
            throw new IOException("Speicherergebnis nicht vollständig bestätigt. Bitte Gerät erneut auslesen. Es erfolgt keine automatische Wiederholung. " + ex.Message, ex);
        }
        finally
        {
            if (began && !commitSent)
            {
                try { using var abortLimit = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await Send(OnboardProtocol.Abort, [], abortLimit.Token); }
                catch { /* Staging is volatile and never committed by this cleanup. */ }
            }
            _exclusive.Release();
        }
    }
    private async Task<OnboardInfo> InfoCore(CancellationToken ct)
    {
        var data = await Send(OnboardProtocol.Info, [], ct);
        if (data.Length != 24) throw new InvalidDataException("Unvollständiger Geräte-Handshake.");
        var schemaVersion = Get16(data, 22);
        var s = OnboardSerializer.Shape(expectedDeviceId, schemaVersion);
        if (((data[7] & 2) != 0) != BoardDescriptorRegistry.Get(expectedDeviceId).HasSlider)
            throw new InvalidDataException("Geräte-Handshake passt nicht zur erwarteten INGLORIOUS-Firmware.");
        var supportsSchema2 = (data[7] & 24) != 0;
        var uploadCapacity = OnboardSerializer.Shape(expectedDeviceId, supportsSchema2 ? 2 : 1).Size;
        if (data[0] != s.Board || data[1] != s.Rows || data[2] != s.Cols || data[3] != 4 || data[4] != s.Leds || data[5] != 32 || data[6] != 128 || ((data[7] & 128) != 0 && (data[7] & 120) != 120) || ((data[7] & 32) != 0 && (data[7] & 8) == 0) || Get16(data, 8) != s.Size || Get16(data, 10) != OnboardSerializer.MacroCapacity(expectedDeviceId, schemaVersion) || Get16(data, 20) > uploadCapacity || (schemaVersion >= 2 && !supportsSchema2) || (schemaVersion >= 3 && (data[7] & 32) == 0) || (schemaVersion == 4 && (data[7] & 128) == 0))
            throw new InvalidDataException("Geräte-Handshake passt nicht zur erwarteten INGLORIOUS-Firmware.");
        return new(expectedDeviceId, data[7], Get16(data, 8), Get32(data, 12), Get32(data, 16), Get16(data, 20), schemaVersion);
    }
    private async Task<byte[]> ReadCore(OnboardInfo before, CancellationToken ct)
    {
        var result = new byte[before.BlobSize];
        for (var offset = 0; offset < result.Length;)
        {
            var count = Math.Min(20, result.Length - offset);
            var request = new byte[4]; Put16(request, 0, offset); request[2] = (byte)count;
            var data = await Send(OnboardProtocol.Read, request, ct);
            if (data.Length != 4 + count || Get16(data, 0) != offset || data[2] != count || data[3] != 0) throw new InvalidDataException("Ungültiger Rückleseblock.");
            data.AsSpan(4).CopyTo(result.AsSpan(offset)); offset += count;
        }
        var after = await InfoCore(ct);
        if (after.Generation != before.Generation || after.ActiveCrc != before.ActiveCrc || after.BlobSize != before.BlobSize || after.SchemaVersion != before.SchemaVersion || OnboardProtocol.Crc32(result) != before.ActiveCrc)
            throw new InvalidDataException("Gerätekonfiguration änderte sich während des Auslesens oder ist beschädigt.");
        if (Get16(result, 4) != before.SchemaVersion || result[6] != OnboardSerializer.Shape(expectedDeviceId).Board)
            throw new InvalidDataException("Zurückgelesenes Format stimmt nicht mit dem Geräte-Handshake überein.");
        return result;
    }
    private async Task<byte[]> Send(byte command, byte[] payload, CancellationToken ct)
    {
        var sequence = unchecked(++_sequence);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Embedded-flash wear-level consolidation may exceed 20 seconds on WB32.
        deadline.CancelAfter(TimeSpan.FromSeconds(command == OnboardProtocol.Commit ? 120 : 4));
        var response = await transport.ExchangeAsync(OnboardProtocol.Packet(command, sequence, payload), deadline.Token);
        return OnboardProtocol.ParseResponse(response, command, sequence);
    }
    private static ushort Get16(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));
    private static uint Get32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));
    private static void Put16(byte[] b, int i, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i), checked((ushort)v));
    private static void Put32(byte[] b, int i, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i), v);
    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
