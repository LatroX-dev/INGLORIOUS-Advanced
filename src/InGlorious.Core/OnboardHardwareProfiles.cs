using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace InGlorious.Core;

public sealed record OnboardProfileBinding(string DeviceId, Guid InstallationId, byte Slot, ulong Generation, uint Crc32);
public sealed record OnboardHardwareProfileStatus(string DeviceId, byte PhysicalSlot, byte ActiveSlot, byte Flags,
    byte Slot, ulong Generation, uint Crc32, int Bytes, int SchemaVersion)
{
    public bool StorageHealthy => (Flags & 1) != 0;
    public bool LegacyFallback => (Flags & 2) != 0;
    public bool SwitchPending => (Flags & 4) != 0;
    public bool UploadOpen => (Flags & 8) != 0;
    public bool MacPosition => (Flags & 16) != 0;
    public bool MacActive => (Flags & 32) != 0;
    public bool OsPending => (Flags & 64) != 0;
    public bool Recovered => (Flags & 128) != 0;
    public OnboardProfileBinding Bind(Guid installationId) => new(DeviceId, installationId, Slot, Generation, Crc32);
}
public sealed record OnboardHardwareProfileSnapshot(Guid InstallationId, OnboardHardwareProfileStatus Status, byte[] Payload)
{
    public OnboardProfileBinding Binding => Status.Bind(InstallationId);
}
public sealed record OnboardHardwareProfileTransferResult(OnboardProfileBinding Binding, int Bytes, bool ReadbackVerified);

public sealed partial class OnboardClient
{
    private bool? _hardwareProfilesSupported;
    private static readonly Guid InvalidInstallationId = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private async Task<bool> HardwareProfilesSupportedCore(CancellationToken ct)
    {
        if (expectedDeviceId != "gmmk3-pro-100-iso") return false;
        // In particular, private diagnostic 0.11.10 used RAW18 for a different,
        // read-only probe. Never send RAW18 without this positive capability.
        _hardwareProfilesSupported ??= (await ExtensionsCore(ct)).SupportsHardwareProfiles;
        return _hardwareProfilesSupported == true;
    }
    private async Task RequireHardwareProfilesCore(CancellationToken ct)
    {
        if (!await HardwareProfilesSupportedCore(ct))
            throw new NotSupportedException(L.T("Diese Funktion benötigt ein Firmware-Update."));
    }
    public async Task<OnboardHardwareProfileStatus?> GetHardwareProfileStatusAsync(byte slot = 0, CancellationToken cancellationToken = default)
    {
        if (slot > 3) throw new ArgumentOutOfRangeException(nameof(slot));
        await _exclusive.WaitAsync(cancellationToken);
        try { return await HardwareProfilesSupportedCore(cancellationToken) ? await ProfileStatusCore(slot, cancellationToken) : null; }
        finally { _exclusive.Release(); }
    }
    private async Task<OnboardHardwareProfileStatus> ProfileStatusCore(byte slot, CancellationToken ct) =>
        ParseHardwareProfileStatus(await Send(OnboardProtocol.ProfileStatus, [slot], ct), expectedDeviceId, slot);

