using System.Text.Json;
using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal static partial class SelfTests
{
    private static void InstalledUpdateChecks(string root, Model model)
    {
        string id = model.DeviceId;
        string directory = Path.Combine(root, id + "-installed-update");
        Directory.CreateDirectory(directory);
        int session = 0;
        string Next(string name) => Path.Combine(directory, $"{++session}-{name}");

        // The marker refers to a stock image that is unavailable on this computer.
        var identity = new InstallationIdentity((byte)model.BoardId, Guid.NewGuid(), Files.Hash("unavailable original"u8.ToArray()));
        byte[] application = ReadApprovedFirmware(id);
        byte[] previous = application;
        string? previousDirectory = Environment.GetEnvironmentVariable("INGLORIOUS_TEST_PREVIOUS_FIRMWARE_DIRECTORY");
        if (!string.IsNullOrEmpty(previousDirectory))
            previous = Files.ReadBounded(Path.Combine(previousDirectory, (id == "pro-iso" ? "pro" : id) + ".bin"), model.FlashBytes);
        byte[] current = InstallationIdentity.CreateImage(previous, model.FlashBytes, identity);
        for (int i = current.Length - InstallationIdentity.StorageBytes; i < current.Length; i++)
            current[i] = (byte)(i * 37 + model.BoardId);
        byte[] target = InstallationIdentity.CreateImage(application, model.FlashBytes, identity);
        current.AsSpan(^InstallationIdentity.StorageBytes).CopyTo(target.AsSpan(^InstallationIdentity.StorageBytes));
        string sourceVersion = KnownFirmwareImages.Validate(current, id, identity.BoardId);
        string targetVersion = KnownFirmwareImages.Validate(target, id, identity.BoardId);
        if (!string.IsNullOrEmpty(previousDirectory))
            Check(Version.Parse(sourceVersion) < Version.Parse(targetVersion), id + " supplied fixture exercises a real upgrade");

        var device = new FakeDevice(model) { Image = current.ToArray() };
        var fp = device.Fingerprint;
        var selection = new Selection(fp.Vid, fp.Pid, fp.Bus, fp.PortPath, fp.Serial, model.Transport);
        var engine = new AdapterEngine(() => new FakeBackend(device, selection));
        string BundleFor(byte[] bytes, string version)
        {
            string folder = Next("bundle");
            Directory.CreateDirectory(folder);
            var image = Files.New(folder, "target.bin", bytes, "test-image");
            var bundle = new Bundle(1, id, model.BoardId, model.Transport, Model.FlashBase, model.FlashBytes,
                model.SramBytes, new(image.Path, bytes.Length, image.Sha256), version);
            Files.New(folder, "bundle.json", Json.Bytes(bundle), "test-bundle");
            return Path.Combine(folder, "bundle.json");
        }
        string Capture(bool installed = true)
        {
            int reads = device.Reads, writes = device.Writes;
            var capture = engine.Run(new(1, "capture", Next("capture"), id, selection, CaptureInstalled: installed));
            Check(capture.Status == "ok" && device.Writes == writes && device.Reads == reads + 2, id + " captures the current device twice");
            if (installed)
                Check(capture.Artifacts.Where(a => a.Kind == "current-read").Select(a => a.Path)
                    .SequenceEqual(["current-1.bin", "current-2.bin"]), id + " identifies both current reads separately from a factory backup");
            return Path.Combine(capture.EvidenceDirectory, "capture.json");
        }

        var request = new AdapterRequest(1, "update", Next("update"), id, selection, Files.Hash(Json.Bytes(fp)),
            BundleFor(target, targetVersion), Capture(), true, identity.OriginalSha256, identity.InstallationId,
            Files.Hash(current), RestartAfterVerify: true, UseCurrentCapture: true);
        void Block(AdapterRequest test, string error)
        {
            int writes = device.Writes, restarts = device.Restarts;
            var result = engine.Run(test with { SessionDirectory = Next("rejected") });
            Check(result is { Status: "blocked", WriteAttempted: false, Verified: false, RestartAttempted: false } &&
                result.ErrorCode == error && device.Writes == writes && device.Restarts == restarts,
                $"{id} expected {error} without a write or restart, got {result.Status}/{result.ErrorCode}");
        }

        // Run the capture substitution and transfer fault matrix on both physical transports.
        bool exerciseFaults = id is "pro-iso" or "numpad";
        if (exerciseFaults)
        {
            Block(request with { ExpectedInstallationId = Guid.NewGuid() }, "INSTALLATION_IDENTITY_MISMATCH");
            Block(request with { ExpectedBackupSha256 = request.ExpectedCurrentSha256 }, "INSTALLATION_IDENTITY_MISMATCH");
            var captured = Json.Read<CaptureManifest>(request.BackupPath!);
            string AlteredManifest(CaptureManifest manifest)
            {
                string path = Path.Combine(Path.GetDirectoryName(request.BackupPath!)!, $"{++session}-capture.json");
                File.WriteAllBytes(path, Json.Bytes(manifest));
                return path;
            }
            var foreign = fp with { PortPath = "4.2", Serial = fp.Serial.Length == 0 ? "" : "FEDCBA987654" };
            Block(request with { BackupPath = AlteredManifest(captured with { Fingerprint = foreign, FingerprintSha256 = Files.Hash(Json.Bytes(foreign)) }) }, "CAPTURE_DEVICE_CHANGED");
            Block(request with { BackupPath = AlteredManifest(captured with { Artifacts = [captured.Artifacts[0], captured.Artifacts[0]] }) }, "BACKUP_MANIFEST_MISMATCH");
            Block(request with { BackupPath = AlteredManifest(captured with { DeviceId = id == "numpad" ? "gmmk2-65-iso" : "pro-ansi" }) }, "BACKUP_MANIFEST_MISMATCH");
            byte[] altered = target.ToArray();
            altered[^1] ^= 1;
            Block(request with { BundlePath = BundleFor(altered, targetVersion) }, "UPDATE_CONFIGURATION_CHANGED");
            device.Image[^1] ^= 1;
            Block(request, "CURRENT_CHANGED_SINCE_CAPTURE");
            device.Image = current.ToArray();
            device.ReadFault = true;
            Block(request, "FULL_READ_PAIR_MISMATCH");
            device.ReadFault = false;

            // A fresh, internally consistent pair still needs known code and a valid marker.
            device.Image[100] ^= 1;
            Block(request with { BackupPath = Capture(), ExpectedCurrentSha256 = Files.Hash(device.Image) }, "UNKNOWN_INSTALLED_FIRMWARE");
            device.Image = current.ToArray();
            device.Image[InstallationIdentity.Offset(model.FlashBytes)] ^= 1;
            Block(request with { BackupPath = Capture(), ExpectedCurrentSha256 = Files.Hash(device.Image) }, "DEVICE_RECOVERY_ID_MISSING");
            device.Image = current.ToArray();
        }

        int beforeReads = device.Reads;
        var updated = engine.Run(request);
        Check(updated is { Status: "verified", Verified: true, WriteAttempted: true, RestartAttempted: true, CanRestore: false } &&
            updated.InstallationId == identity.InstallationId && device.Reads == beforeReads + 4 && device.Writes == 1 && device.Restarts == 1 &&
            updated.RestartAcknowledged == (model.Transport == "wb32-dfu"),
            $"{id} writes and verifies using only the current pair, got {updated.Status}/{updated.ErrorCode}");
        Check(device.Image.AsSpan().SequenceEqual(target) && InstallationIdentity.Read(device.Image) == identity, id + " preserves the recovery identity and all settings");
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(updated.EvidenceDirectory, "installation.json")));
        Check(receipt.RootElement.GetProperty("originalCaptureManifest").ValueKind == JsonValueKind.Null &&
            receipt.RootElement.GetProperty("currentCaptureManifest").GetString() == request.BackupPath &&
            receipt.RootElement.GetProperty("originalSha256").GetString() == identity.OriginalSha256, id + " receipt preserves origin and cannot advertise stock recovery");
        if (exerciseFaults)
        {
            if (sourceVersion != targetVersion)
                Block(request with { BackupPath = Capture(), ExpectedCurrentSha256 = Files.Hash(target), BundlePath = BundleFor(current, sourceVersion) }, "FIRMWARE_DOWNGRADE_NOT_ALLOWED");
            device.Image = current.ToArray();
            device.AfterPairFault = true;
            var failed = engine.Run(request with { SessionDirectory = Next("failed-readback"), BackupPath = Capture(installed: false) });
            Check(failed is { Status: "failed", WriteAttempted: true, Verified: false, RestartAttempted: false, ErrorCode: "FULL_READ_PAIR_MISMATCH" } &&
                device.Writes == 2 && device.Restarts == 1, id + " interrupted verification cannot claim success or restart");
        }
    }
}
