using System.Buffers.Binary;
using System.Text.Json;
using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal static partial class SelfTests
{
    private static void LegacyChecks(string root)
    {
        var request = new AdapterRequest(1, "restore", root, "pro-iso", new(0x0483, 0xDF11, 1, "1", "0123456789AB", "stm32-dfuse"),
            new string('A', 64), BackupPath: "fixture", ConfirmWrite: true, ExpectedBackupSha256: new string('B', 64), ExpectedInstallationId: Guid.NewGuid(), MigrateLegacy: true);
        Reject(() => AdapterEngine.ValidateRequest(request), "legacy flag cannot authorize restore");
        Reject(() => AdapterEngine.ValidateRequest(request with { Operation = "install", BundlePath = "fixture" }), "legacy flag cannot authorize stock installation");
        Reject(() => AdapterEngine.ValidateRequest(request with { Operation = "update", DeviceId = "numpad", BundlePath = "fixture", ExpectedCurrentSha256 = new string('C', 64) }), "WB legacy has no serial proof");
        var fixturePath = Environment.GetEnvironmentVariable("INGLORIOUS_TEST_LEGACY_FIRMWARE");
        if (string.IsNullOrWhiteSpace(fixturePath))
        {
            Console.WriteLine("Legacy end-to-end fixture not supplied; only synthetic rejection checks ran.");
            return;
        }
        var code = Files.ReadBounded(fixturePath, LegacyFirmwareMigration.ApplicationBytes);
        var legacy = Enumerable.Repeat((byte)0xFF, 262144).ToArray(); code.CopyTo(legacy, 0);
        for (int i = legacy.Length - InstallationIdentity.StorageBytes; i < legacy.Length; i++) legacy[i] = (byte)(i * 17 + 123);
        LegacyFirmwareMigration.ValidateImage(legacy, "pro-iso"); checks++;
        foreach (string fault in new[] { "none", "not-opted-in", "unknown-code", "changed-gap", "changed-marker", "changed-config", "wrong-serial", "bad-stock", "missing-stock", "changed-pair", "stale-current", "bad-backup-hash", "read-fault" })
        {
            var model = Model.Get("pro-iso"); var device = new FakeDevice(model);
            byte[] descriptor = [18, 1, 0, 2, 0, 0, 0, 64, 0x0F, 0x32, 0x46, 0x50, 0, 1, 1, 2, 3, 1];
            if (fault == "bad-stock") descriptor[10] = 0x88;
            if (fault != "missing-stock") descriptor.CopyTo(device.Image, 128);
            var original = device.Image.ToArray();
            var selection = new Selection(0x0483, 0xDF11, 1, "6.7", device.Fingerprint.Serial, model.Transport);
            AdapterResponse Invoke(AdapterRequest value) => new AdapterEngine(() => new FakeBackend(device, selection)).Run(value);
            string Folder(string suffix) => Path.Combine(root, "legacy-" + fault + "-" + suffix + "-" + Guid.NewGuid().ToString("N"));
            var initial = new AdapterRequest(1, "capture", Folder("capture"), model.DeviceId, selection);
            var capture = Invoke(initial);
            Check(capture.Status == "ok", "legacy synthetic original captured");
            string capturePath = Path.Combine(capture.EvidenceDirectory, "capture.json");
            if (fault == "wrong-serial")
            {
                var manifest = Json.Read<CaptureManifest>(capturePath);
                var foreign = manifest.Fingerprint with { Serial = "FEDCBA987654" };
                File.WriteAllBytes(capturePath, Json.Bytes(manifest with { Fingerprint = foreign, FingerprintSha256 = Files.Hash(Json.Bytes(foreign)) }));
            }
            if (fault == "changed-pair") File.WriteAllBytes(Path.Combine(capture.EvidenceDirectory, "original-2.bin"), new byte[262144]);
            device.Image = legacy.ToArray();
            if (fault == "unknown-code") device.Image[100] ^= 1;
            if (fault == "changed-gap") device.Image[code.Length + 1] = 0;
            if (fault == "changed-marker") device.Image[InstallationIdentity.Offset(262144)] = 0;
            var identity = new InstallationIdentity(1, Guid.NewGuid(), Files.Hash(original));
            var image = InstallationIdentity.CreateImage(ReadApprovedFirmware("pro-iso"), 262144, identity);
            device.Image.AsSpan(262144 - InstallationIdentity.StorageBytes).CopyTo(image.AsSpan(262144 - InstallationIdentity.StorageBytes));
            if (fault == "changed-config") image[^1] ^= 1;
            string bundleFolder = Folder("bundle"); Directory.CreateDirectory(bundleFolder);
            var imageArtifact = Files.New(bundleFolder, "full.bin", image, "fixture");
            var bundle = new Bundle(1, "pro-iso", 1, model.Transport, Model.FlashBase, 262144, 40960,
                new("full.bin", image.Length, imageArtifact.Sha256), KnownFirmwareImages.Validate(image, "pro-iso", 1));
            var bundlePath = Path.Combine(bundleFolder, "bundle.json"); File.WriteAllBytes(bundlePath, Json.Bytes(bundle));
            device.ReadFault = fault == "read-fault";
            var update = new AdapterRequest(1, "update", Folder("update"), "pro-iso", selection, capture.FingerprintSha256,
                bundlePath, capturePath, true, fault == "bad-backup-hash" ? new string('0', 64) : identity.OriginalSha256,
                identity.InstallationId, fault == "stale-current" ? new string('0', 64) : Files.Hash(device.Image), fault != "not-opted-in");
            var result = Invoke(update);
            if (fault != "none")
            {
                Check(!result.WriteAttempted && !result.Verified && device.Writes == 0 && result.Status == "blocked", "legacy blocks " + fault);
                continue;
            }
            Check(result.Verified && device.Writes == 1 && InstallationIdentity.Read(device.Image) == identity, "legacy migrates only in verified full image");
            Check(device.Image.AsSpan(262144 - InstallationIdentity.StorageBytes).SequenceEqual(legacy.AsSpan(262144 - InstallationIdentity.StorageBytes)), "legacy preserves every settings byte");
            Check(KnownFirmwareImages.Validate(device.Image, "pro-iso", 1) == bundle.ReleaseId, "migrated code has normal known-image provenance");
            var restore = Invoke(update with { Operation = "restore", SessionDirectory = Folder("restore"), BundlePath = null, ExpectedCurrentSha256 = null, MigrateLegacy = false });
            Check(restore.Verified && device.Writes == 2 && device.Image.SequenceEqual(original), "normal restore works after legacy migration");
        }
    }
}