    internal static OnboardHardwareProfileStatus ParseHardwareProfileStatus(byte[] data, string deviceId, byte querySlot)
    {
        if (deviceId != "gmmk3-pro-100-iso" || querySlot > 3 || data.Length != 24 || data[0] != 1 || data[1] != 16 || data[2] != 3 ||
            data[3] > 3 || data[4] > 3 || data[6] is < 1 or > 3 || data[7] != 0 ||
            (querySlot == 0 ? data[6] != data[4] : data[6] != querySlot)) throw ProfileReplyError();
        var value = new OnboardHardwareProfileStatus(deviceId, data[3], data[4], data[5], data[6],
            Get64(data, 8), Get32(data, 16), Get16(data, 20), Get16(data, 22));
        if (value.SchemaVersion is < 1 or > 7 || value.Bytes != OnboardSerializer.Shape(deviceId, value.SchemaVersion).Size ||
            value.Bytes > 7680 || (value.Generation == 0) != value.LegacyFallback ||
            (!value.OsPending && value.MacPosition != value.MacActive) ||
            (!value.SwitchPending && value.PhysicalSlot != 0 && value.PhysicalSlot != value.ActiveSlot)) throw ProfileReplyError();
        return value;
    }
    public async Task<Guid> GetInstallationIdAsync(CancellationToken cancellationToken = default)
    {
        await _exclusive.WaitAsync(cancellationToken);
        try { await RequireHardwareProfilesCore(cancellationToken); return await ProfileIdentityCore(cancellationToken); }
        finally { _exclusive.Release(); }
    }
    private async Task<Guid> ProfileIdentityCore(CancellationToken ct)
    {
        var data = await Send(OnboardProtocol.ProfileInstallationIdentity, [], ct);
        if (data.Length != 20 || data[0] != 1 || data[1] != 16 || Get16(data, 2) != 0) throw ProfileReplyError();
        var id = new Guid(data.AsSpan(4, 16));
        if (id == Guid.Empty || id == InvalidInstallationId) throw ProfileReplyError();
        return id;
    }
    public async Task<OnboardHardwareProfileSnapshot> ReadHardwareProfileAsync(byte slot, CancellationToken cancellationToken = default)
    {
        RequireSlot(slot);
        await _exclusive.WaitAsync(cancellationToken);
        try
        {
            await RequireHardwareProfilesCore(cancellationToken);
            var identity = await ProfileIdentityCore(cancellationToken);
            var status = await ProfileStatusCore(slot, cancellationToken);
            var payload = await ProfileReadCore(status, cancellationToken);
            if (identity != await ProfileIdentityCore(cancellationToken)) throw ProfileChangedError();
            return new(identity, status, payload);
        }
        finally { _exclusive.Release(); }
    }
    public async Task<OnboardReconnectResult> ReconnectHardwareProfileUsbAsync(OnboardProfileBinding binding, CancellationToken cancellationToken = default)
    {
        RequireSlot(binding.Slot);
        if (binding.DeviceId != expectedDeviceId || binding.InstallationId == Guid.Empty || binding.InstallationId == InvalidInstallationId)
            throw ProfileChangedError();
        await _exclusive.WaitAsync(cancellationToken);
        try
        {
            await RequireHardwareProfilesCore(cancellationToken);
            if (await ProfileIdentityCore(cancellationToken) != binding.InstallationId) throw ProfileChangedError();
            var before = await ProfileStatusCore(binding.Slot, cancellationToken);
            RequireReconnectProfile(before, binding);
            var payload = await ProfileReadCore(before, cancellationToken);
            int savedInterval = OnboardSerializer.Decode(payload, _allowEngineering).DeviceSettings.UsbPollingIntervalMs;
            var info = await InfoCore(cancellationToken);
            if (!info.SupportsTiming || (info.Flags & 4) != 0 || info.StagingBytes != 0 ||
                info.Generation != unchecked((uint)binding.Generation) || info.ActiveCrc != binding.Crc32 ||
                info.BlobSize != before.Bytes || info.SchemaVersion != before.SchemaVersion) throw ProfileChangedError();
            if (await ProfileIdentityCore(cancellationToken) != binding.InstallationId) throw ProfileChangedError();
            RequireReconnectProfile(await ProfileStatusCore(binding.Slot, cancellationToken), binding);
            // Firmware checks this binding and physical selector again in the
            // RAW8 handler. A switch after our final read cannot restart a different slot.
            var request = new byte[16]; request[0] = binding.Slot; Put64(request, 4, binding.Generation); Put32(request, 12, binding.Crc32);
            var response = await Send(OnboardProtocol.ReconnectUsb, request, cancellationToken);
            if (response.Length != 4 || response[0] is not (1 or 2 or 4 or 8) || response[1] != savedInterval || response[2] > 1 ||
                response[3] != 0 || (response[2] == 1) != (response[0] != response[1])) throw ProfileReplyError();
            return new(response[0], response[1], response[2] == 1);
        }
        finally { _exclusive.Release(); }
    }
    private static void RequireReconnectProfile(OnboardHardwareProfileStatus status, OnboardProfileBinding binding)
    {
        if (!status.StorageHealthy || status.UploadOpen || status.SwitchPending || status.OsPending ||
            status.PhysicalSlot != binding.Slot || status.ActiveSlot != binding.Slot || status.Slot != binding.Slot ||
            status.Generation != binding.Generation || status.Crc32 != binding.Crc32) throw ProfileChangedError();
    }
    private async Task<byte[]> ProfileReadCore(OnboardHardwareProfileStatus before, CancellationToken ct)
    {
        if (before.UploadOpen) throw new IOException(L.T("Eine Übertragung läuft noch. Bitte warte auf ihren Abschluss."));
        var result = new byte[before.Bytes];
        for (int offset = 0; offset < result.Length;)
        {
            int count = Math.Min(12, result.Length - offset);
            var request = new byte[16]; request[0] = before.Slot; request[1] = (byte)count;
            Put16(request, 2, offset); Put64(request, 4, before.Generation); Put32(request, 12, before.Crc32);
            var data = await Send(OnboardProtocol.ProfileRead, request, ct);
            if (data.Length != 12 + count || !data.AsSpan(0, 12).SequenceEqual(request.AsSpan(0, 12))) throw ProfileReplyError();
            data.AsSpan(12).CopyTo(result.AsSpan(offset)); offset += count;
        }
        var after = await ProfileStatusCore(before.Slot, ct);
        if (!SameProfile(before, after) || after.UploadOpen || OnboardProtocol.Crc32(result) != before.Crc32 ||
            Get16(result, 4) != before.SchemaVersion) throw ProfileChangedError();
        if (OnboardSerializer.Decode(result, _allowEngineering).DeviceId != expectedDeviceId) throw ProfileReplyError();
        return result;
    }
    public async Task<OnboardHardwareProfileTransferResult> ApplyHardwareProfileAsync(OnboardImage image, OnboardProfileBinding binding,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default, bool requireActiveSlot = false)
    {
        RequireSlot(binding.Slot);
        if (binding.DeviceId != expectedDeviceId || image.DeviceId != expectedDeviceId || binding.InstallationId == Guid.Empty ||
            binding.InstallationId == InvalidInstallationId || binding.Generation == ulong.MaxValue) throw ProfileChangedError();
        var frozen = image.Payload.ToArray();
        var decoded = OnboardSerializer.Decode(frozen, _allowEngineering);
        if (decoded.DeviceId != expectedDeviceId || frozen.Length > 7680) throw ProfileReplyError();
        var crc = OnboardProtocol.Crc32(frozen);
        uint owner;
        do { owner = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)); } while (owner == 0);
        await _exclusive.WaitAsync(cancellationToken);
        bool beginSent = false, commitSent = false;
        try
        {
            await RequireHardwareProfilesCore(cancellationToken);
            if (binding.InstallationId != await ProfileIdentityCore(cancellationToken)) throw ProfileChangedError();
            var before = await ProfileStatusCore(binding.Slot, cancellationToken);
            if (!before.StorageHealthy || before.UploadOpen || before.Generation != binding.Generation || before.Crc32 != binding.Crc32)
                throw ProfileChangedError();
            // Full frozen target snapshot before staging; the caller persists its
            // own lossless backup before invoking this operation.
            _ = await ProfileReadCore(before, cancellationToken);
            if (requireActiveSlot)
            {
                var active = await ProfileStatusCore(binding.Slot, cancellationToken);
                if (active.ActiveSlot != binding.Slot || active.SwitchPending || active.UploadOpen || !SameProfile(before, active))
                    throw ProfileChangedError();
            }
            var begin = new byte[24]; begin[0] = binding.Slot; Put16(begin, 2, frozen.Length);
            Put64(begin, 4, binding.Generation); Put32(begin, 12, binding.Crc32); Put32(begin, 16, crc); Put32(begin, 20, owner);
            beginSent = true;
            var response = await Send(OnboardProtocol.ProfileBegin, begin, cancellationToken);
            if (!response.AsSpan().SequenceEqual(begin)) throw ProfileReplyError();
            for (int offset = 0; offset < frozen.Length;)
            {
                int count = Math.Min(16, frozen.Length - offset);
                var chunk = new byte[8 + count]; Put32(chunk, 0, owner); chunk[4] = binding.Slot; chunk[5] = (byte)count;
                Put16(chunk, 6, offset); frozen.AsSpan(offset, count).CopyTo(chunk.AsSpan(8));
                response = await Send(OnboardProtocol.ProfileWrite, chunk, cancellationToken); offset += count;
                if (response.Length != 8 || Get32(response, 0) != owner || response[4] != binding.Slot || response[5] != 0 || Get16(response, 6) != offset)
                    throw ProfileReplyError();
                progress?.Report(offset * 80 / frozen.Length);
            }
            var commit = new byte[20]; Put32(commit, 0, owner); commit[4] = binding.Slot;
            Put64(commit, 8, binding.Generation); Put32(commit, 16, crc);
            commitSent = true;
            response = await Send(OnboardProtocol.ProfileCommit, commit, cancellationToken);
            if (response.Length != 16 || response[0] != binding.Slot || response[1] != 0 || Get16(response, 2) != frozen.Length ||
                Get64(response, 4) != binding.Generation + 1 || Get32(response, 12) != crc) throw ProfileReplyError();
            var after = await ProfileStatusCore(binding.Slot, cancellationToken);
            if (!after.StorageHealthy || after.UploadOpen || after.LegacyFallback || after.Generation != binding.Generation + 1 ||
                after.Crc32 != crc || after.Bytes != frozen.Length || after.SchemaVersion != Get16(frozen, 4)) throw ProfileChangedError();
            var readback = await ProfileReadCore(after, cancellationToken);
            if (!readback.AsSpan().SequenceEqual(frozen) || binding.InstallationId != await ProfileIdentityCore(cancellationToken)) throw ProfileChangedError();
            progress?.Report(100);
            return new(after.Bind(binding.InstallationId), frozen.Length, true);
        }
        catch
        {
            // Only our own RAM upload can be abandoned. Once COMMIT was sent its
            // outcome must be established with a new read; it is never retried.
            if (beginSent && !commitSent)
            {
                try
                {
                    var abort = new byte[8]; Put32(abort, 0, owner); abort[4] = binding.Slot;
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var aborted = await Send(OnboardProtocol.ProfileAbort, abort, cleanup.Token);
                    if (!aborted.AsSpan().SequenceEqual(abort)) throw ProfileReplyError();
                }
                catch { /* The caller reports uncertain state; never retarget or write again. */ }
            }
            throw;
        }
        finally { _exclusive.Release(); }
    }
    private static bool SameProfile(OnboardHardwareProfileStatus a, OnboardHardwareProfileStatus b) =>
        a.DeviceId == b.DeviceId && a.Slot == b.Slot && a.Generation == b.Generation && a.Crc32 == b.Crc32 &&
        a.Bytes == b.Bytes && a.SchemaVersion == b.SchemaVersion && a.LegacyFallback == b.LegacyFallback &&
        a.StorageHealthy == b.StorageHealthy && a.Recovered == b.Recovered;
    private static void RequireSlot(byte slot) { if (slot is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(slot)); }
    private static InvalidDataException ProfileReplyError() => new(L.T("Der Gerätestand konnte nicht gelesen werden. Bitte erneut versuchen."));
    private static IOException ProfileChangedError() => new(L.T("Das Profil auf der Tastatur hat sich geändert. Bitte erneut auslesen."));
    private static ulong Get64(byte[] b, int i) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i));
    private static void Put64(byte[] b, int i, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i), value);
}
