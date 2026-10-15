using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using InGlorious.Core;

internal static partial class Program
{
    private static async Task CheckCoordinator()
    {
        CheckNativeToolLockHandoff();
        Check(!System.Text.RegularExpressions.Regex.IsMatch(WindowsDeviceSetupEnvironment.DiscoveryScript,
            @"\$(?:PID|HOME|PSHOME)\s*=",System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "Discovery never assigns reserved PowerShell process/home variables.");
        await CheckSuccessfulRoundTrips();
        await CheckUpdates();
        await CheckDiscoveryAndTransitions();
        await CheckIdentifiedModelWithoutFirmware();
        await CheckCaptureFailures();
        await CheckWritesAndArchives();
        using var busy = new SetupFixture("numpad");
        var busySession = await busy.ReadyToCapture(); await busy.Service.CaptureAsync(busySession);
        busy.Environment.WriteFault = "device-busy";
        await RejectAsync(() => busy.Service.InstallAsync(busySession, true), "Concurrent adapter operation must refuse installation.");
        Check(busySession.Stage == SetupStage.Blocked && !busySession.OutcomeUncertain && !busySession.WriteAttempted && busy.Environment.Writes == 0,
            "Refusal before USB access is reported as no-write, without an uncertain flash result.");
    }

    private static void CheckNativeToolLockHandoff()
    {
        string root = Path.Combine(Path.GetTempPath(), "inglorious-tool-lock-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "synthetic-native-tool.bin");
        byte[] contents = [1, 3, 5, 7]; File.WriteAllBytes(path, contents);
        string hash = Convert.ToHexString(SHA256.HashData(contents));
        try
        {
            using (var parent = FirmwareAdapterProcess.VerifyToolFile(path, contents.Length, hash, adapterOwnsNativeLock: true))
            {
                Check((parent is null) == !OperatingSystem.IsWindows(), "POSIX gives the native tool lock to the adapter; Windows keeps compatible read protection.");
                using var child = new FileStream(path, FileMode.Open, FileAccess.Read, OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None);
                Check(child.Length == contents.Length, "The adapter can acquire its native tool handle after parent verification.");
            }
            using (var ordinary = FirmwareAdapterProcess.VerifyToolFile(path, contents.Length, hash, adapterOwnsNativeLock: false))
                Check(ordinary is not null && ordinary.CanRead, "Managed adapter/runtime files remain held during execution.");
            Reject(() => FirmwareAdapterProcess.VerifyToolFile(path, contents.Length, new string('0',64), true), "Native tool handoff still rejects altered bytes.");
            Reject(() => FirmwareAdapterProcess.VerifyToolFile(path, contents.Length + 1, hash, true), "Native tool handoff still rejects wrong size.");
            using var afterFailure = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(afterFailure.Length == contents.Length, "A failed tool verification releases its handle without changing the input.");
        }
        finally { File.Delete(path); Directory.Delete(root); }
    }

    private static async Task CheckIdentifiedModelWithoutFirmware()
    {
        using var f = new SetupFixture("numpad");
        var pro = f.Environment.Normal with { InstanceId = "USB\\VID_342D&PID_E3CE\\PRO-FIXTURE", VendorId = 0x342D, ProductId = 0xE3CE };
        f.Environment.Nodes = [pro];
        var archive = Path.Combine(f.Root, "pro-selection-archive");
        var service = new DeviceSetupService(f.Environment, archive, Path.Combine(f.Root, "Firmware"));
        var identified = (await service.ListDevicesAsync()).Single();
        Check(identified.Name == "GMMK 3 PRO 100% ISO" && !identified.IsRuntime && !identified.IsExperimental &&
            identified.CandidateDeviceIds.SequenceEqual([RetainedLoaderLayout.DeviceId]), "Exact stock PRO is a separate hardware-tested model.");
        Check(DeviceSetupService.FlashBytesFor(identified.CandidateDeviceIds[0]) == 262144 && DeviceSetupService.SramBytesFor(identified.CandidateDeviceIds[0]) == 36864 &&
            DeviceSetupService.TransportFor(identified.CandidateDeviceIds[0]) == "wb32-dfu", "PRO memory geometry remains separate from generic100ISO.");
        foreach (var operation in Enum.GetValues<SetupOperation>())
        {
            await RejectAsync(() => service.BeginAsync(identified with { CandidateDeviceIds = ["gmmk3-100-iso"] }, operation), "Forged generic model cannot adopt the PRO USB identity.");
            Check(!Directory.Exists(archive) && f.Environment.Requests.Count == 0 && f.Environment.Writes == 0, "Forged model is rejected before adapter or archive access.");
        }
        foreach (var operation in new[] { SetupOperation.Update, SetupOperation.Restore })
            await RejectAsync(() => service.BeginAsync(identified, operation), "Stock PRO cannot masquerade as installed firmware.");
        var session = await service.BeginAsync(identified);
        Check(session.Stage == SetupStage.Disconnect && f.Environment.Requests.Count == 0 && f.Environment.Writes == 0, "Normal PRO selection starts only the observed disconnect stage.");
        f.Environment.Nodes = [pro, f.Environment.Normal];
        var available = await service.ListDevicesAsync();
        Check(available.Count == 2 && available.Single(device => device.VendorId == 0x320F).CandidateDeviceIds.SequenceEqual(["numpad"]), "PRO candidate does not change Numpad discovery.");
        f.Environment.Nodes = [pro with { InstanceId = "USB\\VID_FEED&PID_1C10\\PRO-FIXTURE", VendorId = 0xFEED, ProductId = 0x1C10 }];
        var runtime = (await service.ListDevicesAsync()).Single();
        Check(runtime.IsRuntime && !runtime.IsExperimental && runtime.CandidateDeviceIds.SequenceEqual([RetainedLoaderLayout.DeviceId]), "Exact tested PRO runtime resolves only board16.");
        await RejectAsync(() => service.BeginAsync(runtime), "Installed PRO cannot use initial installation.");
        foreach (var operation in new[] { SetupOperation.Update, SetupOperation.Restore })
        {
            var prepared = await service.BeginAsync(runtime, operation);
            Check(prepared.Stage == SetupStage.Disconnect && f.Environment.Requests.Count == 0 && f.Environment.Writes == 0, "Runtime PRO update/restore begins with metadata only.");
        }
        foreach (ushort pid in new ushort[] { 0xE3CD, 0xE3CF, 0xE3D0 })
        {
            f.Environment.Nodes = [pro with { InstanceId = $"USB\\VID_342D&PID_{pid:X4}\\UNKNOWN", ProductId = pid }];
            Check((await service.ListDevicesAsync()).All(item => !item.CandidateDeviceIds.Contains(RetainedLoaderLayout.DeviceId)), "Other PRO layouts and wireless variants never alias wired ISO.");
        }
    }
    private static async Task CheckUpdates()
    {
        Check(Version.TryParse(DeviceSetupService.TargetFirmwareVersion, out _), "Update target comes from a valid embedded catalog version.");
        foreach (var model in RoundTripBoards)
        {
            using var f = new SetupFixture(model.DeviceId);
            var original = f.Environment.Current.ToArray();
            var install = await f.ReadyToCapture(); await f.Service.CaptureAsync(install); await f.Service.InstallAsync(install, true);
            var recoveryPath = Path.Combine(install.Directory, "recovery.json");
            var recoveryBefore = File.ReadAllBytes(recoveryPath);
            for (int i = f.Environment.Current.Length - InstallationIdentity.StorageBytes; i < f.Environment.Current.Length; i++)
                f.Environment.Current[i] = (byte)(i * 29 + 101);
            var before = f.Environment.Current.ToArray();
            var identity = InstallationIdentity.Read(before);
            var update = await f.ReadyToCapture(SetupOperation.Update);
            var prepared = await f.Service.CaptureAsync(update);
            Check(prepared.CanUpdate && f.Environment.Writes == 1 && update.InstalledFirmwareVersion == DeviceSetupService.TargetFirmwareVersionFor(model.DeviceId),
                "Known code is identified and prepared without an update write.");
            Check(File.ReadAllBytes(Path.Combine(update.Directory, "installation.bin")).SequenceEqual(before),
                "Prepared update preserves the exact whole configuration and installation identity.");
            Check(!File.Exists(Path.Combine(update.Directory, "recovery.json")), "Update cannot re-label runtime firmware as a new manufacturer original.");
            Check((await f.Service.UpdateAsync(update, true)).Stage == SetupStage.Reconnect && f.Environment.Writes == 2,
                "Explicit update proceeds only with matching prewrite and postwrite evidence.");
            Check(f.Environment.Current.SequenceEqual(before) && InstallationIdentity.Read(f.Environment.Current) == identity &&
                File.ReadAllBytes(recoveryPath).SequenceEqual(recoveryBefore), "Update retains configuration, recovery identity and original archive.");
            f.Environment.Nodes = [f.Environment.Normal];
            Check((await f.Service.ObserveAsync(update)).Stage == SetupStage.Reconnect, "Original firmware is not an update success.");
            f.Environment.Nodes = [f.Environment.Runtime];
            Check((await f.Service.ObserveAsync(update)).Stage == SetupStage.Complete, "Update completes only when runtime returns on its port.");
            var restore = await f.ReadyToCapture(SetupOperation.Restore); await f.Service.CaptureAsync(restore); await f.Service.RestoreAsync(restore, true);
            Check(f.Environment.Current.SequenceEqual(original), "Original restoration still works after a generic update.");
        }
        foreach (var fault in new[] { "unknown-code", "missing-identity", "different-board", "changed-gap", "missing-original" })
        {
            using var f = new SetupFixture("numpad");
            var install = await f.ReadyToCapture(); await f.Service.CaptureAsync(install); await f.Service.InstallAsync(install, true);
            if (fault == "unknown-code") f.Environment.Current[100] ^= 1;
            if (fault == "missing-identity") f.Environment.Current.AsSpan(InstallationIdentity.Offset(f.Environment.Current.Length), 64).Fill(0xFF);
            if (fault == "different-board")
            {
                var identity = InstallationIdentity.Read(f.Environment.Current)! with { BoardId = 4 };
                identity.Encode().CopyTo(f.Environment.Current, InstallationIdentity.Offset(f.Environment.Current.Length));
            }
            if (fault == "changed-gap") f.Environment.Current[InstallationIdentity.Offset(f.Environment.Current.Length) - 1] = 0;
            if (fault == "missing-original") File.Delete(Path.Combine(install.Directory, "recovery.json"));
            var update = await f.ReadyToCapture(SetupOperation.Update);
            await RejectAsync(async () => await f.Service.CaptureAsync(update), "Unsafe update is rejected: " + fault);
            Check(update.Stage == SetupStage.Blocked && f.Environment.Writes == 1, "Unrecognized/unrecoverable runtime never becomes a write target.");
        }
        foreach (var fault in new[] { "missing-prewrite", "duplicate-prewrite", "wrong-prewrite", "wrong-readback", "wrong-operation" })
        {
            using var f = new SetupFixture("numpad");
            var install = await f.ReadyToCapture(); await f.Service.CaptureAsync(install); await f.Service.InstallAsync(install, true);
            var update = await f.ReadyToCapture(SetupOperation.Update); await f.Service.CaptureAsync(update);
            f.Environment.WriteFault = fault;
            await RejectAsync(async () => await f.Service.UpdateAsync(update, true), "Unverified update cannot complete: " + fault);
            Check(update.Stage == SetupStage.Blocked && update.OutcomeUncertain, "Invalid update evidence remains uncertain.");
            await RejectAsync(async () => await f.Service.UpdateAsync(update, true), "Uncertain update cannot be repeated.");
            Check(f.Environment.Writes == 2, "Failed update never automatically retries.");
        }
        using (var f = new SetupFixture("numpad"))
        {
            var normal = (await f.Service.ListDevicesAsync()).Single();
            await RejectAsync(async () => await f.Service.BeginAsync(normal, SetupOperation.Update), "Original firmware must use first installation, never update.");
            Check(f.Environment.Writes == 0, "Update does not touch original firmware during selection.");
        }
        using (var f = new SetupFixture("numpad"))
        {
            var install = await f.ReadyToCapture(); await f.Service.CaptureAsync(install); await f.Service.InstallAsync(install, true);
            var update = await f.ReadyToCapture(SetupOperation.Update); await f.Service.CaptureAsync(update);
            f.Environment.WriteFault = "blocked-prewrite";
            await RejectAsync(async () => await f.Service.UpdateAsync(update, true), "Adapter prewrite rejection stops update.");
            Check(!update.WriteAttempted && !update.OutcomeUncertain && f.Environment.Writes == 1,
                "Verified prewrite rejection is distinguished from an uncertain partial write.");
            await RejectAsync(async () => await f.Service.UpdateAsync(update, true), "Rejected update still requires a fresh session, never automatic retry.");
        }
    }

    private static async Task CheckSuccessfulRoundTrips()
    {
        foreach (var model in RoundTripBoards)
        {
            using var f = new SetupFixture(model.DeviceId);
            var original = (byte[])f.Environment.Current.Clone();
            var session = await f.ReadyToCapture();
            Check(session.DeviceId == model.DeviceId, "Boot transport resolves the correct selected layout/revision.");
            var capture = await f.Service.CaptureAsync(session);
            Check(capture.CanInstall && f.Environment.Writes == 0, "Verified backup alone never writes firmware.");
            Check(File.Exists(Path.Combine(session.Directory,"recovery.json")), "Durable recovery association exists before approval.");
            var result = await f.Service.InstallAsync(session, true);
            Check(result.Stage == SetupStage.Reconnect && f.Environment.Writes == 1, "Confirmed install reaches reconnect after readback.");
            var identity = InstallationIdentity.Read(f.Environment.Current);
            Check(identity is not null && identity.BoardId == model.BoardId && identity.MatchesOriginal(original), "Installed identity binds original bytes and board.");
            f.Environment.Nodes = [f.Environment.Runtime with { Service="Unavailable" }];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Reconnect,"A normal USB node with failed PnP status cannot complete setup.");
            f.Environment.Nodes = [f.Environment.Runtime with { LocationPaths=[SetupFixture.OtherControllerPort] }];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Reconnect,"A matching runtime on another controller is not the expected return.");
            f.Environment.Nodes = [f.Environment.Runtime,f.Environment.Runtime with {InstanceId=f.Environment.Runtime.InstanceId+"-duplicate"}];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Reconnect,"Ambiguous runtime return does not complete setup.");
            f.Environment.Nodes = [f.Environment.Runtime];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Complete, "Only normal runtime return completes installation.");
            var restore = await f.ReadyToCapture(SetupOperation.Restore);
            Check((await f.Service.CaptureAsync(restore)).CanRestore && f.Environment.Writes == 1, "Restore captures current state before selecting own original.");
            Check((await f.Service.RestoreAsync(restore,true)).Stage == SetupStage.Reconnect, "Original restore is separately confirmed.");
            Check(f.Environment.Current.SequenceEqual(original), "Complete original image is restored byte exactly.");
            f.Environment.Nodes = [f.Environment.Normal];
            Check((await f.Service.ObserveAsync(restore)).Stage == SetupStage.Complete, "Original normal-mode identity completes restore.");
            Check(f.Environment.Requests.Where(x => x.GetProperty("operation").GetString() is "capture" or "probe" or "list")
                .All(x => !x.GetProperty("confirmWrite").GetBoolean()), "All detection and capture requests remain explicitly read-only.");
        }
    }

    private static async Task CheckDiscoveryAndTransitions()
    {
        using (var f = new SetupFixture("numpad"))
        {
            f.Environment.Nodes = [f.Environment.Normal, new("USB\\VID_342D&PID_E3DD\\foreign",0x342D,0xE3DD,"HE","usbccgp",[SetupFixture.Port])];
            Check((await f.Service.ListDevicesAsync()).Count == 1, "Unsupported HE cannot masquerade as supported wired stock.");
            var chosen = (await f.Service.ListDevicesAsync()).Single();
            var forged = chosen with { VendorId = 0x1111, ProductId = 0x2222, IsRuntime = true };
            var session = await f.Service.BeginAsync(forged);
            Check(session.Device.VendorId == chosen.VendorId && session.Device.ProductId == chosen.ProductId && !session.Device.IsRuntime,
                "Fresh discovery replaces caller-supplied mutable identification fields.");
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Disconnect, "Connected device cannot skip cable removal.");
            f.Environment.Nodes = [f.Environment.Boot];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Disconnect && f.Environment.Requests.Count == 0,
                "Normal-to-boot without an observed empty interval cannot start capture.");
            f.Environment.Nodes = [];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.ConnectBootloader, "Actual observed absence unlocks reconnect step.");
            f.Environment.Nodes = [f.Environment.Boot, f.Environment.Boot with { InstanceId = "USB\\VID_342D&PID_DFA0\\second" }];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.ConnectBootloader && f.Environment.Requests.Count == 0,
                "Two bootloaders cannot select the first one.");
            f.Environment.Nodes = [f.Environment.Boot with { LocationPaths = [SetupFixture.OtherControllerPort] }];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.ConnectBootloader && f.Environment.Requests.Count == 0,
                "Same USB hop numbers on another controller are not the selected physical port.");
            f.Environment.Nodes = [f.Environment.Boot with { Service = "unrecognized" }];
            Check(!(await f.Service.ObserveAsync(session)).CanCapture && f.Environment.Requests.Count == 0, "Wrong boot driver never starts adapter I/O.");
            f.Environment.Nodes = [f.Environment.Boot];
            Check((await f.Service.ObserveAsync(session)).CanCapture, "Unique matching bootloader can be probed.");
            f.Environment.Nodes = [f.Environment.Boot with { InstanceId = "USB\\VID_342D&PID_DFA0\\replacement" }];
            Check((await f.Service.ObserveAsync(session)).Stage == SetupStage.Blocked, "Replacing a selected bootloader invalidates the session.");
        }
        foreach (var core in new bool?[] { true, null })
        {
            using var f = new SetupFixture("numpad");
            f.Environment.CoreRunning = core;
            await RejectAsync(async () => await f.Service.BeginAsync((await f.Service.ListDevicesAsync()).Single()), "CORE running/unknown blocks setup.");
            Check(f.Environment.Writes == 0 && f.Environment.Requests.Count == 0, "CORE guard precedes all adapter calls.");
        }
        using (var f = new SetupFixture("numpad"))
        {
            var chosen = (await f.Service.ListDevicesAsync()).Single();
            var old = await f.Service.BeginAsync(chosen);
            var fresh = await f.Service.BeginAsync(chosen);
            await RejectAsync(async () => await f.Service.ObserveAsync(old), "A new selected session invalidates previous async/UI handles.");
            Check(fresh.Stage == SetupStage.Disconnect && f.Environment.Requests.Count == 0, "New session needs its own cable transition.");
        }
        using (var f = new SetupFixture("pro-ansi"))
        {
            var session = await f.ReadyToCapture();
            Check(session.DeviceId == "pro-ansi", "STM32 resolves original PRO ANSI among identical normal IDs.");
        }
        using (var f = new SetupFixture("pro-rev2-ansi", forceSharedProPid: true))
        {
            var session = await f.ReadyToCapture();
            Check(session.DeviceId == "pro-rev2-ansi", "WB32 resolves second PRO ANSI revision without a layout default.");
        }
        using (var f = new SetupFixture("numpad"))
        {
            var selected = (await f.Service.ListDevicesAsync()).Single(); var s = await f.Service.BeginAsync(selected);
            f.Environment.ThrowScan = true;
            await RejectAsync(async () => await f.Service.ObserveAsync(s), "A scan failure is not cable removal.");
            Check(s.Stage == SetupStage.Disconnect && f.Environment.Writes == 0, "Failed snapshot preserves required disconnect step.");
        }
        foreach (var fault in new[] { "wrong-flash", "wrong-transport", "wrong-sram", "wrong-model", "cannot-capture" })
        {
            using var f = new SetupFixture("numpad"); f.Environment.ProbeFault = fault;
            var s = await f.BeginAndDisconnect(); f.Environment.Nodes = [f.Environment.Boot];
            try { await f.Service.ObserveAsync(s); } catch (IOException) { }
            Check(s.Stage != SetupStage.ReadyToBackup && f.Environment.Writes == 0, "Invalid model/MCU/geometry probe cannot authorize capture: " + fault);
        }
    }

    private static async Task CheckCaptureFailures()
    {
        foreach (var fault in new[] { "mismatch", "missing", "duplicate-path", "wrong-hash", "short", "traversal", "outside", "conflicting-model", "missing-stock", "wrong-identity", "wrong-operation", "throw" })
        {
            using var f = new SetupFixture("numpad"); var session = await f.ReadyToCapture(); f.Environment.CaptureFault = fault;
            await RejectAsync(async () => await f.Service.CaptureAsync(session), "Failed/incomplete backup stops install: " + fault);
            Check(session.Stage == SetupStage.Blocked && f.Environment.Writes == 0, "Backup failure cannot reach a write request.");
        }
        using (var f = new SetupFixture("numpad"))
        {
            var session = await f.ReadyToCapture(); f.Environment.CoreRunning = true;
            await RejectAsync(async () => await f.Service.CaptureAsync(session), "CORE starting before capture blocks it.");
            Check(f.Environment.Requests.All(x => x.GetProperty("operation").GetString() != "capture"), "Capture rechecks CORE before invoking adapter.");
        }
    }

    private static async Task CheckWritesAndArchives()
    {
        using (var f = new SetupFixture("numpad"))
        {
            var session = await f.ReadyToCapture(); await f.Service.CaptureAsync(session);
            await RejectAsync(async () => await f.Service.InstallAsync(session,false), "Explicit user confirmation is mandatory.");
            Check(f.Environment.Writes == 0, "Declined confirmation never reaches adapter write.");
        }
        using (var f = new SetupFixture("numpad"))
        {
            var session = await f.ReadyToCapture(); await f.Service.CaptureAsync(session);
            var first = f.Service.InstallAsync(session,true); var second = f.Service.InstallAsync(session,true);
            await first; await RejectAsync(async () => await second, "Double-click cannot repeat installation.");
            Check(f.Environment.Writes == 1, "Serialized write is consumed exactly once.");
        }
        foreach (var fault in new[] { "throw", "unverified", "missing-readbacks", "duplicate-path", "wrong-readback", "wrong-identity", "wrong-operation", "wrong-installation" })
        {
            using var f = new SetupFixture("numpad"); var session = await f.ReadyToCapture(); await f.Service.CaptureAsync(session);
            f.Environment.WriteFault = fault;
            await RejectAsync(async () => await f.Service.InstallAsync(session,true), "Unproven write cannot report success: " + fault);
            Check(session.Stage == SetupStage.Blocked && f.Environment.Writes == 1, "Uncertain result is consumed and blocked.");
            await RejectAsync(async () => await f.Service.InstallAsync(session,true), "Uncertain write is never retried automatically.");
            Check(f.Environment.Writes == 1, "Retry guard holds after errors.");
        }
        foreach (var fault in new[] { "missing", "duplicate", "traversal", "changed-hash", "wrong-board" })
        {
            using var f = new SetupFixture("numpad"); var installed = await f.ReadyToCapture(); await f.Service.CaptureAsync(installed); await f.Service.InstallAsync(installed,true);
            var recordPath = Path.Combine(installed.Directory,"recovery.json");
            if (fault == "missing") File.Delete(recordPath);
            if (fault == "duplicate") { var dir=Path.Combine(f.Archive,"duplicate"); Directory.CreateDirectory(dir); File.Copy(recordPath,Path.Combine(dir,"recovery.json")); }
            if (fault is "traversal" or "changed-hash" or "wrong-board")
            {
                var node = JsonNode.Parse(File.ReadAllText(recordPath))!;
                if (fault == "traversal") node["backupManifest"] = "../foreign/capture.json";
                if (fault == "changed-hash") node["backupManifestSha256"] = new string('0',64);
                if (fault == "wrong-board") node["deviceId"] = "pro-iso";
                File.WriteAllText(recordPath,node.ToJsonString());
            }
            f.Environment.Nodes = [f.Environment.Runtime]; var restore = await f.ReadyToCapture(SetupOperation.Restore);
            await RejectAsync(async () => await f.Service.CaptureAsync(restore), "Restore needs exactly one intact own archive: " + fault);
            Check(f.Environment.Writes == 1 && restore.Stage == SetupStage.Blocked, "Invalid recovery metadata cannot trigger restore.");
        }
        using (var f = new SetupFixture("numpad"))
        {
            var outside=Path.Combine(f.Root,"outside");Directory.CreateDirectory(outside);
            var link=Path.Combine(f.Root,"linked");
            CreateFixtureJunction(link,outside,f.Root);
            Reject(() => DeviceSetupService.ContainedFile(link,"capture.json"), "Linked archive roots are rejected.");
            Reject(() => DeviceSetupService.ContainedFile(f.Archive,"../outside/capture.json"), "Parent traversal is rejected.");
            Reject(() => DeviceSetupService.ContainedFile(f.Archive,"capture.json:stream"), "NTFS alternate streams are rejected.");
        }
    }

    private static void CreateFixtureJunction(string link,string target,string fixtureRoot)
    {
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureRoot))+Path.DirectorySeparatorChar;
        if(new[]{link,target}.Any(path=>!Path.GetFullPath(path).StartsWith(root,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Junction fixture must remain within its isolated root.");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            if (!File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Symbolic-link fixture failed.");
            return;
        }
        // NTFS directory junctions exercise the same ReparsePoint rejection without
        // requiring Windows Developer Mode or the symbolic-link privilege.
        var start=new ProcessStartInfo(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),"cmd.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"/d","/c","mklink","/J",link,target})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Could not create isolated junction fixture.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(10000)){process.Kill(true);process.WaitForExit();throw new IOException("Junction fixture timed out.");}
        Task.WaitAll(stdout,stderr);
        if(process.ExitCode!=0 || !File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Junction fixture failed: "+stderr.Result);
    }

    private sealed class SetupFixture : IDisposable
    {
        internal const string Port = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)#USB(2)";
        internal const string OtherControllerPort = "PCIROOT(0)#PCI(1500)#USBROOT(0)#USB(1)#USB(2)";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),"inglorious-setup-checks-"+Guid.NewGuid().ToString("N"));
        internal string Archive => Path.Combine(Root,"archives");
        internal FakeSetupEnvironment Environment { get; }
        internal DeviceSetupService Service { get; }
        internal SetupFixture(string id,bool forceSharedProPid=false)
        {
            Directory.CreateDirectory(Root);Directory.CreateDirectory(Archive);
            var packages=Path.Combine(Root,"Firmware");Directory.CreateDirectory(packages);
            var repo=FindRepo();
            var firmwareDirectory = System.Environment.GetEnvironmentVariable("INGLORIOUS_TEST_FIRMWARE_DIRECTORY")
                ?? Path.Combine(repo,"firmware","build");
            foreach(var file in Directory.EnumerateFiles(firmwareDirectory,"*.bin"))
                File.Copy(file,Path.Combine(packages,Path.GetFileName(file)));
            Environment=new(id,forceSharedProPid);
            Service=new(Environment,Archive,packages);
        }
        internal async Task<SetupSession> BeginAndDisconnect(SetupOperation operation=SetupOperation.Install)
        {
            Environment.Nodes=[operation==SetupOperation.Install?Environment.Normal:Environment.Runtime];
            var selected=(await Service.ListDevicesAsync()).Single();
            var session=await Service.BeginAsync(selected,operation);Environment.Nodes=[];
            Check((await Service.ObserveAsync(session)).Stage==SetupStage.ConnectBootloader,"Each operation observes an unplugged interval.");
            return session;
        }
        internal async Task<SetupSession> ReadyToCapture(SetupOperation operation=SetupOperation.Install)
        {
            var session=await BeginAndDisconnect(operation);Environment.Nodes=[Environment.Boot];
            Check((await Service.ObserveAsync(session)).CanCapture,"Valid unique bootloader reaches backup step.");return session;
        }
        public void Dispose()
        {
            var fullRoot=Path.GetFullPath(Root);
            var tempPrefix=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
            if(!fullRoot.StartsWith(tempPrefix,StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("inglorious-setup-checks-",StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the isolated fixture directory.");
            // Remove links themselves before recursive fixture cleanup; never follow targets.
            foreach(var child in Directory.EnumerateDirectories(Root))
                if(File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(child);
            Directory.Delete(Root,true);
        }
        private static string FindRepo()
        {
            for(var p=new DirectoryInfo(AppContext.BaseDirectory);p is not null;p=p.Parent)
                if(File.Exists(Path.Combine(p.FullName,"src","InGlorious.Core","SetupFirmwareCatalog.json")))return p.FullName;
            throw new InvalidOperationException("Run this fixture suite from the repository build output.");
        }
    }

    private sealed class FakeSetupEnvironment : IDeviceSetupEnvironment
    {
        internal string DeviceId { get; }
        internal SetupUsbNode Normal { get; }
        internal SetupUsbNode Runtime { get; }
        internal SetupUsbNode Boot { get; }
        internal IReadOnlyList<SetupUsbNode> Nodes { get; set; }
        internal byte[] Current { get; set; }
        internal bool? CoreRunning { get; set; }=false;
        internal bool ThrowScan;
        internal string? CaptureFault,WriteFault,ProbeFault;
        internal int Writes;
        internal List<JsonElement> Requests { get; }=[];
        private readonly bool _stm;
        private readonly int _size;
        private readonly JsonElement _selection,_fingerprint;
        private readonly string _fingerprintHash;
        internal FakeSetupEnvironment(string id,bool shared)
        {
            DeviceId=id;_stm=id is "pro-iso" or "pro-ansi";_size=DeviceSetupService.FlashBytesFor(id);
            var match=StockDeviceCatalog.All.First(x=>x.CandidateDeviceIds.Contains(id) && (shared || !id.StartsWith("pro-rev2") || x.IsModelUnambiguous));
            Normal=new($"USB\\VID_{match.VendorId:X4}&PID_{match.ProductId:X4}\\FAKE",match.VendorId,match.ProductId,"Fixture stock","usbccgp",[SetupFixture.Port]);
            var board=BoardDescriptorRegistry.Get(id);
            Runtime=new($"USB\\VID_FEED&PID_{board.RuntimeProductId:X4}\\FAKE",0xFEED,board.RuntimeProductId,"Fixture runtime","usbccgp",[SetupFixture.Port]);
            Boot=new(_stm?"USB\\VID_0483&PID_DF11\\FAKE":"USB\\VID_342D&PID_DFA0\\FAKE",(ushort)(_stm?0x0483:0x342D),(ushort)(_stm?0xDF11:0xDFA0),"Fixture boot","WinUSB",[SetupFixture.Port]);
            Nodes=[Normal];Current=Enumerable.Repeat((byte)0xFF,_size).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(Current,0x20001000);BinaryPrimitives.WriteUInt32LittleEndian(Current.AsSpan(4),0x08000101);
            UsbDescriptor(match.VendorId,match.ProductId).CopyTo(Current,113);
            if (id == RetainedLoaderLayout.DeviceId && RetainedOriginalPair() is { } retained) Current = retained;
            _selection=JsonSerializer.SerializeToElement(new{vid=Boot.VendorId,pid=Boot.ProductId,bus=1,portPath="1.2",serial=_stm?"0123456789AB":"",transport=_stm?"stm32-dfuse":"wb32-dfu"});
            _fingerprint=JsonSerializer.SerializeToElement(new{transport=_stm?"stm32-dfuse":"wb32-dfu",vid=Boot.VendorId,pid=Boot.ProductId,bus=1,portPath="1.2",serial=_stm?"0123456789AB":"",configuration=1,@interface=0,alternate=0,deviceVersion=0x0100,chipId=_stm?(uint?)null:0x3A502980,bootVersion=_stm?(byte?)null:id == RetainedLoaderLayout.DeviceId ? (byte)4 : (byte)3,flashBase=0x08000000,flashBytes=_size,sramBytes=DeviceSetupService.SramBytesFor(id),memoryMap=_stm?"@Internal Flash /0x08000000/128*0002Kg":null,uniqueId=(string?)null,identityStrength=_stm?"usb-serial-and-port":"transport-session"});
            _fingerprintHash=Hash(JsonSerializer.SerializeToUtf8Bytes(_fingerprint,new JsonSerializerOptions{WriteIndented=true}));
        }
        public Task<IReadOnlyList<SetupUsbNode>> ScanAsync()=>ThrowScan?Task.FromException<IReadOnlyList<SetupUsbNode>>(new IOException("fake scan failure")):Task.FromResult(Nodes);
        public Task<bool?> IsCoreRunningAsync()=>Task.FromResult(CoreRunning);
        public Task<JsonElement> InvokeAdapterAsync(IReadOnlyDictionary<string,object?> request,string evidenceDirectory)
        {
            var req=JsonSerializer.SerializeToElement(request);Requests.Add(req);
            var op=req.GetProperty("operation").GetString()!;
            var path=req.GetProperty("sessionDirectory").GetString()!;
            Directory.CreateDirectory(path);
            var response=new Dictionary<string,object?> { ["schema"]=1,["operation"]=op,["sessionId"]=Guid.NewGuid().ToString("N"),["status"]="ok",["errorCode"]=null,["deviceId"]=DeviceId,["fingerprint"]=_fingerprint,["fingerprintSha256"]=_fingerprintHash,["canCapture"]=true,["canInstall"]=true,["canRestore"]=true,["writeAttempted"]=false,["verified"]=false,["evidenceDirectory"]=path,["artifacts"]=Array.Empty<object>(),["error"]=null,["compatibleDeviceIds"]=new[]{DeviceId} };
            if(op=="list")response["devices"]=new[]{_selection};
            else if(op=="probe")
            {
                var fp=JsonNode.Parse(_fingerprint.GetRawText())!;
                if(ProbeFault=="wrong-flash")fp["flashBytes"]=65536;
                if(ProbeFault=="wrong-sram")fp["sramBytes"]=4096;
                if(ProbeFault=="wrong-transport")fp["transport"]="stm32-dfuse";
                if(ProbeFault=="wrong-model")response["deviceId"]="pro-iso";
                if(ProbeFault=="cannot-capture")response["canCapture"]=false;
                response["fingerprint"]=fp;
            }
            else if(op=="capture")
            {
                if(CaptureFault=="throw")throw new IOException("fake capture failure");
                if(CaptureFault=="outside") {path=Path.Combine(Path.GetDirectoryName(evidenceDirectory)!,"outside");Directory.CreateDirectory(path);response["evidenceDirectory"]=path;}
                var first=(byte[])Current.Clone();var second=(byte[])Current.Clone();
                if(CaptureFault=="mismatch")second[^1]^=1;
                if(CaptureFault=="short")first=first[..^1];
                if(CaptureFault=="conflicting-model") {UsbDescriptor(0x342D,0xE3D3).CopyTo(first,211);first.CopyTo(second,0);}
                if(CaptureFault=="missing-stock") {first.AsSpan(113,18).Clear();first.CopyTo(second,0);}
                var items=new List<Dictionary<string,object?>> {Artifact(path,"original-1.bin",first,"original-read"),Artifact(path,"original-2.bin",second,"original-read")};
                if(CaptureFault=="wrong-hash")items[0]["sha256"]=new string('0',64);
                if(CaptureFault=="missing")items.RemoveAt(1);
                if(CaptureFault=="duplicate-path")items[1]["path"]="original-1.bin";
                if(CaptureFault=="traversal")items[0]["path"]="../original-1.bin";
                if(CaptureFault=="wrong-identity")response["fingerprintSha256"]=new string('0',64);
                if(CaptureFault=="wrong-operation")response["operation"]="probe";
                var capture=new{schema=1,deviceId=DeviceId,fingerprint=_fingerprint,fingerprintSha256=_fingerprintHash,artifacts=items.ToArray(),scope="Synthetic internal flash only",excludedRegions=new[]{"external SPI","option bytes"},restoreTested=false};
                File.WriteAllBytes(Path.Combine(path,"capture.json"),JsonSerializer.SerializeToUtf8Bytes(capture));
                response["artifacts"]=items;
            }
            else if(op is "install" or "restore" or "update")
            {
                if (WriteFault == "device-busy")
                {
                    response["status"] = "blocked"; response["errorCode"] = "DEVICE_OPERATION_BUSY";
                    response["fingerprint"] = null; response["fingerprintSha256"] = null;
                    return Task.FromResult(JsonSerializer.SerializeToElement(response));
                }
                if (WriteFault == "blocked-prewrite")
                {
                    response["status"] = "blocked"; response["errorCode"] = "CURRENT_CHANGED_SINCE_CAPTURE";
                    return Task.FromResult(JsonSerializer.SerializeToElement(response));
                }
                Check(req.GetProperty("confirmWrite").GetBoolean(),"Fake boundary rejects implicit writes.");Writes++;
                response["installationId"]=req.GetProperty("expectedInstallationId").GetString();
                if(WriteFault=="throw")throw new IOException("fake uncertain write");
                byte[] target;
                if(op is "install" or "update")
                {
                    var bundlePath=req.GetProperty("bundlePath").GetString()!;
                    using var bundle=JsonDocument.Parse(File.ReadAllBytes(bundlePath));
                    target=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(bundlePath)!,bundle.RootElement.GetProperty("image").GetProperty("path").GetString()!));
                }
                else
                {
                    var backupPath=req.GetProperty("backupPath").GetString()!;
                    using var capture=JsonDocument.Parse(File.ReadAllBytes(backupPath));
                    target=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(backupPath)!,capture.RootElement.GetProperty("artifacts")[0].GetProperty("path").GetString()!));
                }
                var items=new List<Dictionary<string,object?>> {Artifact(path,"before-1.bin",Current,"pre-write-read"),Artifact(path,"before-2.bin",Current,"pre-write-read")};
                if (op == "update") Check(req.GetProperty("expectedCurrentSha256").GetString() == Hash(Current), "Update forwards exact captured full-image binding.");
                if (WriteFault == "missing-prewrite") items.Clear();
                if (WriteFault == "duplicate-prewrite") items[1]["path"] = "before-1.bin";
                if (WriteFault == "wrong-prewrite")
                {
                    var changed = Current.ToArray(); changed[^1] ^= 1;
                    items[0] = Artifact(path, "before-1.bin", changed, "pre-write-read");
                    items[1] = Artifact(path, "before-2.bin", changed, "pre-write-read");
                }
                Current=(byte[])target.Clone();
                if(WriteFault=="wrong-readback")target[^1]^=1;
                if(WriteFault!="missing-readbacks") {items.Add(Artifact(path,"after-1.bin",target,"verified-read"));items.Add(Artifact(path,"after-2.bin",target,"verified-read"));}
                if(WriteFault=="duplicate-path")items[^1]["path"]="after-1.bin";
                if(WriteFault=="wrong-identity")response["fingerprintSha256"]=new string('0',64);
                if(WriteFault=="wrong-operation")response["operation"]="capture";
                if(WriteFault=="wrong-installation")response["installationId"]=Guid.NewGuid();
                response["artifacts"]=items;response["verified"]=WriteFault!="unverified";response["writeAttempted"]=true;response["status"]=WriteFault=="unverified"?"error":"verified";
            }
            else throw new InvalidOperationException("Unexpected fake adapter operation.");
            return Task.FromResult(JsonSerializer.SerializeToElement(response));
        }
        private static Dictionary<string,object?> Artifact(string directory,string name,byte[] bytes,string kind)
        {File.WriteAllBytes(Path.Combine(directory,name),bytes);return new(){["path"]=name,["bytes"]=bytes.Length,["sha256"]=Hash(bytes),["kind"]=kind};}
        private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    }
}
