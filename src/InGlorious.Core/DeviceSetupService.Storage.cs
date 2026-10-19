using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace InGlorious.Core;

public sealed partial class DeviceSetupService
{
    private sealed record SetupImage(string DeviceId, byte BoardId, string File, int Bytes, string Sha256,
        string Transport, int FlashBytes, int SramBytes, string? ReleaseId = null, int ApplicationOffset = 0, string? StorageLayout = null);
    private sealed record ImageCatalog(int Schema, string ReleaseId, SetupImage[] Targets);
    private sealed record RecoveryRecord(int Schema, string DeviceId, Guid InstallationId, string OriginalSha256,
        string BackupManifest, string BackupManifestSha256, ushort OriginalVendor, ushort OriginalProduct);
    public static string TargetFirmwareVersion => ReadImageCatalog().ReleaseId;
    public static string TargetFirmwareVersionFor(string deviceId)
    {
        var catalog = ReadImageCatalog();
        var target = catalog.Targets.SingleOrDefault(item => item.DeviceId == deviceId)
            ?? throw Error("Für dieses Modell ist noch keine INGLORIOUS-Firmware verfügbar.");
        var value = target.ReleaseId ?? catalog.ReleaseId;
        if (!FirmwareSemanticVersion.TryParse(value, out _)) throw Error("Das Firmware-Paket ist nicht gültig.");
        return value;
    }

