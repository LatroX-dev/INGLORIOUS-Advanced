using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using InGlorious.Core;

internal static partial class Program
{
    private static async Task CheckLegacyMigration()
    {
        byte[] code = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        byte[] image = Enumerable.Repeat((byte)0xFF, 262144).ToArray(); code.CopyTo(image, 0);
        string hash = Convert.ToHexString(SHA256.HashData(code));
        LegacyFirmwareMigration.ValidateImageBytes(image, code.Length, hash); Check(true, "synthetic legacy layout accepted against its exact hash");
        foreach (int position in new[] { 1, 63, 64, InstallationIdentity.Offset(image.Length), image.Length - InstallationIdentity.StorageBytes - 1 })
        {
            var changed = image.ToArray(); changed[position] ^= 1;
            Reject(() => LegacyFirmwareMigration.ValidateImageBytes(changed, code.Length, hash), "any code, gap or identity byte change is rejected");
        }
        image[^1] = 23; LegacyFirmwareMigration.ValidateImageBytes(image, code.Length, hash); Check(true, "settings are outside immutable code approval");
        Reject(() => LegacyFirmwareMigration.ValidateImage(image, "pro-iso"), "synthetic bytes cannot satisfy production legacy approval");
        Reject(() => LegacyFirmwareMigration.ValidateImage(image, "pro-ansi"), "no unreviewed model borrows ISO approval");
        var original = new byte[262144]; UsbDescriptor(0x320F, 0x5046).CopyTo(original, 64);
        LegacyFirmwareMigration.ValidateOriginal(original, "pro-iso"); Check(true, "stock model classification remains mandatory");
        Reject(() => LegacyFirmwareMigration.ValidateOriginal(new byte[262144], "pro-iso"), "missing original stock descriptor rejected");
        UsbDescriptor(0x320F, 0x5088).CopyTo(original, 128);
        Reject(() => LegacyFirmwareMigration.ValidateOriginal(original, "pro-iso"), "conflicting stock model rejected");
        using var fixture = new SetupFixture("pro-iso");
        var install = await fixture.ReadyToCapture(); await fixture.Service.CaptureAsync(install);
        using var capture = JsonDocument.Parse(File.ReadAllBytes(install.BackupPath!));
        var fp = JsonNode.Parse(capture.RootElement.GetProperty("fingerprint").GetRawText())!;
        fp["serial"] = "0123456789AB";
        LegacyFirmwareMigration.ValidateFingerprint(JsonSerializer.SerializeToElement(fp), "0123456789AB"); Check(true, "synthetic complete STM serial evidence accepted");
        Reject(() => LegacyFirmwareMigration.ValidateFingerprint(JsonSerializer.SerializeToElement(fp), "FEDCBA987654"), "foreign serial rejected");
        foreach (var field in new[] { "transport", "serial", "identityStrength", "memoryMap" })
        {
            var changed = fp.DeepClone(); changed[field] = "invalid";
            Reject(() => LegacyFirmwareMigration.ValidateFingerprint(JsonSerializer.SerializeToElement(changed), "0123456789AB"), "invalid capture identity rejected: " + field);
        }
        foreach (var field in new[] { "vid", "pid", "configuration", "alternate", "interface", "flashBase", "flashBytes", "sramBytes" })
        {
            var changed = fp.DeepClone(); changed[field] = changed[field]!.GetValue<int>() + 1;
            Reject(() => LegacyFirmwareMigration.ValidateFingerprint(JsonSerializer.SerializeToElement(changed), "0123456789AB"), "invalid capture geometry rejected: " + field);
        }
        await CheckLegacyCoordinator();
    }

