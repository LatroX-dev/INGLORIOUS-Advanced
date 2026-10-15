using System.Buffers.Binary;
using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal static partial class SelfTests
{
    private static void RetainedLoaderChecks(string root)
    {
        var model = Model.Get(RetainedLoaderLayout.DeviceId);
        Check(model is { EngineeringOnly: false, BoardId: 16, BootVersion: 4, FlashBytes: 262144, SramBytes: 36864, ProtectedPrefixBytes: 0x7000 }, "normal retained-loader model has exact independent geometry");
        RetainedLoaderLayout.ValidateLayout(model.DeviceId, 16, 262144, 0x7000); checks++;
        foreach (int offset in new[] { -1, 0, 0x6000, 0x7100 })
            Reject(() => RetainedLoaderLayout.ValidateLayout(model.DeviceId, 16, 262144, offset), "normal model cannot change its retained application offset");
        Reject(() => RetainedLoaderLayout.ValidateLayout("gmmk3-100-iso", 16, 262144, 0x7000), "retained layout cannot alias a generic100ISO");
        Reject(() => RetainedLoaderLayout.ValidateLayout(model.DeviceId, 15, 262144, 0x7000), "retained layout requires board16");
        Reject(() => RetainedLoaderLayout.ValidateLayout("numpad", 2, 131072, 0x7000), "old board cannot borrow relocated layout");
        var fake = new FakeDevice(model);
        byte[] original = fake.Image.ToArray(), application = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(application, 0x20009000);
        BinaryPrimitives.WriteUInt32LittleEndian(application.AsSpan(4), 0x08007101);
        RetainedLoaderLayout.ValidateApplicationVectors(application); checks++;
        foreach (uint reset in new uint[] { 0x08000101, 0x08007100, 0x08007201, 0x1FFFE001 })
        {
            var wrong = application.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(wrong.AsSpan(4), reset);
            Reject(() => RetainedLoaderLayout.ValidateApplicationVectors(wrong), "retained reset must be Thumb and inside application bytes");
        }
        var identity = new InstallationIdentity(16, Guid.NewGuid(), Files.Hash(original));
        Reject(() => InstallationIdentity.CreateImage(application, model.FlashBytes, identity), "normal board16 requires its complete original");
        Reject(() => InstallationIdentity.CreateImage(application, model.FlashBytes, identity, original), "synthetic unapproved resident loader is rejected");
        Reject(() => RetainedLoaderLayout.ValidatePrefix(original), "unknown loader revision is rejected by immutable code digest");
        Reject(() => RetainedLoaderLayout.ValidatePrefix(new byte[0x7001]), "prefix digest does not accept arbitrary-length input");
        for (int offset = 0; offset < model.ProtectedPrefixBytes; offset += WbProtocol.Page)
        {
            uint address = Model.FlashBase + (uint)offset;
            Reject(() => WbProtocol.ErasePacket(address, model), "normal mode cannot erase any retained loader page");
            Reject(() => WbProtocol.WritePacket(address, new byte[256], model), "normal mode cannot program any retained loader page");
            Reject(() => WbProtocol.ValidatePacket(WbProtocol.ErasePacket(address, Model.Get("numpad")), true, model), "normal mode rejects forged raw prefix erase");
            Reject(() => WbProtocol.ValidatePacket(WbProtocol.WritePacket(address, new byte[256], Model.Get("numpad")), true, model), "normal mode rejects forged raw prefix program");
        }
        var selection = new Selection(0x342D, 0xDFA0, 1, "6.7", "", model.Transport);
        var engine = new AdapterEngine(() => new FakeBackend(fake, selection));
        var rejected = engine.Run(new(1, "capture", Path.Combine(root, "unknown-retained-loader"), model.DeviceId, selection));
        Check(rejected is { Status: "blocked", ErrorCode: "UNKNOWN_RESIDENT_LOADER", WriteAttempted: false } && fake.Reads == 2 && fake.Writes == 0,
            "unknown loader capture saves reads but cannot advance to installation");
        RetainedLoaderWriteGrant.ValidateBackupDevice(fake.Fingerprint, fake.Fingerprint with { Bus = 2, PortPath = "3.4" }); checks++;
        Reject(() => RetainedLoaderWriteGrant.ValidateBackupDevice(fake.Fingerprint, fake.Fingerprint with { ChipId = 0x3A512980 }), "another hardware fingerprint cannot adopt this backup");
        Reject(() => RetainedLoaderWriteGrant.ValidateBackupDevice(fake.Fingerprint, fake.Fingerprint with { FlashBytes = 131072 }), "backup geometry cannot change");
    }

    internal static int RunRetainedLoader(string candidatePath, string originalPath, string repeatPath)
    {
        string root = CheckDirectory(); Directory.CreateDirectory(root);
        try
        {
            RetainedLoaderChecks(root);
            RetainedLoaderFixtureChecks(root, candidatePath, originalPath, repeatPath);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(Json.Bytes(new { passed = checks, retainedLoaderEndToEnd = true,
                hardwareAccess = false, nativeLibraryLoaded = false, toolProcessesStarted = false })));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { CleanCheckDirectory(root); }
    }

    private static void RetainedLoaderFixtureChecks(string root, string candidatePath, string originalPath, string repeatPath)
    {
        var model = Model.Get(RetainedLoaderLayout.DeviceId);
        Check(!string.Equals(Path.GetFullPath(originalPath), Path.GetFullPath(repeatPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "original pair paths are distinct");
        byte[] original = Files.ReadBounded(originalPath, model.FlashBytes), repeat = Files.ReadBounded(repeatPath, model.FlashBytes);
        Check(original.Length == model.FlashBytes && original.AsSpan().SequenceEqual(repeat), "complete explicit original fixture pair matches");
        using var firstLock = Files.Lock(originalPath, model.FlashBytes, Files.Hash(original)).Stream;
        using var secondLock = Files.Lock(repeatPath, model.FlashBytes, Files.Hash(original)).Stream;
        byte[] application = Files.ReadBounded(candidatePath, model.FlashBytes);
        using var candidateLock = Files.Lock(candidatePath, application.Length, Files.Hash(application)).Stream;
        RetainedLoaderLayout.ValidatePrefix(original); checks++;
        // Exercise another valid original digest, proving no specimen/full-image allowlist.
        original = original.ToArray(); original[^1] ^= 0x5A;
        var identity = new InstallationIdentity(16, Guid.NewGuid(), Files.Hash(original));
        byte[] image = InstallationIdentity.CreateImage(application, model.FlashBytes, identity, original);
        string version = KnownFirmwareImages.Validate(image, model.DeviceId, 16);
        Check(image.AsSpan(0, 0x7000).SequenceEqual(original.AsSpan(0, 0x7000)) && image.AsSpan(0x7000, application.Length).SequenceEqual(application), "prepared image combines own loader and offset-linked reviewed code");
        var device = new FakeDevice(model) { Image = original.ToArray() };
        var selection = new Selection(0x342D, 0xDFA0, 1, "6.7", "", model.Transport);
        var engine = new AdapterEngine(() => new FakeBackend(device, selection));
        string Folder(string name) => Path.Combine(root, "normal-retained-" + name + "-" + Guid.NewGuid().ToString("N"));
        string BundleFor(byte[] bytes)
        {
            string directory = Folder("bundle"); Directory.CreateDirectory(directory);
            Files.New(directory, "image.bin", bytes, "offline-image");
            Files.New(directory, "bundle.json", Json.Bytes(new Bundle(1, model.DeviceId, 16, model.Transport, Model.FlashBase,
                model.FlashBytes, model.SramBytes, new("image.bin", bytes.Length, Files.Hash(bytes)), version)), "offline-bundle");
            return Path.Combine(directory, "bundle.json");
        }
        var capture = engine.Run(new(1, "capture", Folder("original-capture"), model.DeviceId, selection));
        Check(capture.Status == "ok" && capture.CanInstall && device.Writes == 0, "normal capture accepts approved loader and no engineering authorization");
        var install = new AdapterRequest(1, "install", Folder("install"), model.DeviceId, selection, capture.FingerprintSha256,
            BundleFor(image), Path.Combine(capture.EvidenceDirectory, "capture.json"), true, Files.Hash(original), identity.InstallationId);
        void Block(AdapterRequest request, string name)
        {
            int writes = device.Writes;
            var response = engine.Run(request with { SessionDirectory = Folder(name) });
            Check(response is { Status: "blocked", WriteAttempted: false, Verified: false } && device.Writes == writes, name + " blocks before writes");
        }
        var wrongPrefix = image.ToArray(); wrongPrefix[100] ^= 1;
        Block(install with { BundlePath = BundleFor(wrongPrefix) }, "changed prepared loader");
        var unknownCode = image.ToArray(); unknownCode[0x7100] ^= 1;
        Block(install with { BundlePath = BundleFor(unknownCode) }, "unknown application code");
        Block(install with { ExpectedInstallationId = Guid.NewGuid() }, "unrelated installation identity");
        var grant = RetainedLoaderWriteGrant.Create(install, model, device.Fingerprint, original, original, image);
        Reject(() => grant.ValidateTransport(model, device.Fingerprint with { PortPath = "6.8" }, image), "normal grant binds current physical transport");
        Reject(() => grant.Consume(model, device.Fingerprint, image, wrongPrefix[..0x7000]), "normal grant rejects changed live loader");
        grant.Consume(model, device.Fingerprint, image, original[..0x7000]); checks++;
        Reject(() => grant.Consume(model, device.Fingerprint, image, original[..0x7000]), "normal grant cannot be reused");
        var installed = engine.Run(install);
        Check(installed is { Status: "verified", WriteAttempted: true } && device.Image.AsSpan().SequenceEqual(image), "normal install verifies complete image with protected loader");
        // Arbitrary persisted configuration must survive every update byte for byte.
        for (int i = model.FlashBytes - InstallationIdentity.StorageBytes; i < model.FlashBytes; i++) device.Image[i] = (byte)(i * 37 + 11);
        byte[] current = device.Image.ToArray();
        var currentCapture = engine.Run(new(1, "capture", Folder("current-capture"), model.DeviceId, selection));
        Check(currentCapture.Status == "ok", "normal installed capture retains full configuration");
        byte[] updateImage = InstallationIdentity.CreateImage(application, model.FlashBytes, identity, original);
        current.AsSpan(model.FlashBytes - InstallationIdentity.StorageBytes).CopyTo(updateImage.AsSpan(model.FlashBytes - InstallationIdentity.StorageBytes));
        var update = install with { Operation = "update", SessionDirectory = Folder("update"), BundlePath = BundleFor(updateImage), ExpectedCurrentSha256 = Files.Hash(current) };
        Block(update with { ExpectedCurrentSha256 = new string('0', 64) }, "stale current image");
        byte[] changedConfig = updateImage.ToArray(); changedConfig[^1] ^= 1;
        Block(update with { BundlePath = BundleFor(changedConfig) }, "configuration byte changed");
        byte[] changedIdentity = updateImage.ToArray(); new InstallationIdentity(16, Guid.NewGuid(), Files.Hash(original)).Encode().CopyTo(changedIdentity, InstallationIdentity.Offset(model.FlashBytes));
        Block(update with { BundlePath = BundleFor(changedIdentity) }, "recovery marker replaced");
        device.Image[0x7100] ^= 1;
        Block(update with { ExpectedCurrentSha256 = Files.Hash(device.Image) }, "unknown installed code");
        device.Image = current.ToArray();
        var updated = engine.Run(update);
        Check(updated is { Status: "verified", WriteAttempted: true } && device.Image.AsSpan().SequenceEqual(updateImage) && InstallationIdentity.Read(device.Image) == identity,
            "normal update preserves all configuration, loader and installation identity");
        var restore = install with { Operation = "restore", SessionDirectory = Folder("restore"), BundlePath = null };
        device.Image[InstallationIdentity.Offset(model.FlashBytes)] ^= 1;
        Block(restore, "missing marker recovery stays blocked");
        device.Image = updateImage.ToArray();
        // Restore remains possible if the application vector is damaged, because
        // original pairing and retained loader, rather than current app validity, authorize it.
        device.Image.AsSpan(0x7000, 8).Clear();
        var restored = engine.Run(restore);
        Check(restored is { Status: "verified", WriteAttempted: true } && device.Image.AsSpan().SequenceEqual(original), "normal restore returns exact own original despite damaged application vectors");
        Check(device.Writes == 3, "only install update and restore wrote through bounded fake packets");
    }
}
