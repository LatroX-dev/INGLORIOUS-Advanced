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
        LegacyInstalledUpdateChecks(root, legacy);
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

    private static void LegacyInstalledUpdateChecks(string root, byte[] legacy)
    {
        var model = Model.Get("pro-iso");
        var installed = new FakeDevice(model) { Image = legacy.ToArray() };
        int session = 0;
        string Folder(string name) => Path.Combine(root, $"legacy-no-archive-{++session}-{name}");
        Selection Select(FakeDevice device) => new(0x0483, 0xDF11, 1, "6.7", device.Fingerprint.Serial, model.Transport);
        AdapterResponse Invoke(FakeDevice device, AdapterRequest request) =>
            new AdapterEngine(() => new FakeBackend(device, Select(device))).Run(request with { SessionDirectory = Folder(request.Operation) });
        string Capture(FakeDevice device, bool installedFirmware = true)
        {
            var capture = Invoke(device, new(1, "capture", "", model.DeviceId, Select(device), CaptureInstalled: installedFirmware));
            Check(capture.Status == "ok", "legacy migration fixture gets its own double read");
            return Path.Combine(capture.EvidenceDirectory, "capture.json");
        }
        void Block(FakeDevice device, AdapterRequest request, string fault)
        {
            byte[] before = device.Image.ToArray();
            int writes = device.Writes, restarts = device.Restarts;
            try
            {
                var result = Invoke(device, request);
                Check(result is { Status: "blocked", WriteAttempted: false, Verified: false, RestartAttempted: false }, "legacy rejects " + fault);
            }
            catch (InvalidDataException) { /* Invalid flag combinations are refused before opening the device. */ }
            Check(device.Writes == writes && device.Restarts == restarts && device.Image.AsSpan().SequenceEqual(before), "legacy refuses " + fault + " before a write");
        }

        byte[] application = ReadApprovedFirmware(model.DeviceId);
        var identity = InstallationIdentity.CreateLegacyWithoutOriginal(Guid.NewGuid(), installed.Fingerprint.Serial);
        byte[] TargetFor(InstallationIdentity marker)
        {
            byte[] image = InstallationIdentity.CreateImage(application, model.FlashBytes, marker);
            legacy.AsSpan(^InstallationIdentity.StorageBytes).CopyTo(image.AsSpan(^InstallationIdentity.StorageBytes));
            return image;
        }
        string BundleFor(byte[] bytes)
        {
            string folder = Folder("bundle"); Directory.CreateDirectory(folder);
            var file = Files.New(folder, "image.bin", bytes, "fixture");
            var bundle = new Bundle(1, model.DeviceId, model.BoardId, model.Transport, Model.FlashBase, model.FlashBytes, model.SramBytes,
                new(file.Path, bytes.Length, file.Sha256), KnownFirmwareImages.Validate(bytes, model.DeviceId, 1));
            Files.New(folder, "bundle.json", Json.Bytes(bundle), "fixture");
            return Path.Combine(folder, "bundle.json");
        }
        byte[] target = TargetFor(identity);
        var request = new AdapterRequest(1, "update", "", model.DeviceId, Select(installed), Files.Hash(Json.Bytes(installed.Fingerprint)),
            BundleFor(target), Capture(installed), true, null, identity.InstallationId, Files.Hash(legacy), MigrateLegacy: true, UseCurrentCapture: true);

        Block(installed, request with { MigrateLegacy = false }, "markerless source without migration");
        const string foreignSerial = "FEDCBA987654";
        var wrongIdentity = InstallationIdentity.CreateLegacyWithoutOriginal(identity.InstallationId, foreignSerial);
        Block(installed, request with { BundlePath = BundleFor(TargetFor(wrongIdentity)) }, "replacement STM ROM serial in target marker");
        var forged = new InstallationIdentity(1, identity.InstallationId, Files.Hash(legacy));
        Block(installed, request with { BundlePath = BundleFor(TargetFor(forged)), ExpectedBackupSha256 = forged.OriginalSha256 }, "invented stock hash from the installed firmware");
        var changedConfig = target.ToArray(); changedConfig[^1] ^= 1;
        Block(installed, request with { BundlePath = BundleFor(changedConfig) }, "replacement settings");
        foreach (int offset in new[] { 100, LegacyFirmwareMigration.ApplicationBytes, InstallationIdentity.Offset(model.FlashBytes) })
        {
            installed.Image[offset] ^= 1;
            Block(installed, request with { BackupPath = Capture(installed), ExpectedCurrentSha256 = Files.Hash(installed.Image) }, "changed legacy bytes at " + offset);
            installed.Image = legacy.ToArray();
        }
        int reads = installed.Reads;
        var migrated = Invoke(installed, request);
        Check(migrated is { Verified: true, WriteAttempted: true, CanRestore: false } && migrated.InstallationId == identity.InstallationId &&
            installed.Writes == 1 && installed.Reads == reads + 4 && installed.Image.AsSpan().SequenceEqual(target) && InstallationIdentity.Read(installed.Image) == identity,
            $"known 0.9.2 migrates without a stock file and preserves settings and ROM serial, got {migrated.Status}/{migrated.ErrorCode}");
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(migrated.EvidenceDirectory, "installation.json")));
        Check(receipt.RootElement.GetProperty("originalSha256").ValueKind == JsonValueKind.Null &&
            receipt.RootElement.GetProperty("originalCaptureManifest").ValueKind == JsonValueKind.Null,
            "archive-free migration records no stock hash or stock manifest");

        var later = request with { BackupPath = Capture(installed), ExpectedCurrentSha256 = Files.Hash(target), MigrateLegacy = false, RestartAfterVerify = true };
        var other = new FakeDevice(model, foreignSerial) { Image = target.ToArray() };
        Block(other, later with { BackupPath = Capture(other), Selection = Select(other), ExpectedFingerprintSha256 = Files.Hash(Json.Bytes(other.Fingerprint)) },
            "same installed bytes copied onto another STM controller");
        var updated = Invoke(installed, later);
        Check(updated is { Verified: true, WriteAttempted: true, RestartAttempted: true, CanRestore: false } && installed.Writes == 2 &&
            installed.Restarts == 1 && installed.Image.AsSpan().SequenceEqual(target), $"later update retains the serial-bound marker, got {updated.Status}/{updated.ErrorCode}");

        // Only now does an independently captured stock archive become available.
        var stock = new FakeDevice(model);
        byte[] descriptor = [18, 1, 0, 2, 0, 0, 0, 64, 0x0F, 0x32, 0x46, 0x50, 0, 1, 1, 2, 3, 1];
        descriptor.CopyTo(stock.Image, 128);
        var restore = new AdapterRequest(1, "restore", "", model.DeviceId, Select(installed), request.ExpectedFingerprintSha256,
            BackupPath: Capture(stock, false), ConfirmWrite: true, ExpectedBackupSha256: Files.Hash(stock.Image), ExpectedInstallationId: identity.InstallationId);
        var foreignStock = new FakeDevice(model, foreignSerial) { Image = stock.Image.ToArray() };
        Block(installed, restore with { BackupPath = Capture(foreignStock, false) }, "stock from a different STM controller");
        Block(installed, restore with { BackupPath = later.BackupPath, ExpectedBackupSha256 = Files.Hash(target) }, "a current capture presented as a stock archive");
        var wrongModelStock = new FakeDevice(model) { Image = stock.Image.ToArray() };
        wrongModelStock.Image[138] ^= 1;
        Block(installed, restore with { BackupPath = Capture(wrongModelStock, false), ExpectedBackupSha256 = Files.Hash(wrongModelStock.Image) },
            "matching STM serial but incorrect model descriptor");
        var restored = Invoke(installed, restore);
        Check(restored is { Verified: true, WriteAttempted: true } && installed.Writes == 3 && installed.Image.AsSpan().SequenceEqual(stock.Image),
            $"later restore requires the matching independent stock archive, got {restored.Status}/{restored.ErrorCode}");
    }
}
