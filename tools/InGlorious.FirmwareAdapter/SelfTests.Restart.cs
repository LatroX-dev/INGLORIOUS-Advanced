using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal static partial class SelfTests
{
    internal static int RunRestart(string approvedFirmwareDirectory)
    {
        firmwareDirectory = approvedFirmwareDirectory;
        string root = CheckDirectory(); Directory.CreateDirectory(root);
        try
        {
            RestartChecks(root);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(Json.Bytes(new
                { passed = checks, hardwareAccess = false, nativeLibraryLoaded = false, toolProcessesStarted = false })));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { CleanCheckDirectory(root); }
    }

    private static void RestartChecks(string root)
    {
        string[] ids = ["pro-iso", "numpad", "pro-ansi", "pro-rev2-ansi", "pro-rev2-iso", "gmmk2-65-ansi", "gmmk2-65-iso",
            "gmmk2-96-ansi", "gmmk2-96-iso", "gmmk3-65-ansi", "gmmk3-65-iso", "gmmk3-75-ansi", "gmmk3-75-iso", "gmmk3-100-ansi", "gmmk3-100-iso", "gmmk3-pro-100-iso"];
        byte[] reset = WbProtocol.VerifiedRestartPacket(Model.Get("numpad"));
        Check(reset.AsSpan().SequenceEqual(new byte[] { 0x83, 0, 0, 0, 0xE8, 0x03, 0, 0 }), "fixed upstream WB reset encoding and delay");
        WbProtocol.CheckRestart([0x83, 0]); checks++;
        foreach (byte[] bad in new byte[][] { [], [0x83], [0x83, 0, 0], [0x9B, 0], [0x83, 0x1A], [0x83, 1] })
            Reject(() => WbProtocol.CheckRestart(bad), "restart missing, malformed or failed ACK is not success");
        foreach (string id in ids)
        {
            var model = Model.Get(id);
            VerifiedRestartGrant.ValidateModel(model); checks++;
            Reject(() => VerifiedRestartGrant.ValidateModel(model with { DeviceId = "unknown" }), "unknown model rejected " + id);
            Reject(() => VerifiedRestartGrant.ValidateModel(model with { BoardId = 255 }), "forged board rejected " + id);
            Reject(() => VerifiedRestartGrant.ValidateModel(model with { FlashBytes = model.FlashBytes * 2 }), "forged geometry rejected " + id);
            var selection = new FakeDevice(model).Fingerprint;
            var selected = new Selection(selection.Vid, selection.Pid, selection.Bus, selection.PortPath, selection.Serial, model.Transport);
            foreach (string operation in new[] { "restart", "reset", "leave", "verified-restart" })
                Reject(() => Stm32Device.Arguments(selected, operation), "ordinary STM command builder rejects restart " + operation);
            if (model.Transport == "wb32-dfu")
            {
                Check(WbProtocol.VerifiedRestartPacket(model).AsSpan().SequenceEqual(reset), "fixed WB reset for " + id);
                foreach (bool writes in new[] { false, true })
                    Reject(() => WbProtocol.ValidatePacket(reset, writes, model), "ordinary WB exchange never accepts reset " + id);
                Reject(() => Stm32Device.VerifiedRestartArguments(selected, model), "WB cannot take STM leave path");
            }
            else
            {
                Reject(() => WbProtocol.VerifiedRestartPacket(model), "STM cannot take WB reset path");
                Check(Stm32Device.VerifiedRestartArguments(selected, model).SequenceEqual(new[] {
                    "-d", "0483:df11", "-S", selected.Serial!, "-p", "1-6.7", "-c", "1", "-i", "0", "-a", "0", "-s", "0x08000000:leave" }),
                    "STM leave fixed address and exact selector, without image, erase, reset or force");
            }
            // Retained-loader complete image tests use the explicitly supplied original pair.
            if (model.ProtectedPrefixBytes == 0) RestartModelChecks(root, model, faultMatrix: id is "numpad" or "pro-iso");
        }
        Reject(() => VerifiedRestartGrant.ValidateModel(Model.Get(Model.EngineeringDeviceId, true)), "engineering restart remains unavailable");
    }

    private static void RestartModelChecks(string root, Model model, byte[]? originalFixture = null, byte[]? applicationFixture = null, bool faultMatrix = true)
    {
        string[] scenarios = faultMatrix ? ["verified", "disabled", "stale", "pair", "partial-write", "mismatch", "after-pair", "identity", "disconnect"] : ["verified"];
        foreach (string scenario in scenarios)
        {
            string sessionRoot = Path.Combine(root, "restart-" + model.DeviceId + "-" + scenario); Directory.CreateDirectory(sessionRoot);
            var device = new FakeDevice(model);
            if (originalFixture is not null) device.Image = originalFixture.ToArray();
            var selection = new Selection(device.Fingerprint.Vid, device.Fingerprint.Pid, 1, "6.7", device.Fingerprint.Serial, model.Transport);
            AdapterResponse Invoke(AdapterRequest request) => new AdapterEngine(() => new FakeBackend(device, selection)).Run(request);
            var captureRequest = new AdapterRequest(1, "capture", Path.Combine(sessionRoot, "capture"), model.DeviceId, selection);
            var capture = Invoke(captureRequest);
            Check(capture.Status == "ok", "restart fixture original capture");
            byte[] original = device.Image.ToArray();
            var identity = new InstallationIdentity((byte)model.BoardId, Guid.NewGuid(), Files.Hash(original));
            byte[] application = applicationFixture ?? ReadApprovedFirmware(model.DeviceId);
            byte[] image = model.ProtectedPrefixBytes == 0 ? InstallationIdentity.CreateImage(application, model.FlashBytes, identity)
                : InstallationIdentity.CreateImage(application, model.FlashBytes, identity, original);
            for (int i = image.Length - InstallationIdentity.StorageBytes; i < image.Length; i++) image[i] = (byte)(i * 7 + 31);
            device.Image = image.ToArray();
            string bundleRoot = Path.Combine(sessionRoot, "bundle"); Directory.CreateDirectory(bundleRoot);
            var artifact = Files.New(bundleRoot, "full.bin", image, "fixture");
            var bundle = new Bundle(1, model.DeviceId, model.BoardId, model.Transport, Model.FlashBase, model.FlashBytes, model.SramBytes,
                new("full.bin", image.Length, artifact.Sha256), KnownFirmwareImages.Validate(image, model.DeviceId, (byte)model.BoardId));
            Files.New(bundleRoot, "bundle.json", Json.Bytes(bundle), "fixture");
            var request = captureRequest with
            {
                Operation = "update", SessionDirectory = Path.Combine(sessionRoot, "update"), ConfirmWrite = true,
                ExpectedFingerprintSha256 = capture.FingerprintSha256, ExpectedBackupSha256 = identity.OriginalSha256,
                ExpectedInstallationId = identity.InstallationId, ExpectedCurrentSha256 = Files.Hash(image),
                BackupPath = Path.Combine(capture.EvidenceDirectory, "capture.json"), BundlePath = Path.Combine(bundleRoot, "bundle.json"),
                RestartAfterVerify = scenario != "disabled"
            };
            if (scenario == "stale") request = request with { ExpectedCurrentSha256 = new string('0', 64) };
            device.ReadFault = scenario == "pair";
            device.WriteFault = scenario == "partial-write";
            device.AfterFault = scenario == "mismatch";
            device.AfterPairFault = scenario == "after-pair";
            device.AfterIdentityFault = scenario == "identity";
            device.RestartFault = scenario == "disconnect";
            var result = Invoke(request);
            if (scenario is "verified" or "disabled" or "disconnect")
            {
                Check(result is { Status: "verified", WriteAttempted: true, Verified: true } && device.Writes == 1, scenario + " firmware result stays verified");
                Check(device.Image.AsSpan().SequenceEqual(image), scenario + " restart does not change saved configuration");
                if (scenario == "disabled")
                    Check(!result.RestartAttempted && !result.RestartAcknowledged && result.RestartErrorCode == null && device.Restarts == 0,
                        "ordinary updates retain manual reconnect");
                else
                {
                    Check(result.RestartAttempted && device.Restarts == 1, scenario + " exactly one reset attempt");
                    Check(result.RestartAcknowledged == (scenario == "verified" && model.Transport == "wb32-dfu") &&
                        result.RestartErrorCode == (scenario == "disconnect" ? "FAKE_RESTART_DISCONNECTED" : null), scenario + " ACK is distinct from firmware verification");
                    Check(File.Exists(Path.Combine(result.EvidenceDirectory, "firmware-verified.json")) &&
                        File.Exists(Path.Combine(result.EvidenceDirectory, "restart-attempt.json")) &&
                        File.Exists(Path.Combine(result.EvidenceDirectory, "restart-result.json")), scenario + " durable verification and restart evidence");
                }
            }
            else
            {
                Check(!result.Verified && !result.RestartAttempted && !result.RestartAcknowledged && device.Restarts == 0,
                    scenario + " failure cannot reset or report restart");
                Check(result.WriteAttempted == (scenario is "partial-write" or "mismatch" or "after-pair" or "identity"), scenario + " write state retained");
                string expectedError = scenario switch
                {
                    "stale" => "CURRENT_CHANGED_SINCE_CAPTURE", "pair" or "after-pair" => "FULL_READ_PAIR_MISMATCH",
                    "partial-write" => "FAKE_PARTIAL_WRITE", "mismatch" => "FULL_READBACK_MISMATCH",
                    "identity" => "CAPTURE_IDENTITY_CHANGED", _ => throw new InvalidOperationException()
                };
                Check(result.ErrorCode == expectedError, scenario + " fails at the intended safety check");
            }

            if (scenario == "verified")
            {
                foreach (string operation in new[] { "probe", "capture", "install", "restore", "list", "reset", "go" })
                    Reject(() => AdapterEngine.ValidateRequest(request with { Operation = operation }), "restart rejects operation " + operation);
                foreach (string id in new[] { "unknown", "gmmk3-pro-75-iso", "generic-wb32" })
                    Reject(() => AdapterEngine.ValidateRequest(request with { DeviceId = id }), "restart rejects unknown model " + id);
                Reject(() => AdapterEngine.ValidateRequest(request with { MigrateLegacy = true }), "restart cannot enable legacy migration");
                GrantChecks(request, model, device, image);
            }
        }
    }

    private static void GrantChecks(AdapterRequest request, Model model, FakeDevice device, byte[] image)
    {
        VerifiedRestartGrant Grant() => VerifiedRestartGrant.Create(request, model, device, device.Fingerprint, true, image, image.ToArray(), image.ToArray());
        Reject(() => VerifiedRestartGrant.Create(request, model, device, device.Fingerprint, false, image, image, image), "no restart grant without a completed write attempt");
        Reject(() => VerifiedRestartGrant.Create(request with { RestartAfterVerify = false }, model, device, device.Fingerprint, true, image, image, image), "no implicit restart grant");
        byte[] different = image.ToArray(); different[100] ^= 1;
        Reject(() => VerifiedRestartGrant.Create(request, model, device, device.Fingerprint, true, image, image, different), "both full readbacks must match exact written image");
        Reject(() => VerifiedRestartGrant.Create(request, model, device, device.Fingerprint, true, image, different, image), "first readback must also match");
        Reject(() => VerifiedRestartGrant.Create(request, model, device, device.Fingerprint, true, image, image[..^1], image), "short full readback rejected");
        Reject(() => VerifiedRestartGrant.Create(request, model, device, device.Fingerprint with { Serial = "changed" }, true, image, image, image), "grant cannot change session fingerprint");
        var grant = Grant();
        grant.Consume(device, model, device.Fingerprint, Files.Hash(image)); checks++;
        Reject(() => grant.Consume(device, model, device.Fingerprint, Files.Hash(image)), "restart grant is single use");
        grant = Grant();
        Reject(() => grant.Consume(new FakeDevice(model), model, device.Fingerprint, Files.Hash(image)), "same descriptors on another handle cannot consume grant");
        Reject(() => grant.Consume(device, model, device.Fingerprint, Files.Hash(image)), "refused restart grant cannot be retried");
        grant = Grant();
        Reject(() => grant.Consume(device, model, device.Fingerprint with { PortPath = "8.8" }, Files.Hash(image)), "identity must remain equal at restart");
        grant = Grant();
        Reject(() => grant.Consume(device, Model.Get(model.DeviceId == "numpad" ? "pro-iso" : "numpad"), device.Fingerprint, Files.Hash(image)), "another model cannot consume restart grant");
        grant = Grant();
        Reject(() => grant.Consume(device, model, device.Fingerprint, Files.Hash(different)), "restart bound to completed transport write hash");
    }
}