    public async Task<SetupProgress> CaptureAsync(SetupSession session, IProgress<string>? log = null)
    {
        await _gate.WaitAsync();
        try
        {
            AssertSession(session);
            if (session.Stage != SetupStage.ReadyToBackup || session.Consumed) throw Error("Starte die Einrichtung erneut.");
            await CheckBoundBootloader(session);
            log?.Report(L.T("Die Firmware wird zweimal vollständig gelesen und als Sicherung geprüft."));
            var response = await Adapter(session, "capture");
            RequireSuccess(response);
            ValidateAdapterIdentity(response, session, "capture");
            var evidence = RequiredEvidenceDirectory(response, session.Directory);
            var reads = response.GetProperty("artifacts").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "original-read").ToArray();
            if (reads.Length != 2) throw Error("Die Sicherungen stimmen nicht überein. Es wird nichts installiert.");
            RequireDistinctReads(evidence, reads);
            var images = reads.Select(item => ReadCheckedArtifact(evidence, item, FlashBytesFor(session.DeviceId!))).ToArray();
            if (!images[0].AsSpan().SequenceEqual(images[1])) throw Error("Die Sicherungen stimmen nicht überein. Es wird nichts installiert.");
            var current = images[0];
            var currentHash = Convert.ToHexString(SHA256.HashData(current));
            session.CurrentSha256 = currentHash;
            var capturePath = ContainedFile(evidence, "capture.json");
            if (!File.Exists(capturePath)) throw Error("Die Sicherung konnte nicht dauerhaft gespeichert werden.");
            session.BackupDirectory = evidence;
            if (session.Operation == SetupOperation.Install)
            {
                if (InstallationIdentity.Read(current) is not null)
                    throw Error("INGLORIOUS ist bereits installiert. Wähle bei Bedarf die Wiederherstellung.");
                StockFirmwareInspector.Validate(current, session.Device.VendorId, session.Device.ProductId, session.DeviceId!);
                var board = BoardDescriptorRegistry.Get(session.DeviceId!);
                session.Installation = new(board.BoardId, Guid.NewGuid(), currentHash);
                session.OriginalSha256 = currentHash;
                session.BackupPath = capturePath;
                session.BundlePath = PrepareBundle(session);
                // The original recovery archive is durable before any authorized write begins.
                var record = new RecoveryRecord(1, session.DeviceId!, session.Installation.InstallationId, currentHash,
                    Path.GetRelativePath(session.Directory, capturePath), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(capturePath))),
                    session.Device.VendorId, session.Device.ProductId);
                WriteNew(Path.Combine(session.Directory, "recovery.json"), JsonSerializer.SerializeToUtf8Bytes(record, Json));
            }
            else
            {
                var identity = InstallationIdentity.Read(current);
                if (identity is null && session.Operation == SetupOperation.Update)
                {
                    PrepareLegacyMigration(session, current);
                    session.BundlePath = PrepareBundle(session, current);
                }
                else
                {
                    if (identity is null || identity.BoardId != BoardDescriptorRegistry.Get(session.DeviceId!).BoardId)
                        throw Error("Für diese Installation wurde keine passende Original-Sicherung gefunden.");
                    var recordPath = FindRecovery(identity, session.DeviceId!);
                    var record = ReadRecovery(recordPath);
                    var archiveDirectory = Path.GetDirectoryName(recordPath)!;
                    var originalManifest = ContainedFile(archiveDirectory, record.BackupManifest);
                    if (!HashEquals(File.ReadAllBytes(originalManifest), record.BackupManifestSha256))
                        throw Error("Die Original-Sicherung wurde verändert. Die Wiederherstellung wurde angehalten.");
                    session.Installation = identity;
                    session.OriginalSha256 = record.OriginalSha256;
                    session.BackupPath = originalManifest;
                    session.OriginalVendor = record.OriginalVendor;
                    session.OriginalProduct = record.OriginalProduct;
                    ExportCurrentHardwareProfiles(session, current);
                    if (session.Operation == SetupOperation.Update)
                    {
                        try { session.InstalledFirmwareVersion = KnownFirmwareImages.Validate(current, session.DeviceId!, identity.BoardId); }
                        catch (InvalidDataException) { throw Error("Die installierte Firmware ist nicht bekannt. Das Update wurde angehalten."); }
                        if (Version.Parse(session.InstalledFirmwareVersion) > Version.Parse(TargetFirmwareVersionFor(session.DeviceId!)))
                            throw Error("Die installierte Firmware ist neuer als das verfügbare Update.");
                        session.BundlePath = PrepareBundle(session, current);
                    }
                }
            }
            session.Stage = SetupStage.ReadyToInstall;
            log?.Report(L.T("Beide vollständigen Sicherungen stimmen überein."));
            return Progress(session);
        }
        catch (SetupException error) when (error.Recoverable) { throw; }
        catch
        {
            session.Stage = SetupStage.Blocked;
            throw;
        }
        finally { _gate.Release(); }
    }

    public Task<SetupProgress> InstallAsync(SetupSession session, bool explicitlyConfirmed, IProgress<string>? log = null) =>
        WriteAsync(session, SetupOperation.Install, explicitlyConfirmed, log);
    public Task<SetupProgress> RestoreAsync(SetupSession session, bool explicitlyConfirmed, IProgress<string>? log = null) =>
        WriteAsync(session, SetupOperation.Restore, explicitlyConfirmed, log);
    public Task<SetupProgress> UpdateAsync(SetupSession session, bool explicitlyConfirmed, IProgress<string>? log = null) =>
        WriteAsync(session, SetupOperation.Update, explicitlyConfirmed, log);

    private async Task<SetupProgress> WriteAsync(SetupSession session, SetupOperation operation, bool explicitlyConfirmed, IProgress<string>? log)
    {
        await _gate.WaitAsync();
        try
        {
            AssertSession(session);
            if (!explicitlyConfirmed || session.Consumed || session.Stage != SetupStage.ReadyToInstall || session.Operation != operation ||
                session.Installation is null || session.BackupPath is null) throw Error("Bestätige zuerst die Installation für das erkannte Gerät.");
            await CheckBoundBootloader(session);
            session.Consumed = true; // No automatic or duplicate retry, including uncertain outcomes.
            session.OutcomeUncertain = true;
            log?.Report(L.T("USB angeschlossen und INGLORIOUS Advanced geöffnet lassen."));
            var adapterOperation = operation switch { SetupOperation.Install => "install", SetupOperation.Restore => "restore", _ => "update" };
            var response = await Adapter(session, adapterOperation, confirmWrite: true);
            // The adapter can reject a concurrent operation before opening USB,
            // so this particular refusal has no observed fingerprint or write.
            if (response.GetProperty("schema").GetInt32() == 1 && response.GetProperty("operation").GetString() == adapterOperation &&
                response.GetProperty("deviceId").GetString() == session.DeviceId && response.GetProperty("status").GetString() == "blocked" &&
                response.TryGetProperty("errorCode", out var busyCode) && busyCode.GetString() == "DEVICE_OPERATION_BUSY" &&
                !response.GetProperty("writeAttempted").GetBoolean() && !response.GetProperty("verified").GetBoolean())
            {
                session.OutcomeUncertain = false;
                throw Error("Die Firmware konnte nicht sicher geprüft werden. Es wurde nichts auf die Tastatur geschrieben.");
            }
            ValidateAdapterIdentity(response, session, adapterOperation);
            session.WriteAttempted = response.GetProperty("writeAttempted").GetBoolean();
            if (response.GetProperty("status").GetString() == "blocked" && !session.WriteAttempted && !response.GetProperty("verified").GetBoolean())
            {
                session.OutcomeUncertain = false;
                throw Error(response.TryGetProperty("errorCode", out var code) ? code.GetString() switch
                {
                    "CURRENT_CHANGED_SINCE_CAPTURE" => "Die Firmware hat sich seit der Prüfung verändert. Starte das Update erneut.",
                    "UNKNOWN_INSTALLED_FIRMWARE" => "Die installierte Firmware ist nicht bekannt. Das Update wurde angehalten.",
                    "UPDATE_CONFIGURATION_CHANGED" => "Die Einstellungen konnten nicht unverändert übernommen werden. Es wurde keine Firmware geschrieben.",
                    _ => "Die Firmware konnte nicht sicher geprüft werden. Es wurde nichts auf die Tastatur geschrieben."
                } : "Die Firmware konnte nicht sicher geprüft werden. Es wurde nichts auf die Tastatur geschrieben.");
            }
            if (response.GetProperty("status").GetString() != "verified" || !response.GetProperty("verified").GetBoolean() ||
                !response.GetProperty("writeAttempted").GetBoolean())
                throw Error("Das Firmware-Ergebnis ist nicht bestätigt. Lasse das Gerät im Update-Modus.");
            if (response.GetProperty("installationId").GetGuid() != session.Installation.InstallationId)
                throw Error("Das Firmware-Ergebnis gehört nicht zu dieser Installation.");
            var evidence = RequiredEvidenceDirectory(response, session.Directory);
            var readbacks = response.GetProperty("artifacts").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "verified-read").ToArray();
            if (readbacks.Length != 2) throw Error("Die vollständige Rückprüfung der Firmware fehlt.");
            RequireDistinctReads(evidence, readbacks);
            if (operation == SetupOperation.Update)
            {
                var before = response.GetProperty("artifacts").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "pre-write-read").ToArray();
                if (before.Length != 2) throw Error("Die vollständige Rückprüfung der Firmware fehlt.");
                RequireDistinctReads(evidence, before);
                foreach (var artifact in before)
                    if (!HashEquals(ReadCheckedArtifact(evidence, artifact, FlashBytesFor(session.DeviceId!)), session.CurrentSha256!))
                        throw Error("Die Firmware hat sich seit der Prüfung verändert. Starte das Update erneut.");
            }
            var expectedHash = operation != SetupOperation.Restore
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(session.Directory, "installation.bin"))))
                : session.OriginalSha256!;
            foreach (var artifact in readbacks)
            {
                var bytes = ReadCheckedArtifact(evidence, artifact, FlashBytesFor(session.DeviceId!));
                if (!HashEquals(bytes, expectedHash)) throw Error("Die geschriebene Firmware stimmt nicht mit dem Ziel überein.");
            }
            session.Stage = SetupStage.Reconnect;
            session.OutcomeUncertain = false;
            log?.Report(L.T("Die Firmware wurde geschrieben und zweimal vollständig geprüft."));
            return Progress(session);
        }
        catch (SetupException error) when (error.Recoverable) { throw; }
        catch
        {
            session.Stage = SetupStage.Blocked;
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task CheckBoundBootloader(SetupSession session)
    {
        await AssertCoreClosed();
        var boot = (await _environment.ScanAsync()).Where(IsBootloader).ToArray();
        if (boot.Length != 1 || session.Bootloader is null || boot[0].InstanceId != session.Bootloader.InstanceId ||
            !SamePort(boot[0].LocationPaths, session.Device.LocationPaths) || !HasUsableUsbTransport(boot[0]))
            throw Error("Die USB-Verbindung hat sich geändert. Starte die Einrichtung erneut.");
    }

    private static void ValidateAdapterIdentity(JsonElement response, SetupSession session, string operation)
    {
        if (response.GetProperty("schema").GetInt32() != 1 || response.GetProperty("operation").GetString() != operation ||
            response.GetProperty("deviceId").GetString() != session.DeviceId ||
            response.GetProperty("fingerprintSha256").GetString() != session.FingerprintSha256)
            throw Error("Der Prüfbericht gehört nicht zum erkannten Gerät.");
    }

    private static ImageCatalog ReadImageCatalog()
    {
        var resourceName = typeof(DeviceSetupService).Assembly.GetManifestResourceNames().Single(name => name.EndsWith(".SetupFirmwareCatalog.json", StringComparison.Ordinal));
        using var resource = typeof(DeviceSetupService).Assembly.GetManifestResourceStream(resourceName)!;
        var catalog = JsonSerializer.Deserialize<ImageCatalog>(resource, new JsonSerializerOptions(Json) { PropertyNameCaseInsensitive = true })!;
        if (catalog.Schema != 1 || !Version.TryParse(catalog.ReleaseId, out var version) || version.Build < 0 || version.Revision >= 0)
            throw Error("Das Firmware-Paket ist nicht gültig.");
        return catalog;
    }
    private string PrepareBundle(SetupSession session, byte[]? current = null)
    {
        var catalog = ReadImageCatalog();
        var target = catalog.Targets.Single(item => item.DeviceId == session.DeviceId);
        if (target.FlashBytes != FlashBytesFor(target.DeviceId) || target.SramBytes != SramBytesFor(target.DeviceId) || target.Transport != TransportFor(target.DeviceId) ||
            target.BoardId != BoardDescriptorRegistry.Get(target.DeviceId).BoardId)
            throw Error("Das Firmware-Paket passt nicht zu diesem Modell.");
        RetainedLoaderLayout.ValidateLayout(target.DeviceId, target.BoardId, target.FlashBytes, target.ApplicationOffset);
        ProfileBankLayout.ValidateGeometry(target.DeviceId, target.BoardId, target.FlashBytes, target.ApplicationOffset, target.Bytes, target.StorageLayout);
        var source = ContainedFile(_packageRoot, target.File);
        var bytes = File.ReadAllBytes(source);
        if (bytes.Length != target.Bytes || !HashEquals(bytes, target.Sha256)) throw Error("Das Firmware-Paket ist beschädigt oder unvollständig.");
        var original = target.ApplicationOffset == 0 ? Array.Empty<byte>() : ReadOriginalForBundle(session);
        var full = InstallationIdentity.CreateImage(bytes, target.FlashBytes, session.Installation!, original, target.StorageLayout);
        if (current is not null)
        {
            if (session.MigrateLegacy) LegacyFirmwareMigration.ValidateImage(current, target.DeviceId);
            if (current.Length != target.FlashBytes || (!session.MigrateLegacy && InstallationIdentity.Read(current) != session.Installation))
                throw Error("Das Firmware-Paket passt nicht zu diesem Modell.");
            var previousLayout = session.MigrateLegacy ? null : KnownFirmwareImages.GetStorageLayout(current, target.DeviceId, target.BoardId);
            ProfileBankLayout.Preserve(current, full, previousLayout, target.StorageLayout);
        }
        // The target must itself be in the retained approved-image catalog, including its release ID.
        var releaseId = TargetFirmwareVersionFor(target.DeviceId);
        if (KnownFirmwareImages.Validate(full, target.DeviceId, target.BoardId) != releaseId)
            throw Error("Das Firmware-Paket ist nicht gültig.");
        if (KnownFirmwareImages.GetStorageLayout(full, target.DeviceId, target.BoardId) != target.StorageLayout)
            throw Error("Das Firmware-Paket ist nicht gültig.");
        var imagePath = Path.Combine(session.Directory, "installation.bin");
        WriteNew(imagePath, full);
        var bundlePath = Path.Combine(session.Directory, "bundle.json");
        var bundle = new
        {
            schema = 1, deviceId = target.DeviceId, boardId = target.BoardId, transport = target.Transport,
            flashBase = 0x08000000, flashBytes = target.FlashBytes, sramBytes = target.SramBytes,
            image = new { path = "installation.bin", bytes = full.Length, sha256 = Convert.ToHexString(SHA256.HashData(full)) },
            releaseId
        };
        WriteNew(bundlePath, JsonSerializer.SerializeToUtf8Bytes(bundle, Json));
        return bundlePath;
    }

    private static void ExportCurrentHardwareProfiles(SetupSession session, byte[] current)
    {
        if (session.DeviceId != "gmmk3-pro-100-iso") return;
        string? layout;
        try { layout = KnownFirmwareImages.GetStorageLayout(current, session.DeviceId, 16); }
        catch (InvalidDataException)
        {
            // Restore remains possible for damaged/unknown installations. The
            // two complete verified current reads already preserve every byte.
            return;
        }
        if (layout != ProfileBankLayout.Name) return;
        foreach (var profile in ProfileBankLayout.Inspect(current.AsSpan(ProfileBankLayout.Start, ProfileBankLayout.Length)).Profiles)
        {
            AppProject project;
            try
            {
                project = OnboardSerializer.Decode(profile.Payload);
                if (project.DeviceId != session.DeviceId || OnboardSerializer.Get16(profile.Payload, 4) != profile.Schema) continue;
            }
            catch (InvalidDataException) { continue; }
            var stem = Path.Combine(session.Directory, $"profile-{profile.Slot}-{Guid.NewGuid():N}");
            WriteNew(stem + ".igcfg.bin", profile.Payload);
            if (!File.ReadAllBytes(stem + ".igcfg.bin").AsSpan().SequenceEqual(profile.Payload)) throw new IOException("PROFILE_EXPORT_READBACK_FAILED");
            project.Name = "Profile " + profile.Slot;
            ProjectService.Save(stem + ".inglorious.json", project);
        }
    }

    private static byte[] ReadOriginalForBundle(SetupSession session)
    {
        var path = session.BackupPath ?? throw Error("Für diese Installation wurde keine passende Original-Sicherung gefunden.");
        AssertNoLinks(path);
        if (new FileInfo(path).Length > 65536) throw Error("Die Sicherungszuordnung ist ungültig.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = manifest.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("deviceId").GetString() != session.DeviceId)
            throw Error("Die Original-Sicherung passt nicht zu dieser Installation.");
        var records = root.GetProperty("artifacts").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "original-read").ToArray();
        if (records.Length != 2) throw Error("Die Sicherungen stimmen nicht überein. Es wird nichts installiert.");
        var directory = Path.GetDirectoryName(path)!;
        RequireDistinctReads(directory, records);
        var first = ReadCheckedArtifact(directory, records[0], FlashBytesFor(session.DeviceId!));
        var second = ReadCheckedArtifact(directory, records[1], FlashBytesFor(session.DeviceId!));
        if (!first.AsSpan().SequenceEqual(second) || session.Installation is null || !session.Installation.MatchesOriginal(first))
            throw Error("Die Original-Sicherung passt nicht zu dieser Installation.");
        return first;
    }

    private string FindRecovery(InstallationIdentity identity, string deviceId)
    {
        var matching = new List<string>();
        AssertNoLinks(_archiveRoot);
        foreach (var directory in System.IO.Directory.EnumerateDirectories(_archiveRoot))
        {
            AssertNoLinks(directory);
            var path = Path.Combine(directory, "recovery.json");
            if (!File.Exists(path)) continue;
            var record = ReadRecovery(path);
            if (record.InstallationId == identity.InstallationId)
            {
                if (record.DeviceId != deviceId || record.OriginalSha256 != identity.OriginalSha256)
                    throw Error("Die Original-Sicherung passt nicht zu dieser Installation.");
                matching.Add(path);
            }
        }
        if (matching.Count != 1) throw Error("Für diese Installation wurde keine eindeutige Original-Sicherung gefunden.");
        return matching[0];
    }
    private static RecoveryRecord ReadRecovery(string path)
    {
        AssertNoLinks(path);
        if (new FileInfo(path).Length > 65536) throw Error("Die Sicherungszuordnung ist ungültig.");
        var record = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllBytes(path), new JsonSerializerOptions(Json) { PropertyNameCaseInsensitive = true });
        if (record is null || record.Schema != 1 || record.InstallationId == Guid.Empty || record.OriginalSha256.Length != 64 ||
            record.BackupManifestSha256.Length != 64 || !BoardDescriptorRegistry.TryGet(record.DeviceId, out _)) throw Error("Die Sicherungszuordnung ist ungültig.");
        return record;
    }
    private static byte[] ReadCheckedArtifact(string directory, JsonElement artifact, int expectedBytes)
    {
        var path = ContainedFile(directory, artifact.GetProperty("path").GetString()!);
        if (artifact.GetProperty("bytes").GetInt32() != expectedBytes || new FileInfo(path).Length != expectedBytes)
            throw Error("Die Sicherung ist unvollständig.");
        var bytes = File.ReadAllBytes(path);
        if (!HashEquals(bytes, artifact.GetProperty("sha256").GetString()!)) throw Error("Die Sicherung ist beschädigt.");
        return bytes;
    }
    private static void RequireDistinctReads(string directory, JsonElement[] artifacts)
    {
        if (artifacts.Select(item => ContainedFile(directory, item.GetProperty("path").GetString()!))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
            throw Error("Die vollständige Rückprüfung der Firmware fehlt.");
    }
    private static string RequiredEvidenceDirectory(JsonElement response, string root)
    {
        var path = Path.GetFullPath(response.GetProperty("evidenceDirectory").GetString()!);
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw Error("Der Prüfbericht liegt außerhalb des Sicherungsordners.");
        AssertNoLinks(path); return path;
    }
    internal static string ContainedFile(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (Path.IsPathRooted(relative) || relative.Contains(':') || !path.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw Error("Ungültiger Pfad im Firmware-Paket.");
        AssertNoLinks(path); return path;
    }
    private static bool HashEquals(ReadOnlySpan<byte> bytes, string expected) =>
        expected.Length == 64 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expected));
    private static void WriteNew(string path, byte[] bytes)
    {
        AssertNoLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
}
