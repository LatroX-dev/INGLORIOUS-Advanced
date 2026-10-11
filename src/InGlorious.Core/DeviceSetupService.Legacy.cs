using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace InGlorious.Core;

public sealed partial class DeviceSetupService
{
    private sealed record LegacyRecoveryRecord(int Schema, string DeviceId, string OriginalSha256,
        string BackupManifest, string BackupManifestSha256, ushort OriginalVendor, ushort OriginalProduct);

    private void PrepareLegacyMigration(SetupSession session, byte[] current)
    {
        try
        {
            LegacyFirmwareMigration.ValidateImage(current, session.DeviceId!);
            string serial = session.Selection.GetProperty("serial").GetString() ?? "";
            var matches = new List<(LegacyRecoveryRecord Record, byte[] Manifest, byte[][] Originals)>();
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
                if (fingerprint.GetProperty("serial").GetString() != serial) continue;
                LegacyFirmwareMigration.ValidateFingerprint(fingerprint, serial);
                if (manifest.GetProperty("schema").GetInt32() != 1 || manifest.GetProperty("deviceId").GetString() != session.DeviceId ||
                    !HashEquals(JsonSerializer.SerializeToUtf8Bytes(fingerprint, Json), manifest.GetProperty("fingerprintSha256").GetString()!))
                    throw new InvalidDataException("LEGACY_CAPTURE_INVALID");
                var reads = manifest.GetProperty("artifacts").EnumerateArray().ToArray();
                if (reads.Length != 2 || reads.Any(a => a.GetProperty("kind").GetString() != "original-read") ||
                    !reads.Select(a => a.GetProperty("path").GetString()).Order().SequenceEqual(new[] { "original-1.bin", "original-2.bin" }))
                    throw new InvalidDataException("LEGACY_CAPTURE_INVALID");
                var originals = reads.Select(a => ReadCheckedArtifact(Path.GetDirectoryName(capturePath)!, a, 262144)).ToArray();
                if (!originals[0].AsSpan().SequenceEqual(originals[1]) || !HashEquals(originals[0], record.OriginalSha256))
                    throw new InvalidDataException("LEGACY_ORIGINAL_PAIR_MISMATCH");
                LegacyFirmwareMigration.ValidateOriginal(originals[0], session.DeviceId!);
                matches.Add((record, manifestBytes, originals));
            }
            if (matches.Count != 1) throw new InvalidDataException("LEGACY_ORIGINAL_NOT_UNIQUE");
            var match = matches[0];
            string backupDirectory = Path.Combine(session.Directory, "original-recovery");
            System.IO.Directory.CreateDirectory(backupDirectory);
            using (var document = JsonDocument.Parse(match.Manifest))
            {
                int i = 0;
                foreach (var read in document.RootElement.GetProperty("artifacts").EnumerateArray())
                    WriteNew(ContainedFile(backupDirectory, read.GetProperty("path").GetString()!), match.Originals[i++]);
            }
            session.BackupPath = Path.Combine(backupDirectory, "capture.json");
            WriteNew(session.BackupPath, match.Manifest);
            session.OriginalSha256 = match.Record.OriginalSha256;
            session.OriginalVendor = match.Record.OriginalVendor; session.OriginalProduct = match.Record.OriginalProduct;
            session.Installation = new(1, Guid.NewGuid(), session.OriginalSha256);
            session.MigrateLegacy = true;
            session.InstalledFirmwareVersion = LegacyFirmwareMigration.Version;
            WriteNew(Path.Combine(session.Directory, "recovery.json"), JsonSerializer.SerializeToUtf8Bytes(new RecoveryRecord(1,
                session.DeviceId!, session.Installation.InstallationId, session.OriginalSha256,
                Path.GetRelativePath(session.Directory, session.BackupPath), Convert.ToHexString(SHA256.HashData(match.Manifest)),
                session.OriginalVendor, session.OriginalProduct), Json));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            throw Error("Für diese Installation wurde keine passende Original-Sicherung gefunden.");
        }
    }
}