    private static async Task CheckLegacyCoordinator()
    {
        static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
        string? file = Environment.GetEnvironmentVariable("INGLORIOUS_TEST_LEGACY_FIRMWARE");
        if (string.IsNullOrWhiteSpace(file)) { Console.WriteLine("Legacy end-to-end fixture not supplied; synthetic checks only."); return; }
        byte[] application = File.ReadAllBytes(file);
        foreach (string fault in new[] { "none", "missing-original", "different-serial", "duplicate-original", "changed-original", "bad-stock", "unknown-code", "nonempty-marker", "changed-manifest" })
        {
            using var f = new SetupFixture("pro-iso");
            byte[] original = f.Environment.Current.ToArray();
            var first = await f.ReadyToCapture(); await f.Service.CaptureAsync(first);
            string archive = Path.GetDirectoryName(first.BackupPath!)!;
            var manifest = JsonNode.Parse(File.ReadAllText(first.BackupPath!))!;
            if (fault == "bad-stock")
            {
                original.AsSpan(113, 18).Clear();
                foreach (var artifact in manifest["artifacts"]!.AsArray())
                {
                    File.WriteAllBytes(Path.Combine(archive, artifact!["path"]!.GetValue<string>()), original);
                    artifact["sha256"] = Hash(original);
                }
                File.WriteAllBytes(first.BackupPath!, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (fault == "different-serial")
            {
                manifest["fingerprint"]!["serial"] = "FEDCBA987654";
                manifest["fingerprintSha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest["fingerprint"], new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllBytes(first.BackupPath!, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            var record = new { schema = 1, deviceId = "pro-iso", originalSha256 = Hash(original), backupManifest = Path.GetRelativePath(first.Directory, first.BackupPath!),
                backupManifestSha256 = Hash(File.ReadAllBytes(first.BackupPath!)), originalVendor = 0x320F, originalProduct = 0x5046 };
            if (fault != "missing-original") File.WriteAllBytes(Path.Combine(first.Directory, "legacy-recovery.json"), JsonSerializer.SerializeToUtf8Bytes(record));
            if (fault == "duplicate-original")
            {
                string duplicate = Path.Combine(f.Archive, "duplicate"); Directory.CreateDirectory(duplicate);
                string nested = Path.Combine(duplicate, Path.GetRelativePath(first.Directory, archive)); Directory.CreateDirectory(nested);
                foreach (string input in Directory.EnumerateFiles(archive)) File.Copy(input, Path.Combine(nested, Path.GetFileName(input)));
                File.WriteAllBytes(Path.Combine(duplicate, "legacy-recovery.json"), JsonSerializer.SerializeToUtf8Bytes(record));
            }
            if (fault == "changed-original") File.WriteAllBytes(Path.Combine(archive, "original-1.bin"), new byte[262144]);
            if (fault == "changed-manifest") File.AppendAllText(first.BackupPath!, " ");
            byte[] legacy = Enumerable.Repeat((byte)0xFF, 262144).ToArray(); application.CopyTo(legacy, 0);
            for (int i = 262144 - InstallationIdentity.StorageBytes; i < legacy.Length; i++) legacy[i] = (byte)(i * 13 + 17);
            if (fault == "unknown-code") legacy[100] ^= 1;
            if (fault == "nonempty-marker") legacy[InstallationIdentity.Offset(legacy.Length)] = 0;
            f.Environment.Current = legacy.ToArray();
            var update = await f.ReadyToCapture(SetupOperation.Update);
            if (fault != "none")
            {
                await RejectAsync(async () => await f.Service.CaptureAsync(update), "legacy coordinator blocks " + fault);
                Check(f.Environment.Writes == 0 && update.Stage == SetupStage.Blocked, "unsafe migration never writes");
                continue;
            }
            await f.Service.CaptureAsync(update);
            Check(update.MigrateLegacy && update.InstalledFirmwareVersion == "0.9.2" && f.Environment.Writes == 0, "legacy preparation is read-only");
            var target = File.ReadAllBytes(Path.Combine(update.Directory, "installation.bin"));
            Check(target.AsSpan(262144 - InstallationIdentity.StorageBytes).SequenceEqual(legacy.AsSpan(262144 - InstallationIdentity.StorageBytes)), "legacy preparation preserves complete configuration");
            Check(InstallationIdentity.Read(target)!.MatchesOriginal(original) && File.Exists(Path.Combine(update.Directory, "recovery.json")), "new identity binds archived manufacturer bytes before update");
            await f.Service.UpdateAsync(update, true);
            var restore = await f.ReadyToCapture(SetupOperation.Restore); await f.Service.CaptureAsync(restore); await f.Service.RestoreAsync(restore, true);
            Check(f.Environment.Current.SequenceEqual(original), "migrated coordinator restores exact original through normal path");
        }
    }
}
