using System.Buffers.Binary;
using System.Text.Json;
using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal sealed class AdapterEngine(Func<IBackend> createBackend)
{
    internal AdapterResponse Run(AdapterRequest request)
    {
        ValidateRequest(request);
        string directory = Files.NewSession(request.SessionDirectory);
        string sessionId = Path.GetFileName(directory);
        List<Artifact> artifacts = []; List<IDisposable> locks = [];
        Fingerprint? fingerprint = null; string? fingerprintHash = null;
        bool attempted = false, verified = false; Guid? installationId = null;
        IDevice? device = null;
        IBackend? backend = null;
        try
        {
            Files.New(directory, "request.json", Json.Bytes(request), "request");
            backend = createBackend();
            if (request.Operation == "list")
                return Save(new(1, request.Operation, sessionId, "ok", null, request.DeviceId, null, null, false, false, false, false, false, directory, [], backend.List()));
            Model model = Model.Get(request.DeviceId);
            device = backend.Open(request.Selection!, model, directory);
            fingerprint = device.Probe(model);
            fingerprintHash = Files.Hash(Json.Bytes(fingerprint));
            if (request.ExpectedFingerprintSha256 != null && request.ExpectedFingerprintSha256 != fingerprintHash) throw new InvalidDataException("DEVICE_FINGERPRINT_CHANGED");
            if (request.Operation == "probe")
                return Save(new(1, request.Operation, sessionId, "ok", null, model.DeviceId, fingerprint, fingerprintHash, true, true, false, false, false, directory, []));
            if (request.Operation == "capture")
            {
                var original = CapturePair("original", "original-read");
                ValidateVectors(original, model);
                SaveCapture();
                return Save(new(1, request.Operation, sessionId, "ok", null, model.DeviceId, fingerprint, fingerprintHash, true, true, false, false, false, directory, artifacts.ToArray()));
            }

            // All model/image/backup checks finish before the only write call.
            if (request.ExpectedFingerprintSha256 == null || request.ExpectedBackupSha256 == null || request.ExpectedInstallationId == null)
                throw new InvalidDataException("WRITE_BINDING_REQUIRED");
            byte[] captureBytes = Files.ReadBounded(request.BackupPath!, 1024 * 1024);
            var captureLock = Files.Lock(request.BackupPath!, captureBytes.Length, Files.Hash(captureBytes)); locks.Add(captureLock.Stream);
            var capture = JsonSerializer.Deserialize<CaptureManifest>(captureLock.Bytes, Json.Options) ?? throw new InvalidDataException("BACKUP_MANIFEST_NULL");
            if (capture.Schema != 1 || capture.DeviceId != model.DeviceId || capture.Artifacts.Length != 2 ||
                capture.Artifacts.Any(a => a.Kind != "original-read" || a.Bytes != model.FlashBytes || a.Sha256 != request.ExpectedBackupSha256) ||
                capture.Artifacts[0].Path == capture.Artifacts[1].Path || capture.FingerprintSha256 != Files.Hash(Json.Bytes(capture.Fingerprint)) ||
                capture.Fingerprint.Transport != model.Transport || capture.Fingerprint.FlashBytes != model.FlashBytes || capture.Fingerprint.SramBytes != model.SramBytes)
                throw new InvalidDataException("BACKUP_MANIFEST_MISMATCH");
            string backupRoot = Path.GetDirectoryName(Path.GetFullPath(request.BackupPath!))!;
            if (capture.Artifacts.Select(a => Files.Child(backupRoot, a.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
                throw new InvalidDataException("BACKUP_PAIR_ALIASES");
            byte[]? originalBytes = null;
            foreach (var artifact in capture.Artifacts)
            {
                var item = Files.Lock(Files.Child(backupRoot, artifact.Path), artifact.Bytes, artifact.Sha256); locks.Add(item.Stream);
                if (originalBytes != null && !originalBytes.AsSpan().SequenceEqual(item.Bytes)) throw new InvalidDataException("BACKUP_PAIR_MISMATCH");
                originalBytes = item.Bytes;
            }
            ValidateVectors(originalBytes!, model);
            if (InstallationIdentity.Read(originalBytes!) != null) throw new InvalidDataException("BACKUP_NOT_ORIGINAL");
            byte[] image;
            InstallationIdentity identity;
            string? targetVersion = null;
            if (request.Operation is "install" or "update")
            {
                if (request.Operation == "install" && capture.FingerprintSha256 != fingerprintHash) throw new InvalidDataException("CAPTURE_DEVICE_CHANGED");
                byte[] bundleBytes = Files.ReadBounded(request.BundlePath!, 1024 * 1024);
                var bundleLock = Files.Lock(request.BundlePath!, bundleBytes.Length, Files.Hash(bundleBytes)); locks.Add(bundleLock.Stream);
                var bundle = JsonSerializer.Deserialize<Bundle>(bundleLock.Bytes, Json.Options) ?? throw new InvalidDataException("BUNDLE_NULL");
                ValidateBundle(bundle, model);
                var imageLock = Files.Lock(Files.Child(Path.GetDirectoryName(Path.GetFullPath(request.BundlePath!))!, bundle.Image.Path), bundle.Image.Bytes, bundle.Image.Sha256); locks.Add(imageLock.Stream);
                image = imageLock.Bytes; ValidateVectors(image, model);
                identity = InstallationIdentity.Read(image) ?? throw new InvalidDataException("INSTALLATION_ID_REQUIRED");
                ValidateInstallationIdentity(identity, request, model);
                targetVersion = KnownFirmwareImages.Validate(image, model.DeviceId, (byte)model.BoardId);
                if (targetVersion != bundle.ReleaseId) throw new InvalidDataException("BUNDLE_RELEASE_MISMATCH");
                int offset = InstallationIdentity.Offset(model.FlashBytes);
                if (request.Operation == "install" && image.AsSpan(offset + InstallationIdentity.Size).ContainsAnyExcept((byte)0xFF)) throw new InvalidDataException("INITIAL_CONFIGURATION_NOT_ERASED");
            }
            else image = originalBytes!;

            // A new pair is always persisted again immediately before a write. Restore tolerates a
            // damaged application vector, but still needs the exact installation recovery identity.
            byte[] before = CapturePair("before", "pre-write-read");
            if (request.Operation == "install")
            {
                if (!before.AsSpan().SequenceEqual(originalBytes)) throw new InvalidDataException("ORIGINAL_CHANGED_SINCE_CAPTURE");
                identity = InstallationIdentity.Read(image)!;
            }
            else
            {
                identity = InstallationIdentity.Read(before) ?? throw new InvalidDataException("DEVICE_RECOVERY_ID_MISSING");
                ValidateInstallationIdentity(identity, request, model);
                if (fingerprint.Transport == "stm32-dfuse" && capture.Fingerprint.Serial != fingerprint.Serial) throw new InvalidDataException("RESTORE_USB_SERIAL_MISMATCH");
                if (before.AsSpan(InstallationIdentity.Offset(model.FlashBytes) + 64, 192).ContainsAnyExcept((byte)0xFF)) throw new InvalidDataException("RECOVERY_ID_PAGE_MISMATCH");
                if (request.Operation == "update")
                {
                    if (Files.Hash(before) != request.ExpectedCurrentSha256) throw new InvalidDataException("CURRENT_CHANGED_SINCE_CAPTURE");
                    string currentVersion = KnownFirmwareImages.Validate(before, model.DeviceId, (byte)model.BoardId);
                    if (Version.Parse(currentVersion) > Version.Parse(targetVersion!)) throw new InvalidDataException("FIRMWARE_DOWNGRADE_NOT_ALLOWED");
                    if (InstallationIdentity.Read(image) != identity) throw new InvalidDataException("UPDATE_RECOVERY_ID_CHANGED");
                    if (!before.AsSpan(model.FlashBytes - InstallationIdentity.StorageBytes).SequenceEqual(image.AsSpan(model.FlashBytes - InstallationIdentity.StorageBytes)))
                        throw new InvalidDataException("UPDATE_CONFIGURATION_CHANGED");
                }
            }
            installationId = identity.InstallationId;
            var fresh = artifacts.ToArray();
            foreach (var artifact in fresh)
                locks.Add(Files.Lock(Files.Child(directory, artifact.Path), artifact.Bytes, artifact.Sha256).Stream);
            var imageArtifact = Files.New(directory, "write-image.bin", image, "exact-write-image"); artifacts.Add(imageArtifact);
            locks.Add(Files.Lock(Files.Child(directory, imageArtifact.Path), imageArtifact.Bytes, imageArtifact.Sha256).Stream);
            Files.New(directory, "prepared.json", Json.Bytes(new { model.DeviceId, fingerprint, fingerprintSha256 = fingerprintHash, backupSha256 = request.ExpectedBackupSha256, installationId, writeImage = imageArtifact, writeAttempted = false }), "prepared-write-proof");
            if (device.Probe(model) != fingerprint) throw new InvalidDataException("PREWRITE_IDENTITY_CHANGED");
            device.WriteAll(model, image, () =>
            {
                if (!request.ConfirmWrite || attempted) throw new InvalidOperationException("EXPLICIT_WRITE_GATE_REQUIRED");
                attempted = true;
                Files.New(directory, "write-attempt.json", Json.Bytes(new { utc = DateTimeOffset.UtcNow, operation = request.Operation, imageArtifact.Sha256, installationId }), "write-attempt");
            });
            if (!attempted) throw new IOException("TRANSPORT_DID_NOT_MARK_WRITE");
            byte[] after = CapturePair("after", "verified-read");
            if (!after.AsSpan().SequenceEqual(image)) throw new IOException("FULL_READBACK_MISMATCH");
            if (device.Probe(model) != fingerprint) throw new IOException("POSTWRITE_IDENTITY_CHANGED");
            // Transport disposal and audit output are part of completion, never reported early.
            FlushDeviceLog(); device.Dispose(); device = null;
            Files.New(directory, "installation.json", Json.Bytes(new { schema = 1, operation = request.Operation, model.DeviceId, installationId, originalSha256 = request.ExpectedBackupSha256, originalCaptureManifest = Path.GetFullPath(request.BackupPath!), fingerprint, verified = true }), "installation-receipt");
            verified = true;
            return Save(new(1, request.Operation, sessionId, "verified", null, model.DeviceId, fingerprint, fingerprintHash, true, true, request.Operation != "restore", attempted, true, directory, artifacts.ToArray(), InstallationId: installationId));

            byte[] CapturePair(string prefix, string kind)
            {
                byte[] first = device!.ReadAll(model);
                if (first.Length != model.FlashBytes) throw new IOException("FULL_READ_LENGTH");
                artifacts.Add(Files.New(directory, prefix + "-1.bin", first, kind));
                byte[] second = device.ReadAll(model);
                if (second.Length != model.FlashBytes) throw new IOException("FULL_READ_LENGTH");
                artifacts.Add(Files.New(directory, prefix + "-2.bin", second, kind));
                if (!first.AsSpan().SequenceEqual(second)) throw new IOException("FULL_READ_PAIR_MISMATCH");
                if (device.Probe(model) != fingerprint) throw new IOException("CAPTURE_IDENTITY_CHANGED");
                return first;
            }
            void SaveCapture()
            {
                var manifest = new CaptureManifest(1, model.DeviceId, fingerprint!, fingerprintHash!, artifacts.ToArray(),
                    "MCU internal flash only", ["ROM bootloader", "option/protection bytes", "external SPI/EEPROM", "wireless module storage"]);
                artifacts.Add(Files.New(directory, "capture.json", Json.Bytes(manifest), "original-capture-manifest"));
            }
        }
        catch (Exception ex)
        {
            return Save(new(1, request.Operation, sessionId, attempted ? "failed" : "blocked", ex.Message, request.DeviceId,
                fingerprint, fingerprintHash, false, false, false, attempted, verified, directory, artifacts.ToArray(), Error: ex.GetType().Name));
        }
        finally
        {
            try { if (device != null) { FlushDeviceLog(); device.Dispose(); } }
            finally { backend?.Dispose(); foreach (IDisposable item in locks.AsEnumerable().Reverse()) item.Dispose(); }
        }
        void FlushDeviceLog()
        {
            if (device != null && !File.Exists(Path.Combine(directory, "commands.json")))
                Files.New(directory, "commands.json", Json.Bytes(device.Log), "raw-command-transcript");
        }
        AdapterResponse Save(AdapterResponse response)
        {
            FlushDeviceLog();
            Files.New(directory, "result.json", Json.Bytes(response), "adapter-result"); return response;
        }
    }

    internal static void ValidateRequest(AdapterRequest request)
    {
        if (request.Schema != 1 || request.Operation is not ("list" or "probe" or "capture" or "install" or "restore" or "update")) throw new InvalidDataException("REQUEST_INVALID");
        bool write = request.Operation is "install" or "restore" or "update";
        if (request.ConfirmWrite != write) throw new InvalidDataException("EXPLICIT_WRITE_CONFIRMATION_REQUIRED");
        if (request.Operation != "list" && (request.Selection == null || request.DeviceId == null)) throw new InvalidDataException("SELECTION_REQUIRED");
        if (write && (request.BackupPath == null || request.ExpectedBackupSha256 == null || request.ExpectedFingerprintSha256 == null || request.ExpectedInstallationId is null || request.ExpectedInstallationId == Guid.Empty)) throw new InvalidDataException("WRITE_BINDING_REQUIRED");
        if (request.Operation is "install" or "update" && request.BundlePath == null) throw new InvalidDataException("BUNDLE_REQUIRED");
        if ((request.Operation == "update") != (request.ExpectedCurrentSha256 is not null)) throw new InvalidDataException("CURRENT_IMAGE_BINDING_REQUIRED");
        foreach (string? hash in new[] { request.ExpectedBackupSha256, request.ExpectedFingerprintSha256, request.ExpectedCurrentSha256 })
            if (hash != null && (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || hash != hash.ToUpperInvariant())) throw new InvalidDataException("SHA256_INVALID");
    }
    internal static void ValidateVectors(byte[] image, Model model)
    {
        if (image.Length != model.FlashBytes || image.Length < 8) throw new InvalidDataException("IMAGE_LENGTH");
        uint stack = BinaryPrimitives.ReadUInt32LittleEndian(image), reset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4));
        if (stack <= 0x20000000 || stack > 0x20000000UL + (uint)model.SramBytes || stack % 4 != 0 || (reset & 1) == 0 || (reset & ~1U) < Model.FlashBase || (reset & ~1U) >= Model.FlashBase + (uint)model.FlashBytes)
            throw new InvalidDataException("IMAGE_VECTORS");
    }
    internal static void ValidateBundle(Bundle bundle, Model model)
    {
        if (bundle.Schema != 1 || bundle.DeviceId != model.DeviceId || bundle.BoardId != model.BoardId || bundle.Transport != model.Transport || bundle.FlashBase != Model.FlashBase || bundle.FlashBytes != model.FlashBytes || bundle.SramBytes != model.SramBytes || bundle.Image.Bytes != model.FlashBytes || string.IsNullOrWhiteSpace(bundle.ReleaseId))
            throw new InvalidDataException("BUNDLE_MODEL_MISMATCH");
    }
    private static void ValidateInstallationIdentity(InstallationIdentity identity, AdapterRequest request, Model model)
    {
        if (identity.BoardId != model.BoardId || identity.InstallationId != request.ExpectedInstallationId || identity.OriginalSha256 != request.ExpectedBackupSha256) throw new InvalidDataException("INSTALLATION_IDENTITY_MISMATCH");
    }
}
