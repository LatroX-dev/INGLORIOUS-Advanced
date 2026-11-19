using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace InGlorious.Core;

public sealed partial class DeviceSetupService
{
    private sealed record LegacyRecoveryRecord(int Schema, string DeviceId, string OriginalSha256,
        string BackupManifest, string BackupManifestSha256, ushort OriginalVendor, ushort OriginalProduct);
    private sealed record LegacyOriginal(LegacyRecoveryRecord Record, string CapturePath, byte[] Manifest, byte[][] Reads);

    private void PrepareLegacyMigration(SetupSession session, byte[] current, string capturePath, JsonElement fingerprint)
    {
        try { LegacyFirmwareMigration.ValidateImage(current, session.DeviceId!); }
        catch (InvalidDataException) { throw Error("Die installierte Firmware ist nicht bekannt. Das Update wurde angehalten."); }
        var serial = session.Selection.GetProperty("serial").GetString() ?? "";
        ValidateLegacyDevice(session, fingerprint, serial);

        var original = FindLegacyOriginal(session, serial);
        session.MigrateLegacy = true;
        session.InstalledFirmwareVersion = LegacyFirmwareMigration.Version;
        var installationId = Guid.NewGuid();
        if (original is null)
        {
            session.Installation = InstallationIdentity.CreateLegacyWithoutOriginal(installationId, serial);
            session.BackupPath = capturePath;
            return;
        }

        string backupDirectory = Path.Combine(session.Directory, "original-recovery");
        System.IO.Directory.CreateDirectory(backupDirectory);
        using (var document = JsonDocument.Parse(original.Manifest))
        {
            int index = 0;
            foreach (var read in document.RootElement.GetProperty("artifacts").EnumerateArray())
                WriteNew(ContainedFile(backupDirectory, read.GetProperty("path").GetString()!), original.Reads[index++]);
        }
        session.BackupPath = Path.Combine(backupDirectory, "capture.json");
        WriteNew(session.BackupPath, original.Manifest);
        session.OriginalSha256 = original.Record.OriginalSha256;
        session.OriginalVendor = original.Record.OriginalVendor;
        session.OriginalProduct = original.Record.OriginalProduct;
        session.Installation = new(1, installationId, session.OriginalSha256);
        WriteNew(Path.Combine(session.Directory, "recovery.json"), JsonSerializer.SerializeToUtf8Bytes(new RecoveryRecord(1,
            session.DeviceId!, installationId, session.OriginalSha256, Path.GetRelativePath(session.Directory, session.BackupPath),
            Convert.ToHexString(SHA256.HashData(original.Manifest)), session.OriginalVendor, session.OriginalProduct), Json));
    }

    private static void ValidateLegacyDevice(SetupSession session, JsonElement fingerprint, string serial)
    {
        if (session.DeviceId != "pro-iso" ||
            !string.Equals(session.Selection.GetProperty("serial").GetString(), serial, StringComparison.OrdinalIgnoreCase) ||
            !HashEquals(JsonSerializer.SerializeToUtf8Bytes(fingerprint, Json), session.FingerprintSha256))
            throw Error("Gerät nicht eindeutig erkannt.");
        try { LegacyFirmwareMigration.ValidateFingerprint(fingerprint, serial); }
        catch (InvalidDataException) { throw Error("Gerät nicht eindeutig erkannt."); }
    }

    private void UseLegacyOriginalForRestore(SetupSession session, string serial)
    {
        var original = FindLegacyOriginal(session, serial) ?? throw Error("Für diese Installation wurde keine passende Original-Sicherung gefunden.");
        session.BackupPath = original.CapturePath;
        session.OriginalSha256 = original.Record.OriginalSha256;
        session.OriginalVendor = original.Record.OriginalVendor;
        session.OriginalProduct = original.Record.OriginalProduct;
    }

    private LegacyOriginal? FindLegacyOriginal(SetupSession session, string serial)
    {
        try
        {
            LegacyOriginal? match = null;
            AssertNoLinks(_archiveRoot);
            foreach (var directory in System.IO.Directory.EnumerateDirectories(_archiveRoot))
            {
                var path = Path.Combine(directory, "legacy-recovery.json");
                AssertNoLinks(path);
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("LEGACY_ARCHIVE_INVALID");
                var record = JsonSerializer.Deserialize<LegacyRecoveryRecord>(File.ReadAllBytes(path), new JsonSerializerOptions(Json) { PropertyNameCaseInsensitive = true });
                if (record is null || record.Schema != 1 || record.DeviceId != "pro-iso" || record.OriginalVendor != 0x320F || record.OriginalProduct != 0x5046)
                    throw new InvalidDataException("LEGACY_ARCHIVE_INVALID");
                var capturePath = ContainedFile(directory, record.BackupManifest);
                if (new FileInfo(capturePath).Length > 1024 * 1024) throw new InvalidDataException("LEGACY_ARCHIVE_INVALID");
                byte[] manifestBytes = File.ReadAllBytes(capturePath);
                if (!HashEquals(manifestBytes, record.BackupManifestSha256)) throw new InvalidDataException("LEGACY_ARCHIVE_CHANGED");
                using var document = JsonDocument.Parse(manifestBytes);
                var manifest = document.RootElement;
                var fingerprint = manifest.GetProperty("fingerprint");
                if (!string.Equals(fingerprint.GetProperty("serial").GetString(), serial, StringComparison.OrdinalIgnoreCase)) continue;
                LegacyFirmwareMigration.ValidateFingerprint(fingerprint, serial);
                if (manifest.GetProperty("schema").GetInt32() != 1 || manifest.GetProperty("deviceId").GetString() != session.DeviceId ||
                    !HashEquals(JsonSerializer.SerializeToUtf8Bytes(fingerprint, Json), manifest.GetProperty("fingerprintSha256").GetString()))
                    throw new InvalidDataException("LEGACY_CAPTURE_INVALID");
                var reads = manifest.GetProperty("artifacts").EnumerateArray().ToArray();
                if (reads.Length != 2 || reads.Any(a => a.GetProperty("kind").GetString() != "original-read") ||
                    !reads.Select(a => a.GetProperty("path").GetString()).Order().SequenceEqual(new[] { "original-1.bin", "original-2.bin" }))
                    throw new InvalidDataException("LEGACY_CAPTURE_INVALID");
                var originals = reads.Select(a => ReadCheckedArtifact(Path.GetDirectoryName(capturePath)!, a, 262144)).ToArray();
                if (!originals[0].AsSpan().SequenceEqual(originals[1]) || !HashEquals(originals[0], record.OriginalSha256))
                    throw new InvalidDataException("LEGACY_ORIGINAL_PAIR_MISMATCH");
                LegacyFirmwareMigration.ValidateOriginal(originals[0], session.DeviceId!);
                if (match is not null) throw new InvalidDataException("LEGACY_ORIGINAL_NOT_UNIQUE");
                match = new(record, capturePath, manifestBytes, originals);
            }
            return match;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or
            ArgumentException or KeyNotFoundException or FormatException or UnauthorizedAccessException or OverflowException)
        {
            // An unusable archive cannot bind an original. Updates can use the verified running firmware.
            // Restore requires this lookup to succeed before the adapter can receive a write request.
            return null;
        }
    }
}
