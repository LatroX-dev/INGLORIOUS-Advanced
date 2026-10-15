using System.Buffers.Binary;
using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal static partial class SelfTests
{
    private static string CheckRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "tools", "InGlorious.FirmwareAdapter", "InGlorious.FirmwareAdapter.csproj"))) root = root.Parent;
        if (root == null) throw new IOException("Offline adapter checks require a build inside the source project.");
        return Path.Combine(root.FullName, "workspace", "checks");
    }
    private static string CheckDirectory()
    {
        string directory = Path.Combine(CheckRoot(), "inglorious-adapter-tests-" + Guid.NewGuid().ToString("N"));
        Files.NoLinks(directory);
        return directory;
    }
    private static void CleanCheckDirectory(string root)
    {
        string full = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(full), CheckRoot(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Path.GetFileName(full).StartsWith("inglorious-adapter-tests-", StringComparison.Ordinal)) throw new IOException("Offline check cleanup escaped its project directory.");
        Files.NoLinks(full);
        Directory.Delete(full, true);
    }

    private static void EngineeringChecks(string root)
    {
        var model = Model.Get(Model.EngineeringDeviceId, allowEngineering: true);
        Check(model is { BoardId: 16, FlashBytes: 262144, SramBytes: 36864, EngineeringOnly: true, BootVersion: 4 }, "separate engineering profile");
        var normal = Model.Get(Model.EngineeringDeviceId);
        Check(!normal.EngineeringOnly && normal.ProtectedPrefixBytes == 0x7000 && normal.BootVersion == 4, "normal board16 retains exact loader and boot profile");
        WbProtocol.ValidateModel(normal); checks++;
        Reject(() => WbProtocol.ValidateModel(model with { FlashBytes = 131072 }), "engineering profile cannot borrow production geometry");
        Reject(() => WbProtocol.ValidateModel(Model.Get("numpad") with { FlashBytes = 262144, SramBytes = 36864 }), "production profile cannot borrow engineering geometry");
        Reject(() => WbProtocol.ValidateModel(Model.Get("pro-iso")), "STM profile cannot select WB packets");

        var info = new Echo { Chip = 0x2980, BootVersion = 4, FlashBytes = 262144, SramBytes = 36864 };
        var data = WbProtocol.GetInfo(info, model);
        Check(data is { BootVersion: 4, FlashBytes: 262144, SramBytes: 36864 }, "engineering WB response geometry");
        string[] productionIds = ["pro-iso", "numpad", "pro-ansi", "pro-rev2-ansi", "pro-rev2-iso",
            "gmmk2-65-ansi", "gmmk2-65-iso", "gmmk2-96-ansi", "gmmk2-96-iso", "gmmk3-65-ansi", "gmmk3-65-iso",
            "gmmk3-75-ansi", "gmmk3-75-iso", "gmmk3-100-ansi", "gmmk3-100-iso"];
        for (int index = 0; index < productionIds.Length; index++)
        {
            var production = Model.Get(productionIds[index]);
            bool stm = index is 0 or 2;
            Check(production.BoardId == index + 1 && production == Model.Get(production.DeviceId, true) && !production.EngineeringOnly &&
                production.FlashBytes == (stm ? 262144 : 131072) && production.SramBytes == (stm ? 40960 : 28672) && production.BootVersion == (stm ? null : (byte?)3), "production model retains exact geometry and boot version " + production.DeviceId);
            if (!stm) Reject(() => WbProtocol.GetInfo(info, production), "production WB rejects boot 0.4/256 KiB " + production.DeviceId);
        }
        Reject(() => WbProtocol.GetInfo(new Echo(), model), "engineering WB rejects production boot and geometry");
        info.BootVersion = 3; Reject(() => WbProtocol.GetInfo(info, model), "engineering requires boot 0.4"); info.BootVersion = 4;
        info.FlashBytes = 131072; Reject(() => WbProtocol.GetInfo(info, model), "engineering requires complete 256 KiB flash"); info.FlashBytes = 262144;
        info.SramBytes = 28672; Reject(() => WbProtocol.GetInfo(info, model), "engineering requires 36 KiB SRAM"); info.SramBytes = 36864;
        info.Chip = 0; Reject(() => WbProtocol.GetInfo(info, model), "engineering still rejects unknown WB family"); info.Chip = 0x2980;
        info.Protected = true; Reject(() => WbProtocol.GetInfo(info, model), "engineering never unlocks protection");

        var finalRead = WbProtocol.ReadPacket(0x0803FF00, 256, model);
        Check(Convert.ToHexString(finalRead) == "7200000000FF03080001", "engineering final page encoding");
        WbProtocol.ValidatePacket(finalRead, false, model); checks++;
        Reject(() => WbProtocol.ValidatePacket(finalRead, false, Model.Get("numpad")), "production session rejects engineering upper range");
        foreach (uint address in new uint[] { 0x08040000, 0x1FFFF7E8, uint.MaxValue })
        {
            Reject(() => WbProtocol.ReadPacket(address, 1, model), "engineering read bound");
            Reject(() => WbProtocol.ErasePacket(address, model), "engineering erase bound");
            Reject(() => WbProtocol.WritePacket(address, new byte[256], model), "engineering write bound");
        }
        Reject(() => WbProtocol.ReadPacket(0x0803FFFF, 2, model), "engineering boundary crossing");
        Reject(() => WbProtocol.ValidatePacket(WbProtocol.ErasePacket(0x0803FF00, model), false, model), "engineering readonly rejects erase");
        Reject(() => WbProtocol.ValidatePacket(WbProtocol.WritePacket(0x0803FF00, new byte[256], model), false, model), "engineering readonly rejects program");
        for (int offset = 0; offset < model.ProtectedPrefixBytes; offset += WbProtocol.Page)
        {
            uint address = Model.FlashBase + (uint)offset;
            WbProtocol.ValidatePacket(WbProtocol.ReadPacket(address, 256, model), false, model); checks++;
            Reject(() => WbProtocol.ErasePacket(address, model), "retained loader erase forbidden");
            Reject(() => WbProtocol.WritePacket(address, new byte[256], model), "retained loader program forbidden");
            Reject(() => WbProtocol.ValidatePacket(WbProtocol.ErasePacket(address, Model.Get("numpad")), true, model), "raw retained loader erase rejected in engineering session");
            Reject(() => WbProtocol.ValidatePacket(WbProtocol.WritePacket(address, new byte[256], Model.Get("numpad")), true, model), "raw retained loader program rejected in engineering session");
        }

        byte[] original = Enumerable.Range(0, model.FlashBytes).Select(n => (byte)(n * 17 + n / 65536)).ToArray();
        var identity = new InstallationIdentity(16, Guid.NewGuid(), Files.Hash(original));
        byte[] image = Enumerable.Repeat((byte)0xFF, model.FlashBytes).ToArray();
        identity.Encode().CopyTo(image, InstallationIdentity.Offset(model.FlashBytes));
        original.AsSpan(0, model.ProtectedPrefixBytes).CopyTo(image);
        int marker = InstallationIdentity.Offset(model.FlashBytes);
        var installOrder = WbProtocol.PageOrder(image, model).ToArray();
        var restoreOrder = WbProtocol.PageOrder(original, model).ToArray();
        Check(installOrder.Length == 912 && installOrder.Distinct().Count() == 912 && installOrder[0] == marker && installOrder.Min() == 0x7000 && installOrder.Max() == 261888, "engineering install marker first and every writable page once without loader");
        Check(restoreOrder.Length == 912 && restoreOrder.Distinct().Count() == 912 && restoreOrder[^1] == marker && restoreOrder.Min() == 0x7000, "engineering restore marker last without loader");
        Reject(() => WbProtocol.PageOrder(image, Model.Get("numpad")).ToArray(), "production write ordering refuses 256 KiB image");
        Reject(() => WbProtocol.PageOrder(new byte[131072], model).ToArray(), "engineering write ordering refuses 128 KiB image");
        var memory = new MemoryWb(original, model);
        WbProtocol.WriteImage(memory, image, model);
        Check(memory.Data.AsSpan().SequenceEqual(image) && memory.Erases == 912 && memory.Writes == 912 && memory.Reads == 912, "engineering mock page install preserves loader and verifies every writable page");
        WbProtocol.WriteImage(memory, original, model);
        Check(memory.Data.AsSpan().SequenceEqual(original) && memory.Erases == 1824 && memory.Writes == 1824 && memory.Reads == 1824, "engineering mock restore preserves loader and verifies every writable page");
        foreach (int page in new[] { 0, 456, 911 })
        {
            var protectedMemory = new MemoryWb(original, model) { FailPage = page };
            Reject(() => WbProtocol.WriteImage(protectedMemory, image, model), "engineering protected erase aborts");
            Check(protectedMemory.Erases == page + 1 && protectedMemory.Writes == page && protectedMemory.Reads == page, "engineering protection failure has no retry or later writes");
        }
        var mismatchedMemory = new MemoryWb(original, model) { CorruptRead = true };
        Reject(() => WbProtocol.WriteImage(mismatchedMemory, image, model), "engineering page verification aborts");
        Check(mismatchedMemory.Erases == 1 && mismatchedMemory.Writes == 1 && mismatchedMemory.Reads == 1, "engineering verification failure stops at first page");
        Reject(() => KnownFirmwareImages.Validate(image, model.DeviceId, 16), "engineering marker never authorizes an unlisted firmware image");

        var device = new FakeDevice(model);
        var selection = new Selection(0x342D, 0xDFA0, 1, "6.7", "", model.Transport);
        int opened = 0;
        var engine = new AdapterEngine(() => { opened++; return new FakeBackend(device, selection); });
        string Folder(string suffix) => Path.Combine(root, "engineering-" + suffix);
        var request = new AdapterRequest(1, "probe", Folder("default"), model.DeviceId, selection);
        var normalProbe = engine.Run(request);
        Check(normalProbe is { Status: "ok", CanCapture: true, WriteAttempted: false } && opened == 1, "normal board16 probe is available without engineering authorization");
        var probe = engine.Run(request with { SessionDirectory = Folder("probe") }, engineering: true);
        Check(probe is { Status: "ok", CanCapture: true, CanInstall: false, CanRestore: false, WriteAttempted: false } && opened == 2 && device.Writes == 0, "explicit engineering mode retains separate authorization restrictions");
        var capture = engine.Run(request with { Operation = "capture", SessionDirectory = Folder("capture") }, engineering: true);
        Check(capture is { Status: "ok", CanCapture: true, CanInstall: false, CanRestore: false, WriteAttempted: false } && device.Reads == 2 && device.Writes == 0, "engineering capture persists two reads and never advertises install");
        var manifest = Json.Read<CaptureManifest>(Path.Combine(capture.EvidenceDirectory, "capture.json"));
        Check(manifest.DeviceId == model.DeviceId && manifest.Artifacts.Length == 2 && manifest.Artifacts.All(a => a.Bytes == 262144) && !manifest.RestoreTested, "engineering capture records separate model and full geometry");
        foreach (string operation in new[] { "install", "update", "restore" })
        {
            int previousOpens = opened;
            var write = request with { Operation = operation, SessionDirectory = Folder(operation), ConfirmWrite = true,
                ExpectedFingerprintSha256 = capture.FingerprintSha256, ExpectedBackupSha256 = Files.Hash(device.Image), ExpectedInstallationId = Guid.NewGuid(),
                BackupPath = Path.Combine(capture.EvidenceDirectory, "capture.json"), BundlePath = operation == "restore" ? null : "unreviewed-candidate",
                ExpectedCurrentSha256 = operation == "update" ? Files.Hash(device.Image) : null };
            var blocked = engine.Run(write, engineering: true);
            Check(blocked is { Status: "blocked", WriteAttempted: false, Verified: false } &&
                blocked.ErrorCode == (operation == "update" ? "ENGINEERING_OPERATION_NOT_ALLOWED" : "ENGINEERING_WRITE_AUTHORIZATION_REQUIRED") &&
                opened == previousOpens && device.Writes == 0, "engineering " + operation + " remains blocked before opening hardware");
        }
        EngineeringAuthorizationChecks(root, model);
    }

    private static void EngineeringAuthorizationChecks(string root, Model model)
    {
        var device = new FakeDevice(model);
        byte[] original = device.Image.ToArray();
        byte[] application = Enumerable.Range(0, 512).Select(n => (byte)n).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(application, 0x20005000);
        BinaryPrimitives.WriteUInt32LittleEndian(application.AsSpan(4), 0x08007101);
        var identity = new InstallationIdentity(16, Guid.NewGuid(), Files.Hash(original));
        byte[] image = Enumerable.Repeat((byte)0xFF, model.FlashBytes).ToArray();
        identity.Encode().CopyTo(image, InstallationIdentity.Offset(model.FlashBytes));
        original.AsSpan(0, EngineeringPolicy.ApplicationOffset).CopyTo(image);
        application.CopyTo(image, EngineeringPolicy.ApplicationOffset);
        var candidate = new EngineeringPolicy.Candidate(application.Length, Files.Hash(application), "0.11.1");
        Check(EngineeringPolicy.ValidateCandidateImage(image, model, candidate) == "0.11.1", "synthetic offset-linked candidate identity and vectors");
        EngineeringPolicy.ValidatePrefixEquality(model, original, original, image); checks++;
        foreach (int offset in new[] { 0, 0x6FFF })
        {
            byte[] changed = image.ToArray(); changed[offset] ^= 1;
            Reject(() => EngineeringPolicy.ValidatePrefixEquality(model, original, original, changed), "prepared image must retain exact loader");
            byte[] live = original.ToArray(); live[offset] ^= 1;
            Reject(() => EngineeringPolicy.ValidatePrefixEquality(model, original, live, image), "fresh read must retain exact loader");
        }
        foreach (int offset in new[] { 0x7008, 0x7200, InstallationIdentity.Offset(model.FlashBytes) + 64 })
        {
            byte[] changed = image.ToArray(); changed[offset] ^= 1;
            Reject(() => EngineeringPolicy.ValidateCandidateImage(changed, model, candidate), "candidate application/gap/identity page cannot change");
        }
        foreach (uint reset in new uint[] { 0x08000101, 0x08007000, 0x08007201 })
        {
            byte[] changed = image.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(0x7004), reset);
            var invalid = candidate with { Sha256 = Files.Hash(changed.AsSpan(0x7000, application.Length).ToArray()) };
            Reject(() => EngineeringPolicy.ValidateCandidateImage(changed, model, invalid), "approved code must have relocated application vectors");
        }
        var selection = new Selection(0x342D, 0xDFA0, 1, "6.7", "", model.Transport);
        var request = new AdapterRequest(1, "install", Path.Combine(root, "engineering-scoped-write"), model.DeviceId, selection,
            Files.Hash(Json.Bytes(device.Fingerprint)), "unopened-bundle.json", "unopened-capture.json", true, Files.Hash(original), identity.InstallationId);
        var authorization = new EngineeringWriteAuthorization(1, "install", model.DeviceId, request.SessionDirectory, device.Fingerprint, request.ExpectedFingerprintSha256!,
            request.ExpectedBackupSha256!, identity.InstallationId, Files.Hash(image), candidate.Bytes, candidate.Sha256, EngineeringPolicy.ResidentLoaderSha256);
        EngineeringPolicy.ValidateScope(authorization, request, model, candidate); checks++;
        foreach (var changed in new[] {
            authorization with { Schema = 2 }, authorization with { Operation = "restore" }, authorization with { DeviceId = "numpad" },
            authorization with { FingerprintSha256 = new string('0', 64) }, authorization with { OriginalSha256 = new string('0', 64) },
            authorization with { InstallationId = Guid.NewGuid() }, authorization with { CandidateBytes = candidate.Bytes + 1 },
            authorization with { CandidateSha256 = new string('0', 64) }, authorization with { RetainedPrefixSha256 = new string('0', 64) },
            authorization with { PreparedImageSha256 = new string('a', 64) },
            authorization with { Fingerprint = device.Fingerprint with { PortPath = "6.8" } } })
            Reject(() => EngineeringPolicy.ValidateScope(changed, request, model, candidate), "authorization scope modification rejected");
        Reject(() => EngineeringPolicy.ValidateScope(authorization, request with { ConfirmWrite = false }, model, candidate), "authorization does not replace confirmation");
        Reject(() => EngineeringPolicy.ValidateScope(authorization, request with { Operation = "update", ExpectedCurrentSha256 = Files.Hash(image) }, model, candidate), "engineering update remains forbidden");
        Reject(() => EngineeringPolicy.ValidateScope(authorization, request with { Selection = selection with { PortPath = "6.8" } }, model, candidate), "authorization does not move physical port");
        Reject(() => EngineeringPolicy.ValidateScope(authorization, request with { Selection = selection with { Serial = null } }, model, candidate), "engineering serial selector is explicit even when empty");
        foreach (string port in new[] { "", "0", "6.0", "6.256", "06.7", "6..7", "1.2.3.4.5.6.7.8.9" })
        {
            var f = device.Fingerprint with { PortPath = port }; string hash = Files.Hash(Json.Bytes(f));
            Reject(() => EngineeringPolicy.ValidateScope(authorization with { Fingerprint = f, FingerprintSha256 = hash },
                request with { ExpectedFingerprintSha256 = hash, Selection = selection with { PortPath = port } }, model, candidate), "malformed authorized port rejected");
        }
        Reject(() => JsonSerializerRead("{\"schema\":1,\"operation\":\"list\",\"sessionDirectory\":\"x\",\"engineeringWriteAuthorization\":\"file\"}"), "request JSON cannot enable engineering authorization");
        if (!EngineeringPolicy.HasReviewedCandidate) Reject(() => EngineeringPolicy.RequireReviewedCandidate(), "compiled candidate slot is empty");
        Reject(() => EngineeringPolicy.Authorize(authorization, request, model, device.Fingerprint, original, original, image), "synthetic matching authorization cannot issue a live grant");
        string path = Path.Combine(root, "engineering-authorization.json");
        Files.New(root, Path.GetFileName(path), Json.Bytes(authorization), "synthetic-authorization");
        int opens = 0;
        var engine = new AdapterEngine(() => { opens++; return new FakeBackend(device, selection); });
        var blocked = engine.Run(request, engineering: true, engineeringAuthorizationPath: path);
        Check(blocked is { Status: "blocked", WriteAttempted: false } && blocked.ErrorCode ==
            (EngineeringPolicy.HasReviewedCandidate ? "ENGINEERING_AUTHORIZATION_SCOPE_MISMATCH" : "ENGINEERING_CANDIDATE_NOT_REVIEWED") && opens == 0,
            "synthetic authorization cannot override compiled whitelist or open hardware");
        var production = request with { SessionDirectory = Path.Combine(root, "engineering-auth-production"), DeviceId = "numpad" };
        blocked = engine.Run(production, engineering: true, engineeringAuthorizationPath: path);
        Check(blocked is { Status: "blocked", ErrorCode: "ENGINEERING_AUTHORIZATION_NOT_APPLICABLE", WriteAttempted: false } && opens == 0, "engineering authorization never changes production path");
    }

    internal static int RunEngineering(string candidatePath, string originalPath, string repeatPath)
    {
        string root = CheckDirectory(); Directory.CreateDirectory(root);
        try
        {
            EngineeringChecks(root);
            EngineeringRealFixtureChecks(root, candidatePath, originalPath, repeatPath);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(Json.Bytes(new { passed = checks, engineeringEndToEnd = true,
                hardwareAccess = false, nativeLibraryLoaded = false, toolProcessesStarted = false })));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { CleanCheckDirectory(root); }
    }

    private static void EngineeringRealFixtureChecks(string root, string candidatePath, string originalPath, string repeatPath)
    {
        var model = Model.Get(Model.EngineeringDeviceId, true);
        var candidate = EngineeringPolicy.RequireReviewedCandidate();
        Check(!string.Equals(Path.GetFullPath(originalPath), Path.GetFullPath(repeatPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "real original pair uses distinct paths");
        byte[] application = Files.ReadBounded(candidatePath, model.FlashBytes);
        using var applicationLock = Files.Lock(candidatePath, candidate.Bytes, candidate.Sha256).Stream;
        byte[] original = Files.ReadBounded(originalPath, model.FlashBytes), repeat = Files.ReadBounded(repeatPath, model.FlashBytes);
        Check(original.Length == model.FlashBytes && repeat.AsSpan().SequenceEqual(original), "actual complete original pair matches");
        using var originalLock = Files.Lock(originalPath, model.FlashBytes, Files.Hash(original)).Stream;
        using var repeatLock = Files.Lock(repeatPath, model.FlashBytes, Files.Hash(original)).Stream;
        Check(Files.Hash(original.AsSpan(0, model.ProtectedPrefixBytes).ToArray()) == EngineeringPolicy.ResidentLoaderSha256, "actual original has reviewed resident loader");
        AdapterEngine.ValidateVectors(original, model); checks++;
        var identity = new InstallationIdentity(16, Guid.NewGuid(), Files.Hash(original));
        byte[] image = Enumerable.Repeat((byte)0xFF, model.FlashBytes).ToArray();
        original.AsSpan(0, model.ProtectedPrefixBytes).CopyTo(image);
        application.CopyTo(image, EngineeringPolicy.ApplicationOffset);
        identity.Encode().CopyTo(image, InstallationIdentity.Offset(model.FlashBytes));
        Check(EngineeringPolicy.ValidateCandidateImage(image, model) == candidate.ReleaseId, "real relocated candidate matches immutable approval");
        var device = new FakeDevice(model) { Image = original.ToArray() };
        var selection = new Selection(0x342D, 0xDFA0, 1, "6.7", "", model.Transport);
        int opens = 0;
        var engine = new AdapterEngine(() => { opens++; return new FakeBackend(device, selection); });
        string Folder(string name) => Path.Combine(root, "real-engineering-" + name + "-" + Guid.NewGuid().ToString("N"));
        var captureRequest = new AdapterRequest(1, "capture", Folder("capture"), model.DeviceId, selection);
        var capture = engine.Run(captureRequest, engineering: true);
        Check(capture.Status == "ok" && device.Writes == 0, "real original captured twice through fake engine");
        string bundleFolder = Folder("bundle"); Directory.CreateDirectory(bundleFolder);
        Files.New(bundleFolder, "full.bin", image, "offline-engineering-image");
        Files.New(bundleFolder, "bundle.json", Json.Bytes(new Bundle(1, model.DeviceId, 16, model.Transport, Model.FlashBase, model.FlashBytes,
            model.SramBytes, new("full.bin", image.Length, Files.Hash(image)), candidate.ReleaseId)), "offline-engineering-bundle");
        var install = new AdapterRequest(1, "install", Folder("install"), model.DeviceId, selection, capture.FingerprintSha256,
            Path.Combine(bundleFolder, "bundle.json"), Path.Combine(capture.EvidenceDirectory, "capture.json"), true, Files.Hash(original), identity.InstallationId);
        EngineeringWriteAuthorization Authorization(AdapterRequest request, byte[] prepared) => new(1, request.Operation, model.DeviceId,
            request.SessionDirectory, device.Fingerprint, capture.FingerprintSha256!, Files.Hash(original), identity.InstallationId,
            Files.Hash(prepared), candidate.Bytes, candidate.Sha256, EngineeringPolicy.ResidentLoaderSha256);
        AdapterResponse Invoke(AdapterRequest request, EngineeringWriteAuthorization authorization)
        {
            string path = Path.Combine(root, "real-authorization-" + Guid.NewGuid().ToString("N") + ".json");
            Files.New(root, Path.GetFileName(path), Json.Bytes(authorization), "offline-scoped-authorization");
            return engine.Run(request, true, path);
        }
        var approved = Authorization(install, image);
        var grant = EngineeringPolicy.Authorize(approved, install, model, device.Fingerprint, original, original, image);
        Reject(() => grant.ValidateTransport(Model.Get("numpad"), device.Fingerprint, image), "real grant rejects another model");
        Reject(() => grant.ValidateTransport(model, device.Fingerprint with { PortPath = "6.8" }, image), "real grant rejects another port");
        byte[] changed = image.ToArray(); changed[0x7010] ^= 1;
        Reject(() => grant.ValidateTransport(model, device.Fingerprint, changed), "real grant rejects changed prepared image");
        byte[] wrongPrefix = original.AsSpan(0, model.ProtectedPrefixBytes).ToArray(); wrongPrefix[^1] ^= 1;
        Reject(() => grant.Consume(model, device.Fingerprint, image, wrongPrefix), "real grant rejects changed live loader");
        grant.Consume(model, device.Fingerprint, image, original.AsSpan(0, model.ProtectedPrefixBytes).ToArray()); checks++;
        Reject(() => grant.Consume(model, device.Fingerprint, image, original.AsSpan(0, model.ProtectedPrefixBytes).ToArray()), "real grant is consumed once");
        foreach (string fault in new[] { "operation", "session", "image", "original", "candidate", "prefix" })
        {
            var request = install with { SessionDirectory = Folder(fault) };
            var authorization = Authorization(request, image);
            authorization = fault switch {
                "operation" => authorization with { Operation = "restore" }, "session" => authorization with { SessionDirectory = install.SessionDirectory },
                "image" => authorization with { PreparedImageSha256 = new string('0', 64) }, "original" => authorization with { OriginalSha256 = new string('0', 64) },
                "candidate" => authorization with { CandidateSha256 = new string('0', 64) }, _ => authorization with { RetainedPrefixSha256 = new string('0', 64) } };
            var rejected = Invoke(request, authorization);
            Check(rejected.Status == "blocked" && !rejected.WriteAttempted && device.Writes == 0, "real scoped " + fault + " mismatch never writes");
        }
        var installed = Invoke(install, approved);
        Check(installed is { Status: "verified", WriteAttempted: true, Verified: true } && device.Writes == 1 && device.Image.AsSpan().SequenceEqual(image), "real approved install through mock engine and bounded packet writer");
        int priorOpens = opens;
        var reused = Invoke(install with { SessionDirectory = Folder("reused") }, approved);
        Check(reused is { Status: "blocked", ErrorCode: "ENGINEERING_AUTHORIZATION_SCOPE_MISMATCH", WriteAttempted: false } && opens == priorOpens && device.Writes == 1, "authorization cannot be replayed into another session");
        var restore = install with { Operation = "restore", SessionDirectory = Folder("restore"), BundlePath = null };
        byte[] installedBytes = device.Image.ToArray();
        device.Image[InstallationIdentity.Offset(model.FlashBytes)] ^= 1;
        var missingMarkerRequest = restore with { SessionDirectory = Folder("missing-marker") };
        var missingMarker = Invoke(missingMarkerRequest, Authorization(missingMarkerRequest, original));
        Check(missingMarker is { Status: "blocked", ErrorCode: "DEVICE_RECOVERY_ID_MISSING", WriteAttempted: false } && device.Writes == 1, "marker-loss emergency restore remains unavailable");
        device.Image = installedBytes;
        var restored = Invoke(restore, Authorization(restore, original));
        Check(restored is { Status: "verified", WriteAttempted: true, Verified: true } && device.Writes == 2 && device.Image.AsSpan().SequenceEqual(original), "real original restored through mock engine without loader writes");
    }
}
