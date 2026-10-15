using System.Buffers.Binary;
using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal static partial class SelfTests
{
    private static int checks;
    private static string? firmwareDirectory;
    internal static int Run(string? approvedFirmwareDirectory = null)
    {
        firmwareDirectory = approvedFirmwareDirectory;
        string root = CheckDirectory();
        Directory.CreateDirectory(root);
        try
        {
            NativeUsb.ValidateNativeLayouts(); checks++;
            var native = NativeUsb.VerifyBundledLibrary();
            Check(native.Bytes > 0 && native.Sha256.Length == 64, "native dependency pin without loading");
            var dfu = NativeToolCatalog.Verify(NativeToolCatalog.DfuName);
            using (Files.Lock(dfu.Path, dfu.Bytes, dfu.Sha256).Stream) checks++;
            NativeCatalogChecks();
            PacketChecks(); PageWriteChecks(); FileChecks(root); RequestChecks(root); EngineeringChecks(root); RetainedLoaderChecks(root); OperationLockChecks(root); StmChecks();
            string[] ids = ["pro-iso", "numpad", "pro-ansi", "pro-rev2-ansi", "pro-rev2-iso", "gmmk2-65-ansi", "gmmk2-65-iso", "gmmk2-96-ansi", "gmmk2-96-iso", "gmmk3-65-ansi", "gmmk3-65-iso", "gmmk3-75-ansi", "gmmk3-75-iso", "gmmk3-100-ansi", "gmmk3-100-iso"];
            foreach (string id in ids) EngineChecks(root, id);
            LegacyChecks(root);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(Json.Bytes(new { passed = checks, hardwareAccess = false, nativeLibraryLoaded = false, toolProcessesStarted = false })));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            CleanCheckDirectory(root);
        }
    }
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception("Check failed: " + label); }
    private static void Reject(Action action, string label) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, label); }
    private static void NativeCatalogChecks()
    {
        foreach (string rid in new[] { "win-x64", "linux-x64", "osx-x64", "osx-arm64" })
        {
            var names = rid == "win-x64" ? new[] { "libusb-1.0.dll", "dfu-util-static.exe" } : new[] { rid.StartsWith("osx") ? "libusb-1.0.dylib" : "libusb-1.0.so.0", "dfu-util" };
            var records = names.Select(name => new { path = name, bytes = 123, sha256 = new string('A', 64) }).ToArray();
            using var document = System.Text.Json.JsonDocument.Parse(Json.Bytes(new { schema = 1, runtimeIdentifier = rid, files = records }));
            NativeToolCatalog.Validate(document.RootElement, rid, names[0], 123, new string('A', 64)); checks++;
            Reject(() => NativeToolCatalog.Validate(document.RootElement, rid, names[0], 124, new string('A', 64)), "native tool length mismatch");
            Reject(() => NativeToolCatalog.Validate(document.RootElement, rid, names[0], 123, new string('B', 64)), "native tool digest mismatch");
            Reject(() => NativeToolCatalog.Validate(document.RootElement, rid + "-wrong", names[0], 123, new string('A', 64)), "native RID mismatch");
            Reject(() => NativeToolCatalog.Validate(document.RootElement, rid, "../dfu-util", 123, new string('A', 64)), "native path injection");
        }
    }
    private static void PacketChecks()
    {
        var model = Model.Get("numpad");
        var info = new Echo(); var data = WbProtocol.GetInfo(info, model);
        Check(data.FlashBytes == 131072 && data.SramBytes == 28672, "WB geometry");
        info.Chip = 0x00502980; Check(WbProtocol.GetInfo(info, model).ChipId == info.Chip, "not Numpad full-type pinned");
        info.Chip = 0; Reject(() => WbProtocol.GetInfo(info, model), "unknown family"); info.Chip = 0x3A502980;
        info.Protected = true; Reject(() => WbProtocol.GetInfo(info, model), "protection never unlocked"); info.Protected = false;
        for (uint address = Model.FlashBase; address < Model.FlashBase + 131072; address += 256)
        {
            var read = WbProtocol.ReadPacket(address, 256, model); WbProtocol.ValidatePacket(read, false, model);
            Check(BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(4)) == address && read[8] == 0 && read[9] == 1, "READ address/count");
            var erase = WbProtocol.ErasePacket(address, model); WbProtocol.ValidatePacket(erase, true, model);
            Check(erase[0] == 0x5E && BinaryPrimitives.ReadUInt32LittleEndian(erase.AsSpan(8)) == 1, "one page only");
            Reject(() => WbProtocol.ValidatePacket(erase, false, model), "readonly erase guard");
            var write = WbProtocol.WritePacket(address, new byte[256], model); WbProtocol.ValidatePacket(write, true, model);
            Reject(() => WbProtocol.ValidatePacket(write, false, model), "readonly program guard");
        }
        foreach (uint bad in new uint[] { 0, 0x08000001, 0x08020000, 0x1FFFF7E8, 0xFFFFFFFF })
        { Reject(() => WbProtocol.ErasePacket(bad, model), "erase address"); Reject(() => WbProtocol.WritePacket(bad, new byte[256], model), "program address"); }
        foreach (byte op in new byte[] { 0x83, 0x9B, 0x5E, 0x6A }) Reject(() => WbProtocol.ValidatePacket([op, 0xF0], true, model), "no reset/go/mass erase");
        Reject(() => WbProtocol.WritePacket(Model.FlashBase, new byte[255], model), "partial page");
        Reject(() => WbProtocol.ReadPacket(Model.FlashBase + 131071, 2, model), "read end overflow");
        Reject(() => WbProtocol.Check([0x71, 0], 0x72), "wrong ACK command");
        Reject(() => WbProtocol.CheckWrite([0x6A, 0, 0], 0x6A), "long write ACK");
    }
    private static void FileChecks(string root)
    {
        Reject(() => Files.Child(root, "../escape.bin"), "path traversal");
        Reject(() => Files.Child(root, "C:\\outside.bin"), "absolute artifact");
        var artifact = Files.New(root, "original.bin", [1, 2, 3], "fixture");
        Reject(() => Files.New(root, "original.bin", [4, 5, 6], "fixture"), "no overwrite");
        Reject(() => Files.Lock(Path.Combine(root, artifact.Path), 3, new string('0', 64)), "hash lock");
        var locked = Files.Lock(Path.Combine(root, artifact.Path), 3, artifact.Sha256);
        using (locked.Stream)
        {
            Check(locked.Bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "verified bytes are retained independently of the file");
            Reject(() => File.WriteAllBytes(Path.Combine(root, artifact.Path), [9]), "read lock prevents mutation");
            Check(locked.Stream.Length == 3, "rejected writer does not truncate locked input");
            byte[] retained = new byte[3]; locked.Stream.ReadExactly(retained);
            Check(retained.AsSpan().SequenceEqual(locked.Bytes), "locked input remains identical after rejected mutation");
        }
        File.WriteAllBytes(Path.Combine(root, artifact.Path), [4, 5, 6]);
        Check(Files.ReadBounded(Path.Combine(root, artifact.Path), 3).AsSpan().SequenceEqual(new byte[] { 4, 5, 6 }), "disposing the verified input releases its lock");
    }
    private static void PageWriteChecks()
    {
        var model = Model.Get("numpad");
        byte[] original = Enumerable.Range(0, 131072).Select(n => (byte)(n * 19)).ToArray();
        var identity = new InstallationIdentity(2, Guid.NewGuid(), Files.Hash(original));
        byte[] image = InstallationIdentity.CreateImage(original.AsSpan(0, 4096), 131072, identity);
        int marker = InstallationIdentity.Offset(image.Length);
        int[] installOrder = WbProtocol.PageOrder(image, model).ToArray(), restoreOrder = WbProtocol.PageOrder(original, model).ToArray();
        Check(installOrder.Length == 512 && installOrder.Distinct().Count() == 512 && installOrder[0] == marker, "install marker first, every page once");
        Check(restoreOrder.Length == 512 && restoreOrder.Distinct().Count() == 512 && restoreOrder[^1] == marker, "restore marker last, every page once");
        var memory = new MemoryWb(original); WbProtocol.WriteImage(memory, image, model);
        Check(memory.Data.AsSpan().SequenceEqual(image) && memory.Erases == 512 && memory.Writes == 512 && memory.Reads == 512, "actual page algorithm exact install");
        WbProtocol.WriteImage(memory, original, model);
        Check(memory.Data.AsSpan().SequenceEqual(original) && memory.Erases == 1024 && memory.Writes == 1024 && memory.Reads == 1024, "actual page algorithm exact restore");
        foreach (int fail in new[] { 0, 1, 127, 511 })
        {
            var failing = new MemoryWb(original) { FailPage = fail };
            Reject(() => WbProtocol.WriteImage(failing, image, model), "page failure propagates");
            Check(failing.Erases == fail + 1 && failing.Writes == fail && failing.Reads == fail, "no retries or later pages after protection failure");
        }
        var verifyFail = new MemoryWb(original) { CorruptRead = true };
        Reject(() => WbProtocol.WriteImage(verifyFail, image, model), "page mismatch propagates");
        Check(verifyFail.Erases == 1 && verifyFail.Writes == 1 && verifyFail.Reads == 1, "page mismatch stops immediately");
    }
    private static void RequestChecks(string root)
    {
        var request = new AdapterRequest(1, "probe", root, "numpad", new(0x342D, 0xDFA0, 1, "1.2", null, "wb32-dfu"));
        AdapterEngine.ValidateRequest(request); checks++;
        Reject(() => AdapterEngine.ValidateRequest(request with { ConfirmWrite = true }), "readonly confirm rejected");
        Reject(() => AdapterEngine.ValidateRequest(request with { Operation = "install" }), "no implicit write");
        Reject(() => AdapterEngine.ValidateRequest(request with { Schema = 2 }), "request schema");
        Reject(() => JsonSerializerRead("{\"schema\":1,\"operation\":\"list\",\"sessionDirectory\":\"x\",\"execute\":true}"), "unknown JSON property");
    }
    private static AdapterRequest? JsonSerializerRead(string json) => System.Text.Json.JsonSerializer.Deserialize<AdapterRequest>(json, Json.Options);
    private static void OperationLockChecks(string root)
    {
        var device = new FakeDevice(Model.Get("numpad"));
        var selection = new Selection(0x342D, 0xDFA0, 1, "6.7", "", "wb32-dfu");
        int opened = 0;
        var engine = new AdapterEngine(() => { opened++; return new FakeBackend(device, selection); });
        var request = new AdapterRequest(1, "probe", Path.Combine(root, "busy-operation"), "numpad", selection);
        AdapterResponse? refused = null;
        Exception? error = null;
        using (Files.AcquireDeviceOperation())
        {
            var concurrent = new Thread(() => { try { refused = engine.Run(request); } catch (Exception ex) { error = ex; } });
            concurrent.Start();
            Check(concurrent.Join(10000), "competing transaction is refused without waiting");
            Check(error is null && refused is { Status: "blocked", ErrorCode: "DEVICE_OPERATION_BUSY", WriteAttempted: false, Verified: false }, "competing transaction returns a no-write refusal");
            Check(opened == 0 && device.Reads == 0 && device.Writes == 0, "busy transaction cannot create a USB backend");
        }
        var resumed = engine.Run(request with { SessionDirectory = Path.Combine(root, "released-operation") });
        Check(resumed.Status == "ok" && opened == 1, "new explicit transaction can enter after release");
        var failure = new AdapterEngine(() => throw new IOException("synthetic backend failure"));
        Check(failure.Run(request with { SessionDirectory = Path.Combine(root, "failed-operation") }).Status == "blocked", "backend failure is reported");
        Check(engine.Run(request with { SessionDirectory = Path.Combine(root, "after-failed-operation") }).Status == "ok", "backend failure releases the transaction lock");
    }
    private static void StmChecks()
    {
        var selection = new Selection(0x0483, 0xDF11, 1, "6.2", "0123456789AB", "stm32-dfuse");
        string[] selectors = ["-d", "0483:df11", "-S", "0123456789AB", "-p", "1-6.2", "-c", "1", "-i", "0", "-a", "0"];
        Check(Stm32Device.Arguments(selection, "write", "-").SequenceEqual([.. selectors, "-s", "0x08000000", "-D", "-"]), "stdin write keeps all STM identity selectors and the fixed flash base");
        Check(Stm32Device.Arguments(selection, "write", "fixture.bin").SequenceEqual([.. selectors, "-s", "0x08000000", "-D", "fixture.bin"]), "Windows file write keeps the same bounded STM command");
        Check(Stm32Device.Arguments(selection, "read", "fixture.bin").SequenceEqual([.. selectors, "-s", "0x08000000:262144", "-U", "fixture.bin"]), "STM backup still reads the exact whole flash range");
        Reject(() => Stm32Device.Arguments(selection, "write"), "no write without an input");
        Reject(() => Stm32Device.Arguments(selection, "unprotect", "-"), "stdin does not authorize additional commands");
        string listing = "Found DFU: [0483:df11] ver=2200, devnum=4, cfg=1, intf=0, path=\"1-6.2\", alt=0, name=\"@Internal Flash /0x08000000/128*0002Kg\", serial=\"0123456789AB\"";
        var parsed = Stm32Device.ParseListing(listing, selection, Model.Get("pro-iso"));
        Check(parsed.FlashBytes == 262144 && parsed.Serial == selection.Serial, "STM serial/port/map");
        foreach (string id in new[] { "pro-iso", "pro-ansi" })
        foreach (string padding in new[] { " ", "  ", "    " })
        {
            string map = "@Internal Flash" + padding + "/0x08000000/128*0002Kg";
            var padded = Stm32Device.ParseListing(listing.Replace("@Internal Flash /0x08000000/128*0002Kg", map), selection, Model.Get(id));
            Check(padded.FlashBase == Model.FlashBase && padded.FlashBytes == 262144 && padded.SramBytes == 40960,
                id + " STM ASCII label padding preserves exact geometry");
            Check(padded.MemoryMap == map && padded.Serial == selection.Serial && padded.PortPath == selection.PortPath,
                id + " STM label padding retains raw descriptor and selected identity");
        }
        foreach (string old in new[] { "1-6.2", "0123456789AB", "cfg=1", "intf=0", "alt=0", "128*0002Kg" })
            Reject(() => Stm32Device.ParseListing(listing.Replace(old, "wrong"), selection, Model.Get("pro-iso")), "STM selector " + old);
        Reject(() => Stm32Device.ParseListing(listing + "\n" + listing, selection, Model.Get("pro-iso")), "STM duplicate");
        Reject(() => Stm32Device.ParseListing(listing + "\n" + listing.Replace("@Internal Flash /", "@Internal Flash  /"), selection, Model.Get("pro-iso")), "STM padded duplicate still rejected");
        foreach (string bad in new[]
        {
            "@Option Bytes /0x1FFFF800/01*016 e", "@Internal Flash /0x08000000/128*0002Ke",
            "@Internal Flash /0x08000000/64*0002Kg", "@Internal Flash /0x08000000/128*0002Kg:unprotect",
            "@Internal Flash/0x08000000/128*0002Kg", "@Internal Flash\t/0x08000000/128*0002Kg",
            "@Internal Flash \n/0x08000000/128*0002Kg", "@Internal Flash\u00A0/0x08000000/128*0002Kg",
            "@Internal Flash  /0x08000001/128*0002Kg", "@Internal Flash  /0x08000000/256*0001Kg",
            "@Internal Flash  /0x08000000/128*0001Kg", "@Internal Flash  /0x08000000/128*0004Kg",
            "@Internal Flash  /0x08000000/4294967296*0002Kg", "@Internal Flash  /0x08000000/128*4294967296Kg",
            "@Internal Flash  /0x08000000/128*0002Kg,1*0002Kg", "@Internal Flash  /0x08000000/128*0002Kg\n",
            "@Internal Flash  /0x08000000/128*0002Kg\r\n", "@Internal Flash  /0x08000000/128*0002Kg "
        })
        {
            Reject(() => Stm32Device.ValidateMap(bad), "STM map permissions and bound");
            Reject(() => Stm32Device.ParseListing(listing.Replace("@Internal Flash /0x08000000/128*0002Kg", bad), selection, Model.Get("pro-iso")),
                "STM listing rejects invalid map without relaxing selector binding");
        }
    }
    private static void EngineChecks(string root, string id)
    {
        Model model = Model.Get(id); var device = new FakeDevice(model);
        var selection = new Selection(device.Fingerprint.Vid, device.Fingerprint.Pid, 1, "6.7", device.Fingerprint.Serial, model.Transport);
        AdapterResponse Invoke(AdapterRequest request) => new AdapterEngine(() => new FakeBackend(device, selection)).Run(request);
        string Folder(string name) => Path.Combine(root, id + "-" + name + "-" + Guid.NewGuid().ToString("N"));
        var probeRequest = new AdapterRequest(1, "probe", Folder("probe"), id, selection);
        var probe = Invoke(probeRequest);
        Check(probe.Status == "ok" && device.Writes == 0, id + " readonly probe");
        var capture = Invoke(probeRequest with { Operation = "capture", SessionDirectory = Folder("capture"), ExpectedFingerprintSha256 = probe.FingerprintSha256 });
        Check(capture.Status == "ok" && capture.Artifacts.Count(a => a.Kind == "original-read") == 2 && device.Writes == 0, id + " double capture");
        byte[] original = device.Image.ToArray();
        var identity = new InstallationIdentity((byte)model.BoardId, Guid.NewGuid(), Files.Hash(original));
        byte[] application = ReadApprovedFirmware(id);
        byte[] image = InstallationIdentity.CreateImage(application, model.FlashBytes, identity);
        string bundleRoot = Folder("bundle"); Directory.CreateDirectory(bundleRoot);
        var imageArtifact = Files.New(bundleRoot, "full.bin", image, "fixture");
        var bundle = new Bundle(1, id, model.BoardId, model.Transport, Model.FlashBase, model.FlashBytes, model.SramBytes, new("full.bin", image.Length, imageArtifact.Sha256), KnownFirmwareImages.Validate(image, id, (byte)model.BoardId));
        Files.New(bundleRoot, "bundle.json", Json.Bytes(bundle), "fixture");
        var install = probeRequest with { Operation = "install", SessionDirectory = Folder("install"), BundlePath = Path.Combine(bundleRoot, "bundle.json"), BackupPath = Path.Combine(capture.EvidenceDirectory, "capture.json"), ConfirmWrite = true, ExpectedFingerprintSha256 = probe.FingerprintSha256, ExpectedBackupSha256 = identity.OriginalSha256, ExpectedInstallationId = identity.InstallationId };
        var bad = Invoke(install with { SessionDirectory = Folder("bad-id"), ExpectedInstallationId = Guid.NewGuid() });
        Check(bad.Status == "blocked" && !bad.WriteAttempted && device.Writes == 0, id + " wrong ID prewrite");
        var stale = Invoke(install with { SessionDirectory = Folder("stale-device"), ExpectedFingerprintSha256 = new string('0', 64) });
        Check(stale.Status == "blocked" && !stale.WriteAttempted && device.Writes == 0, id + " changed fingerprint no write");
        var wrongBackup = Invoke(install with { SessionDirectory = Folder("wrong-backup"), ExpectedBackupSha256 = new string('0', 64) });
        Check(wrongBackup.Status == "blocked" && !wrongBackup.WriteAttempted && device.Writes == 0, id + " wrong original hash no write");
        device.Image[100] ^= 0x44;
        var changedBefore = Invoke(install with { SessionDirectory = Folder("changed-before") });
        Check(changedBefore.Status == "blocked" && !changedBefore.WriteAttempted && device.Writes == 0, id + " original changed after capture no write");
        device.Image = original.ToArray();
        int priorReads = device.Reads;
        var result = Invoke(install);
        Check(result.Verified && result.WriteAttempted && result.InstallationId == identity.InstallationId && device.Writes == 1 && device.Reads - priorReads == 4, id + " install before+after pairs");
        Check(device.Image.AsSpan().SequenceEqual(image), id + " exact full image");
        var installedCapture = Invoke(probeRequest with { Operation = "capture", SessionDirectory = Folder("installed-capture"), ExpectedFingerprintSha256 = probe.FingerprintSha256 });
        Check(installedCapture.Status == "ok" && device.Writes == 1, id + " installed capture remains read-only for restore selection");
        UpdateChecks();
        var restore = install with { Operation = "restore", BundlePath = null, SessionDirectory = Folder("restore") };
        var wrongRestore = Invoke(restore with { SessionDirectory = Folder("wrong-restore"), ExpectedInstallationId = Guid.NewGuid() });
        Check(!wrongRestore.Verified && !wrongRestore.WriteAttempted && device.Writes == 2, id + " foreign restore ID rejected");
        int markerOffset = InstallationIdentity.Offset(model.FlashBytes);
        device.Image[markerOffset] ^= 1;
        var missingMarker = Invoke(restore with { SessionDirectory = Folder("missing-marker") });
        Check(!missingMarker.Verified && !missingMarker.WriteAttempted && device.Writes == 2, id + " missing recovery marker no restore");
        device.Image[markerOffset] ^= 1;
        device.Image[markerOffset + 60] ^= 1;
        var badMarker = Invoke(restore with { SessionDirectory = Folder("bad-marker") });
        Check(!badMarker.Verified && !badMarker.WriteAttempted && device.Writes == 2, id + " damaged recovery marker no restore");
        device.Image[markerOffset + 60] ^= 1;
        priorReads = device.Reads; var restored = Invoke(restore);
        Check(restored.Verified && device.Writes == 3 && device.Reads - priorReads == 4 && device.Image.AsSpan().SequenceEqual(original), id + " restore own original and two readbacks after update");
        device.ReadFault = true;
        var faulty = Invoke(install with { SessionDirectory = Folder("bad-read") });
        Check(!faulty.WriteAttempted && !faulty.Verified && device.Writes == 3, id + " differing reads no write"); device.ReadFault = false;
        device.WriteFault = true;
        var failed = Invoke(install with { SessionDirectory = Folder("write-error") });
        Check(failed.WriteAttempted && !failed.Verified && failed.Status == "failed", id + " partial write never success");
        device.WriteFault = false; device.Image = original.ToArray(); device.AfterFault = true;
        var failedAfter = Invoke(install with { SessionDirectory = Folder("verify-error") });
        Check(failedAfter.WriteAttempted && !failedAfter.Verified, id + " mismatched full readback never success");

        void UpdateChecks()
        {
            // Distinct, nontrivial data over every reserved configuration byte must survive update.
            for (int i = model.FlashBytes - InstallationIdentity.StorageBytes; i < model.FlashBytes; i++) device.Image[i] = (byte)(i * 31 + 17);
            byte[] current = device.Image.ToArray();
            string updateRoot = Folder("update-bundle"); Directory.CreateDirectory(updateRoot);
            var updateImage = Files.New(updateRoot, "full.bin", current, "fixture");
            var updateBundle = bundle with { Image = new("full.bin", current.Length, updateImage.Sha256) };
            Files.New(updateRoot, "bundle.json", Json.Bytes(updateBundle), "fixture");
            var update = install with { Operation = "update", SessionDirectory = Folder("update"), BundlePath = Path.Combine(updateRoot, "bundle.json"), ExpectedCurrentSha256 = Files.Hash(current) };
            void NoWrite(AdapterRequest request, string label)
            {
                var result = Invoke(request);
                Check(result.Status == "blocked" && !result.WriteAttempted && !result.Verified && device.Writes == 1, id + " update rejects " + label);
            }
            NoWrite(update with { SessionDirectory = Folder("update-stale"), ExpectedCurrentSha256 = new string('0', 64) }, "stale prewrite bytes");
            device.Image[100] ^= 1;
            NoWrite(update with { SessionDirectory = Folder("update-unknown"), ExpectedCurrentSha256 = Files.Hash(device.Image) }, "unknown application bytes");
            device.Image = current.ToArray();
            device.Image[application.Length + 1] = 0;
            NoWrite(update with { SessionDirectory = Folder("update-code-gap"), ExpectedCurrentSha256 = Files.Hash(device.Image) }, "modified unused code gap");
            device.Image = current.ToArray();
            device.Image[InstallationIdentity.Offset(model.FlashBytes)] ^= 1;
            NoWrite(update with { SessionDirectory = Folder("update-no-id"), ExpectedCurrentSha256 = Files.Hash(device.Image) }, "missing installation identity");
            device.Image = current.ToArray();
            // The old install bundle has matching approved code and identity but blank configuration.
            NoWrite(update with { SessionDirectory = Folder("update-reset-config"), BundlePath = install.BundlePath }, "configuration reset");
            device.ReadFault = true;
            NoWrite(update with { SessionDirectory = Folder("update-read-pair") }, "different fresh reads"); device.ReadFault = false;
            int beforeReads = device.Reads;
            var success = Invoke(update);
            Check(success.Verified && success.WriteAttempted && device.Writes == 2 && device.Reads - beforeReads == 4, id + " update has two fresh reads and two readbacks");
            Check(device.Image.AsSpan().SequenceEqual(current) && InstallationIdentity.Read(device.Image) == identity, id + " update preserves every configuration byte and original recovery identity");
        }
    }

    private static byte[] ReadApprovedFirmware(string id)
    {
        string filename = (id == "pro-iso" ? "pro" : id) + ".bin";
        if (firmwareDirectory is not null) return Files.ReadBounded(Path.Combine(firmwareDirectory, filename), 262144);
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            foreach (var relative in new[] { "Firmware", Path.Combine("firmware", "build") })
            {
                string file = Path.Combine(parent.FullName, relative, filename);
                if (File.Exists(file)) return Files.ReadBounded(file, 262144);
            }
        throw new InvalidDataException("Self-tests need reviewed firmware files. Supply --self-test --firmware-directory DIRECTORY.");
    }
    private sealed class Echo : IWbExchange
    {
        internal uint Chip = 0x3A502980; internal bool Protected;
        internal byte BootVersion = 3; internal uint FlashBytes = 131072, SramBytes = 28672;
        public byte[] Exchange(byte[] request)
        {
            if (Protected) return [request[0], 0x1A];
            byte[] bytes = new byte[16]; bytes[0] = 1; bytes[2] = 13; bytes[3] = BootVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), Chip); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), FlashBytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), SramBytes); return bytes;
        }
    }
    private sealed class MemoryWb(byte[] initial, Model? model = null) : IWbExchange
    {
        private readonly Model selectedModel = model ?? Model.Get("numpad");
        internal readonly byte[] Data = initial.ToArray(); internal int Erases, Writes, Reads;
        internal int FailPage = -1; internal bool CorruptRead;
        public byte[] Exchange(byte[] request)
        {
            WbProtocol.ValidatePacket(request, true, selectedModel);
            int offset = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4)) - Model.FlashBase));
            if (request[0] == 0x5E)
            {
                int page = Erases++; if (page == FailPage) return [0x5E, 0x1A];
                Data.AsSpan(offset, 256).Fill(0xFF); return [0x5E, 0];
            }
            if (request[0] == 0x6A)
            { Writes++; Check(Data.AsSpan(offset, 256).ToArray().All(b => b == 255), "page erased before program"); request.AsSpan(12).CopyTo(Data.AsSpan(offset, 256)); return [0x6A, 0]; }
            if (request[0] == 0x72)
            { Reads++; byte[] result = new byte[258]; result[0] = 0x72; Data.AsSpan(offset, 256).CopyTo(result.AsSpan(2)); if (CorruptRead) result[2] ^= 1; return result; }
            throw new InvalidDataException("unexpected mock opcode");
        }
    }
    private sealed class FakeBackend(FakeDevice device, Selection selection) : IBackend
    { public Selection[] List() => [selection]; public IDevice Open(Selection selected, Model model, string folder) { Check(selected == selection, "exact selection forwarded"); device.BeforeWriteReads = device.Reads; return device; } public void Dispose() { } }
    private sealed class FakeDevice : IDevice
    {
        internal readonly Fingerprint Fingerprint; internal byte[] Image; internal int Writes, Reads, BeforeWriteReads;
        internal bool ReadFault, WriteFault, AfterFault; private bool wroteThisCall;
        internal FakeDevice(Model model)
        {
            Image = new byte[model.FlashBytes]; Array.Fill(Image, (byte)0xFF); BinaryPrimitives.WriteUInt32LittleEndian(Image, 0x20001000); BinaryPrimitives.WriteUInt32LittleEndian(Image.AsSpan(4), Model.FlashBase + 0x101);
            bool stm = model.Transport == "stm32-dfuse";
            Fingerprint = new(model.Transport, (ushort)(stm ? 0x0483 : 0x342D), (ushort)(stm ? 0xDF11 : 0xDFA0), 1, "6.7", stm ? "0123456789AB" : "", 1, 0, 0, 0x100, stm ? null : 0x3A502980, model.BootVersion, Model.FlashBase, model.FlashBytes, model.SramBytes, stm ? "@Internal Flash /0x08000000/128*0002Kg" : null, null, stm ? "usb-serial-and-port" : "transport-session");
        }
        public Fingerprint Probe(Model model) => Fingerprint;
        public byte[] ReadAll(Model model) { Reads++; var bytes = Image.ToArray(); if ((ReadFault && Reads % 2 == 0) || (AfterFault && wroteThisCall)) bytes[100] ^= 1; return bytes; }
        public void WriteAll(Model model, byte[] image, Action beforeFirstWrite, IRetainedLoaderWriteGrant? engineeringGrant = null)
        {
            if (model.ProtectedPrefixBytes != 0)
            {
                if (engineeringGrant == null) throw new InvalidDataException("ENGINEERING_TRANSPORT_GRANT_REQUIRED");
                engineeringGrant.Consume(model, Fingerprint, image, Image.AsSpan(0, model.ProtectedPrefixBytes).ToArray());
            }
            else if (engineeringGrant != null) throw new InvalidDataException("ENGINEERING_AUTHORIZATION_NOT_APPLICABLE");
            Check(Reads - BeforeWriteReads == 2, "two fresh captures before write");
            beforeFirstWrite(); Writes++; wroteThisCall = true;
            if (WriteFault) throw new IOException("FAKE_PARTIAL_WRITE");
            if (model.ProtectedPrefixBytes != 0)
            {
                var memory = new MemoryWb(Image, model); WbProtocol.WriteImage(memory, image, model); Image = memory.Data;
                Check(memory.Erases == 912 && memory.Writes == 912 && memory.Reads == 912, "engineering fake transport uses exact writable pages");
            }
            else Image = image.ToArray();
        }
        public object[] Log => [new { reads = Reads, writes = Writes }];
        public void Dispose() => wroteThisCall = false;
    }
}
