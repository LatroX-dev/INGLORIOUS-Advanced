using System.IO;
using System.Text.Json;

namespace InGlorious.Core;

public sealed partial class TransferWindow
{
    private async Task TransferHardwareProfileAsync(VerifiedTarget target)
    {
        if (_profileBinding is null) return;
        ClearConfirmation(invalidated: true); _reconnectRequired = false;
        SetBusy(true, L.T("Bisheriger Gerätestand wird vor der Übertragung gesichert …"));
        bool applyStarted = false; int revision = ++_progressRevision;
        try
        {
            await using var client = OnboardClient.Open(target.Device);
            var previous = await client.ReadHardwareProfileAsync(_profileBinding.Slot);
            if (previous.Binding != _profileBinding || previous.Status.ActiveSlot != _profileBinding.Slot || previous.Status.SwitchPending)
                throw new IOException("Bound hardware profile has changed.");
            await SaveHardwareProfileBackupAsync(previous);
            var status = await client.GetHardwareProfileStatusAsync(_profileBinding.Slot);
            if (status is null || status.ActiveSlot != _profileBinding.Slot || status.SwitchPending || status.UploadOpen ||
                status.Generation != _profileBinding.Generation || status.Crc32 != _profileBinding.Crc32)
                throw new IOException("Selected hardware profile changed before transfer.");
            _progressActive = true; _progress.IsIndeterminate = false; _progress.Value = 0;
            applyStarted = true;
            var result = await client.ApplyHardwareProfileAsync(_image, _profileBinding, new Progress<int>(value => ReportProgress(value, revision)), requireActiveSlot: true);
            var verified = await client.ReadHardwareProfileAsync(_profileBinding.Slot);
            if (!result.ReadbackVerified || verified.Binding != result.Binding || !verified.Payload.AsSpan().SequenceEqual(_image.Payload))
                throw new IOException("Hardware profile readback changed after commit.");
            AcceptHardwareProfileSnapshot(verified);
            await FinishHardwareProfileReconnectAsync(client, verified);
            CompleteVerifiedOperation();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError(error.ToString()); RejectTransferResult(applyStarted);
            _transferStatus.Text = HardwareProfileFailureMessage(error, applyStarted);
        }
        finally { _progressActive = false; FinishOperation(); }
    }

    private async Task ReadHardwareProfileRecoveryAsync(VerifiedTarget target)
    {
        if (_profileBinding is null) return;
        ClearConfirmation(invalidated: true);
        SetBusy(true, L.T("Aktuelle Konfiguration wird vollständig gelesen und geprüft …"));
        try
        {
            await using var client = OnboardClient.Open(target.Device);
            var snapshot = await client.ReadHardwareProfileAsync(_profileBinding.Slot);
            if (snapshot.InstallationId != _profileBinding.InstallationId) throw new IOException("Recovery installation mismatch.");
            AcceptHardwareProfileSnapshot(snapshot);
            if (DeviceSaveVerified)
            {
                await FinishHardwareProfileReconnectAsync(client, snapshot);
                CompleteVerifiedOperation();
            }
            else if (!snapshot.Status.StorageHealthy)
            {
                _hardwareProfileReloadRequired = true;
                _transferStatus.Text = L.T("Speichern konnte nicht bestätigt werden. Verbinde die Tastatur normal neu und lies das Profil erneut aus.");
            }
            else if (snapshot.Binding != _profileBinding)
            {
                _hardwareProfileReloadRequired = true;
                _transferStatus.Text = L.T("Das gespeicherte Profil hat sich geändert. Öffne den gelesenen Stand als Profil oder schließe dieses Fenster und lies die Tastatur erneut aus.");
            }
            else _transferStatus.Text = L.T("Der Gerätestand unterscheidet sich vom Profil. Du kannst ihn als Profil öffnen oder erneut übertragen.");
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError(error.ToString()); ClearConfirmation(invalidated: true);
            _transferStatus.Text = HardwareProfileFailureMessage(error, attempted: true);
        }
        finally { FinishOperation(); }
    }
    private string HardwareProfileFailureMessage(Exception error, bool attempted)
    {
        if (error is OnboardCommandException { Status: 7 })
        {
            _hardwareProfileReloadRequired = true;
            return L.T("Speichern konnte nicht bestätigt werden. Verbinde die Tastatur normal neu und lies das Profil erneut aus.");
        }
        return attempted ? L.T("Das Ergebnis ist unklar. Prüfe zuerst den Gerätestand.")
            : L.T("Das Profil auf der Tastatur hat sich geändert. Bitte erneut auslesen.");
    }
    private async Task FinishHardwareProfileReconnectAsync(OnboardClient client, OnboardHardwareProfileSnapshot snapshot)
    {
        try
        {
            await client.ReconnectHardwareProfileUsbAsync(snapshot.Binding);
            CompletionNotice = null;
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError(error.ToString());
            // A verified persistent save stays successful even if the profile
            // moved or the USB acknowledgement was lost. Never retry blindly.
            CompletionNotice = L.T("Die Einstellungen sind gespeichert. Die aktive Abfragerate konnte nicht bestätigt werden.");
        }
    }
    private void AcceptHardwareProfileSnapshot(OnboardHardwareProfileSnapshot snapshot)
    {
        if (_profileBinding is null || snapshot.InstallationId != _profileBinding.InstallationId || snapshot.Status.Slot != _profileBinding.Slot ||
            snapshot.Status.DeviceId != _project.DeviceId) throw new InvalidDataException("Hardware profile binding mismatch.");
        _readProject = OnboardSerializer.Decode(snapshot.Payload);
        DeviceProfileSnapshot = snapshot;
        DeviceSaveVerified = snapshot.Status.StorageHealthy && !snapshot.Status.UploadOpen && snapshot.Payload.AsSpan().SequenceEqual(_image.Payload);
        _verificationInvalidated = !DeviceSaveVerified; _outcomeUncertain = false;
    }
    private static async Task SaveHardwareProfileBackupAsync(OnboardHardwareProfileSnapshot snapshot)
    {
        var directory = Path.GetFullPath(Path.Combine(Paths.DataRoot, "device-backups"));
        AssertBackupPath(directory); Directory.CreateDirectory(directory); AssertBackupPath(directory);
        string stem = Path.Combine(directory, $"{snapshot.Status.DeviceId}-profile-{snapshot.Status.Slot}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        await using (var file = new FileStream(stem + ".igcfg.bin", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        { await file.WriteAsync(snapshot.Payload); file.Flush(true); }
        var saved = await File.ReadAllBytesAsync(stem + ".igcfg.bin");
        if (!saved.AsSpan().SequenceEqual(snapshot.Payload)) throw new IOException("Hardware-profile backup readback mismatch.");
        var audit = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, snapshot.Binding, snapshot.Status.SchemaVersion, snapshot.Status.Bytes });
        await using var record = new FileStream(stem + ".json", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await record.WriteAsync(audit); record.Flush(true);
    }
    private static void AssertBackupPath(string path)
    {
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
            if (item.Exists && ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null))
                throw new IOException("Backup directory must not contain links.");
    }
}
