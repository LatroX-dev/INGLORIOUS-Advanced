using System.IO;
using System.Security.Cryptography;

namespace InGlorious.Core;

public sealed class OnboardBootloaderEntryException(string message, Exception inner) : IOException(message, inner);

public sealed partial class OnboardClient
{
    private bool _bootloaderCommitAttempted;
    public static readonly FirmwareSemanticVersion AppBootloaderMinimumVersion = new(0, 11, 12);
    public static bool SupportsAppBootloaderDevice(string deviceId) =>
        BoardDescriptorRegistry.ProtocolBoards.Any(board => board.DeviceId == deviceId);

    public async Task<bool> CanEnterBootloaderAsync(OnboardFirmwareVersion expectedVersion, CancellationToken cancellationToken = default)
    {
        if (!SupportsAppBootloaderDevice(expectedDeviceId) || expectedVersion.DeviceId != expectedDeviceId ||
            expectedVersion.Version < AppBootloaderMinimumVersion) return false;
        await _exclusive.WaitAsync(cancellationToken);
        try
        {
            _ = await InfoCore(cancellationToken);
            if (await FirmwareVersionCore(cancellationToken) != expectedVersion)
                throw new InvalidDataException(L.T("Die Firmware-Version konnte nicht geprüft werden."));
            return (await ExtensionsCore(cancellationToken)).SupportsAppBootloaderEntry;
        }
        finally { _exclusive.Release(); }
    }

    public async Task EnterBootloaderAsync(OnboardFirmwareVersion expectedVersion, bool explicitlyConfirmed, CancellationToken cancellationToken = default, uint? expectedConfigurationCrc = null, OnboardProfileBinding? expectedHardwareProfile = null)
    {
        if (!explicitlyConfirmed || !SupportsAppBootloaderDevice(expectedDeviceId) || expectedVersion.DeviceId != expectedDeviceId ||
            expectedVersion.Version < AppBootloaderMinimumVersion || expectedVersion.BuildId == 0)
            throw new InvalidOperationException("Automatic update entry was not authorized for this firmware and model.");
        await _exclusive.WaitAsync(cancellationToken);
        byte[]? request = null;
        bool prepared = false;
        try
        {
            if (_bootloaderCommitAttempted) throw new InvalidOperationException("This update entry attempt has already been consumed.");
            bool bank = expectedDeviceId == "gmmk3-pro-100-iso";
            if (bank != (expectedHardwareProfile is not null)) throw ProfileChangedError();
            var before = await InfoCore(cancellationToken);
            RequireBootloaderIdle(before, bank);
            if (expectedConfigurationCrc is { } capturedCrc && before.ActiveCrc != capturedCrc)
                throw new InvalidDataException(L.T("Die Gerätekonfiguration hat sich geändert. Bitte erneut laden."));
            if (await FirmwareVersionCore(cancellationToken) != expectedVersion ||
                !(await ExtensionsCore(cancellationToken)).SupportsAppBootloaderEntry)
                throw new InvalidDataException(L.T("Die Firmware-Version konnte nicht geprüft werden."));
            if (expectedHardwareProfile is { } binding)
                await RequireBootloaderProfileCore(binding, before, cancellationToken);

            request = new byte[24];
            Put16(request, 0, 1); request[2] = OnboardSerializer.Shape(expectedDeviceId).Board;
            Put16(request, 4, expectedVersion.Version.Major); Put16(request, 6, expectedVersion.Version.Minor);
            Put16(request, 8, expectedVersion.Version.Patch); Put32(request, 12, expectedVersion.BuildId);
            do { RandomNumberGenerator.Fill(request.AsSpan(16)); } while (request.AsSpan(16).IndexOfAnyExcept((byte)0) < 0);
            RequireBootloaderEcho(await Send(OnboardProtocol.PrepareBootloader, request, cancellationToken), request);
            prepared = true;
            var after = await InfoCore(cancellationToken);
            RequireBootloaderIdle(after, bank);
            if (after != before) throw new InvalidDataException(L.T("Die Gerätekonfiguration hat sich geändert. Bitte erneut laden."));
            if (expectedHardwareProfile is { } afterBinding)
                await RequireBootloaderProfileCore(afterBinding, after, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // From this point, a timeout can mean the device already entered its bootloader.
            // Never resend COMMIT or clean up by issuing another state-changing command.
            _bootloaderCommitAttempted = true;
            try { RequireBootloaderEcho(await Send(OnboardProtocol.CommitBootloader, request, cancellationToken), request); }
            catch (Exception error)
            {
                throw new OnboardBootloaderEntryException(L.T("Der Wechsel in den Update-Modus wurde nicht bestätigt. Prüfe die USB-Verbindung."), error);
            }
        }
        finally
        {
            if (prepared && !_bootloaderCommitAttempted && request is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    RequireBootloaderEcho(await Send(OnboardProtocol.CancelBootloader, request, cleanup.Token), request);
                }
                catch { /* No commit was sent; the firmware preparation also expires on its own. */ }
            }
            _exclusive.Release();
        }
    }

    private async Task RequireBootloaderProfileCore(OnboardProfileBinding binding, OnboardInfo info, CancellationToken ct)
    {
        if (binding.DeviceId != expectedDeviceId) throw ProfileChangedError();
        await RequireHardwareProfilesCore(ct);
        if (await ProfileIdentityCore(ct) != binding.InstallationId) throw ProfileChangedError();
        var status = await ProfileStatusCore(0, ct);
        RequireReconnectProfile(status, binding);
        if (info.Generation != unchecked((uint)binding.Generation) || info.ActiveCrc != binding.Crc32 ||
            info.BlobSize != status.Bytes || info.SchemaVersion != status.SchemaVersion) throw ProfileChangedError();
    }

    private static void RequireBootloaderIdle(OnboardInfo info, bool bank)
    {
        if ((!bank && !info.Persistent) || (info.Flags & 4) != 0 || info.StagingBytes != 0)
            throw new InvalidDataException(L.T("Die Tastatur ist beschäftigt. Warte kurz und versuche es erneut."));
    }

    private static void RequireBootloaderEcho(byte[] actual, byte[] expected)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("The update-mode acknowledgement does not match this request.");
    }
}
