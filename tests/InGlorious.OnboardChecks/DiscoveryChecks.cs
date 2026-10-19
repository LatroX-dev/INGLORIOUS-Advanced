using System.Buffers.Binary;
using System.ComponentModel;
using System.Text.Json;
using InGlorious.Core;

static class DiscoveryChecks
{
    const string Id = "gmmk3-pro-100-iso";
    static readonly Guid Installation = new("e266f47d-ec66-4319-bacb-066efbe38c61");
    static readonly OnboardDevice Endpoint = new(Id, "Fixture", "private-endpoint-path") { UsbInstanceId = "private-usb-serial" };

    public static async Task Run(Action<bool, string> check)
    {
        // Exercise the real client parser with report-level failures, rather than mocking an already parsed discovery result.
        for (int failedRequest = 1; failedRequest <= 6; failedRequest++)
        {
            var fixture = new Fixture { FailRequest = failedRequest, Failure = () => new OperationCanceledException("private timeout text") };
            var result = await fixture.Run();
            check(!result.Failed && result.Targets.Count == 1 && fixture.Opens == 2 && fixture.Discoveries == 4 && fixture.Pauses == 1,
                "Discovery: transient read failure " + failedRequest + " is recovered with one fresh enumeration and transport");
            check(fixture.Transports.All(t => t.Disposals == 1) && fixture.Log.Count == 1 && fixture.Log[0].Failure == OnboardDiscoveryFailure.Timeout,
                "Discovery: failed read transport is disposed before retry and stage is recorded");
            check(fixture.Transports.SelectMany(t => t.Commands).All(c => c is 1 or 11 or 18 or 24),
                "Discovery: retries contain only information, capability, profile-status and identity reads");
            check(fixture.Log[0].Stage == failedRequest switch
            {
                1 => OnboardDiscoveryStage.Info, 2 or 3 => OnboardDiscoveryStage.Extensions,
                4 or 5 => OnboardDiscoveryStage.ProfileStatus, _ => OnboardDiscoveryStage.InstallationIdentity
            }, "Discovery: failure stage identifies the actual failed read");
        }

        foreach (var error in new Exception[] { new Win32Exception(170, "private text"), new IOException("private text", unchecked((int)0x8007048f)), new OnboardTransportResetException() })
        {
            var fixture = new Fixture { OpenFailure = error };
            var result = await fixture.Run();
            check(!result.Failed && fixture.Opens == 2 && fixture.Pauses == 1 && fixture.Log[0].Stage == OnboardDiscoveryStage.Open,
                "Discovery: explicitly classified native/open failure receives one fresh attempt");
        }
        foreach (var error in new Exception[] { new InvalidDataException("private malformed report"), new IOException("private unknown failure"),
            new UnauthorizedAccessException("private path"), new Win32Exception(5, "private denial"), new InvalidOperationException("private programming fault"),
            new OnboardCommandException(7, "private storage failure"), new OnboardCommandException(8, "private changed configuration") })
        {
            var fixture = new Fixture { FailRequest = 1, Failure = () => error };
            var result = await fixture.Run();
            check(result.Failed && result.Targets.Count == 0 && fixture.Opens == 1 && fixture.Pauses == 0,
                "Discovery: invalid, unknown, storage, identity-precondition and programming failures are never retried");
            var log = OnboardDiscoveryDiagnostics.Format(fixture.Log.Single());
            check(!log.Contains("private", StringComparison.OrdinalIgnoreCase), "Discovery diagnostics never persist exception text or device identifiers");
        }
        var busy = new Fixture { FailRequest = 1, Failure = () => new OnboardCommandException(3, "busy") };
        check(!(await busy.Run()).Failed && busy.Opens == 2, "Discovery: explicit device-busy response is read again without any write");
        var persistent = new Fixture { FailRequest = 1, Failure = () => new OperationCanceledException(), FailEveryOpen = true };
        check((await persistent.Run()).Failed && persistent.Opens == 2 && persistent.Pauses == 1 && persistent.Log.Count == 2,
            "Discovery: persistent transient failure stops after two attempts");

        var malformedAndClose = new Fixture { FailRequest = 1, Failure = () => new InvalidDataException("invalid"), DisposeFailure = new Win32Exception(170) };
        check((await malformedAndClose.Run()).Failed && malformedAndClose.Opens == 1 && malformedAndClose.Log[0].Failure == OnboardDiscoveryFailure.InvalidReply,
            "Discovery: a transient disposal fault cannot mask malformed data and trigger a retry");
        var wrongReply = new Fixture { MalformedResponse = true };
        check((await wrongReply.Run()).Failed && wrongReply.Opens == 1 && wrongReply.Log[0].Failure == OnboardDiscoveryFailure.InvalidReply,
            "Discovery: malformed wire reply is rejected by the real client without retry");

        var bound = new OnboardProfileBinding(Id, Installation, 2, 5, Probe.Image.Crc32);
        var wrongInstallation = new Fixture { Identity = Guid.Parse("a366f47d-ec66-4319-bacb-066efbe38c61") };
        check((await wrongInstallation.Run(bound)).Targets.Count == 0 && wrongInstallation.Opens == 1 && wrongInstallation.Pauses == 0,
            "Discovery: a different installation never replaces the editor's bound keyboard");
        var stale = new Fixture();
        var staleResult = await stale.Run(bound with { Generation = 4 });
        check(staleResult.Targets.Single().ProfileBinding == bound && staleResult.Targets.Single().ProfileBinding != bound with { Generation = 4 },
            "Discovery: actual generation is retained so the transfer window still rejects a stale editor binding");
        var changedEndpoint = new Fixture { FailRequest = 1, Failure = () => new OperationCanceledException(),
            Enumerate = count => count >= 3 ? [Endpoint with { Path = "replacement" }] : [Endpoint] };
        check((await changedEndpoint.Run(bound)).Targets.Count == 0 && changedEndpoint.Opens == 1 && changedEndpoint.Log.Last().Failure == OnboardDiscoveryFailure.IdentityChanged,
            "Discovery: retry never opens a replacement endpoint after hotplug");
        var changedAfterRead = new Fixture { Enumerate = count => count == 1 ? [Endpoint] : [] };
        check((await changedAfterRead.Run()).Targets.Count == 0 && changedAfterRead.Opens == 1 && changedAfterRead.Pauses == 0,
            "Discovery: endpoint lost after final read is not returned as a ready target");
        var multiple = new Fixture { Enumerate = _ => [Endpoint, Endpoint with { Path = "second" }] };
        check((await multiple.Run()).Targets.Count == 2 && multiple.Opens == 2 && multiple.Pauses == 0,
            "Discovery: two valid endpoints remain two targets requiring explicit user choice");
        var partial = new Fixture { Enumerate = _ => [Endpoint, Endpoint with { Path = "second" }], FailRequest = 1, Failure = () => new InvalidDataException("bad") };
        var partialResult = await partial.Run();
        check(partialResult.Failed && partialResult.Targets.Count == 0 && partial.Opens == 2 && partial.Pauses == 0,
            "Discovery: permanent error in one endpoint cannot silently select the other keyboard");
        var partialTransient = new Fixture { Enumerate = _ => [Endpoint, Endpoint with { Path = "second" }], FailRequest = 1,
            FailEveryOpen = true, FailOnlyPath = Endpoint.Path, Failure = () => new OperationCanceledException() };
        check((await partialTransient.Run()).Targets.Count == 0 && partialTransient.Opens == 4 && partialTransient.Pauses == 1,
            "Discovery: exhausted transient errors in a multi-device scan expose no write target");

        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = new Fixture { FailRequest = 1, Failure = () => { cancel.Cancel(); return new OperationCanceledException(cancel.Token); } };
            try { await cancelled.Run(cancellationToken: cancel.Token); throw new Exception("Caller cancellation ignored"); }
            catch (OperationCanceledException) { check(cancelled.Opens == 1 && cancelled.Pauses == 0 && cancelled.Log.Count == 0,
                "Discovery: caller cancellation is propagated without retry or failure logging"); }
        }
        var enumeration = new Fixture { Enumerate = count => count == 1 ? throw new Win32Exception(170) : [Endpoint] };
        check(!(await enumeration.Run()).Failed && enumeration.Opens == 1 && enumeration.Log[0].Stage == OnboardDiscoveryStage.Enumeration,
            "Discovery: transient enumeration failure can recover before opening any endpoint");
        var noDevices = new Fixture { Enumerate = _ => [] };
        check(!(await noDevices.Run()).Failed && noDevices.Opens == 0 && noDevices.Pauses == 0,
            "Discovery: an absent device is not an error and does not trigger retries");
        var noStorage = new Fixture { StorageHealthy = false };
        check((await noStorage.Run()).Targets.Single().ProfileStatus?.StorageHealthy == false && noStorage.Opens == 1 && noStorage.Pauses == 0,
            "Discovery: unhealthy storage remains visible and blocked by the existing transfer guard without retry");
        var permanentThenEnumeration = new Fixture { FailRequest = 1, Failure = () => new InvalidDataException("permanent"),
            Enumerate = count => count == 2 ? throw new Win32Exception(170) : [Endpoint] };
        check((await permanentThenEnumeration.Run()).Failed && permanentThenEnumeration.Opens == 1 && permanentThenEnumeration.Pauses == 0,
            "Discovery: a transient final enumeration cannot override an earlier permanent probe rejection");
        await CheckDiagnostics(check);
    }

    private static async Task CheckDiagnostics(Action<bool, string> check)
    {
        var entry = new OnboardDiscoveryDiagnostic(Id, OnboardDiscoveryStage.Info, 1, OnboardDiscoveryFailure.Timeout);
        using var parsed = JsonDocument.Parse(OnboardDiscoveryDiagnostics.Format(entry));
        check(parsed.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "schema", "atUtc", "model", "stage", "attempt", "failure", "code", "metadataOperation", "nativeDomain", "nativeCode" }.Order()),
            "Discovery diagnostic format has only the explicit non-personal field allowlist");
        // Use the project's existing local fixture convention and remove only this freshly created directory.
        var root = Path.GetFullPath(Path.Combine(Paths.WorkspaceRoot, "workspace", "checks", "discovery-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var logs = Path.Combine(root, "logs");
            OnboardDiscoveryDiagnostics.Append(logs, entry);
            var path = Path.Combine(logs, OnboardDiscoveryDiagnostics.FileName);
            check(File.ReadAllLines(path).Length == 1 && Directory.GetFiles(logs).Length == 1, "Discovery diagnostic writes only the expected local log");
            await File.WriteAllTextAsync(path, new string('x', OnboardDiscoveryDiagnostics.MaximumBytes));
            OnboardDiscoveryDiagnostics.Append(logs, entry);
            check(new FileInfo(path).Length <= OnboardDiscoveryDiagnostics.MaximumBytes && File.ReadAllLines(path).Length == 1,
                "Discovery diagnostic log remains bounded and starts a fresh log at its size limit");
            var nativeEntry = new OnboardMetadataDiagnostic(Id, OnboardMetadataStage.Open, 2, OnboardMetadataOutcome.Failed,
                OnboardMetadataOperation.ReportCapabilities, OnboardNativeErrorDomain.NtStatus, 0xc0110001);
            OnboardDiscoveryDiagnostics.AppendMetadata(logs, nativeEntry);
            var nativePath = Path.Combine(logs, OnboardDiscoveryDiagnostics.MetadataFileName);
            check(File.ReadAllLines(nativePath).Length == 1 && Directory.GetFiles(logs).Length == 2,
                "Native metadata diagnostic uses only its separate fixed private file");
            await File.WriteAllTextAsync(nativePath, new string('x', OnboardDiscoveryDiagnostics.MaximumBytes));
            OnboardDiscoveryDiagnostics.AppendMetadata(logs, nativeEntry);
            check(new FileInfo(nativePath).Length <= OnboardDiscoveryDiagnostics.MaximumBytes && File.ReadAllLines(nativePath).Length == 1,
                "Native metadata diagnostic remains bounded at the same size limit");
            var invalid = Path.Combine(root, "must-not-exist");
            OnboardDiscoveryDiagnostics.Append(invalid, entry with { DeviceId = "private arbitrary name" });
            OnboardDiscoveryDiagnostics.AppendMetadata(invalid, nativeEntry with { DeviceId = "private arbitrary name" });
            OnboardDiscoveryDiagnostics.AppendMetadata(invalid, nativeEntry with { Stage = (OnboardMetadataStage)999 });
            OnboardDiscoveryDiagnostics.AppendMetadata(invalid, nativeEntry with { Attempt = 3 });
            check(!Directory.Exists(invalid), "Rejected diagnostic content creates no directory or file");
            var blocker = Path.Combine(root, "file-not-directory"); File.WriteAllText(blocker, "keep");
            OnboardDiscoveryDiagnostics.Append(blocker, entry);
            check(File.ReadAllText(blocker) == "keep", "Unavailable diagnostic path cannot overwrite an existing ordinary file");
            var target = Path.Combine(root, "link-target"); Directory.CreateDirectory(target);
            var link = Path.Combine(root, "directory-link");
            try
            {
                Directory.CreateSymbolicLink(link, target);
                OnboardDiscoveryDiagnostics.Append(link, entry);
                OnboardDiscoveryDiagnostics.AppendMetadata(link, nativeEntry);
                check(Directory.GetFiles(target).Length == 0, "Discovery diagnostics reject directory symlinks");
                Directory.Delete(link);
                var fileLink = Path.Combine(logs, "linked-log"); Directory.CreateDirectory(fileLink);
                File.CreateSymbolicLink(Path.Combine(fileLink, OnboardDiscoveryDiagnostics.FileName), Path.Combine(target, "missing-log"));
                OnboardDiscoveryDiagnostics.Append(fileLink, entry);
                check(!File.Exists(Path.Combine(target, "missing-log")), "Discovery diagnostics reject dangling file symlinks");
                File.Delete(Path.Combine(fileLink, OnboardDiscoveryDiagnostics.FileName));
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("SKIP diagnostic symlink fixture: symbolic-link privilege unavailable"); }
            catch (IOException error) when ((error.HResult & 0xffff) == 1314) { Console.WriteLine("SKIP diagnostic symlink fixture: symbolic-link privilege unavailable"); }
        }
        finally
        {
            // Resolve the exact newly created target; never accept an external cleanup directory.
            var allowed = Path.GetFullPath(Path.Combine(Paths.WorkspaceRoot, "workspace", "checks")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup boundary");
            Directory.Delete(root, true);
        }
    }

    sealed class Fixture
    {
        public int FailRequest, Opens, Discoveries, Pauses;
        public bool FailEveryOpen, MalformedResponse, StorageHealthy = true;
        public Guid Identity = Installation;
        public Exception? OpenFailure, DisposeFailure;
        public string? FailOnlyPath;
        public Func<Exception>? Failure;
        public Func<int, OnboardDevice[]> Enumerate = _ => [Endpoint];
        public List<Probe> Transports = [];
        public List<OnboardDiscoveryDiagnostic> Log = [];
        public Task<OnboardTransferDiscoveryResult> Run(OnboardProfileBinding? binding = null, CancellationToken cancellationToken = default) =>
            OnboardTransferDiscovery.FindAsync(Id, true, binding, ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<OnboardDevice>>(Enumerate(++Discoveries)); },
                device =>
                {
                    Opens++;
                    if (Transports.Any(t => t.Disposals == 0)) throw new InvalidOperationException("Previous native transport not disposed");
                    if (Opens == 1 && OpenFailure is not null) throw OpenFailure;
                    var probe = new Probe { FailRequest = (Opens == 1 || FailEveryOpen) && (FailOnlyPath is null || device.Path == FailOnlyPath) ? FailRequest : 0, Failure = Failure,
                        Identity = Identity, MalformedResponse = MalformedResponse, DisposeFailure = DisposeFailure, StorageHealthy = StorageHealthy };
                    Transports.Add(probe); return new(probe, Id);
                }, ct => { ct.ThrowIfCancellationRequested(); Pauses++; return Task.CompletedTask; }, Log.Add, cancellationToken);
    }

    internal sealed class Probe : IOnboardTransport
    {
        internal static readonly OnboardImage Image = OnboardSerializer.Prepare(ProjectService.Create(Id));
        public int FailRequest, Disposals;
        public bool MalformedResponse, StorageHealthy;
        public Guid Identity;
        public Exception? DisposeFailure;
        public Func<Exception>? Failure;
        public List<byte> Commands = [];
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); OnboardProtocol.ValidateRequest(packet);
            byte command = packet[3]; Commands.Add(command);
            if (Commands.Count == FailRequest) throw Failure!();
            byte[] data;
            switch (command)
            {
                case 1:
                    var shape = OnboardSerializer.Shape(Id, Image.SchemaVersion);
                    data = new byte[24]; data[0] = 16; data[1] = (byte)shape.Rows; data[2] = (byte)shape.Cols; data[3] = 4;
                    data[4] = (byte)shape.Leds; data[5] = 32; data[6] = 128; data[7] = 249;
                    Put16(data, 8, Image.UsedBytes); Put16(data, 10, OnboardSerializer.MacroCapacity(Id, Image.SchemaVersion));
                    Put32(data, 12, 5); Put32(data, 16, Image.Crc32); Put16(data, 22, Image.SchemaVersion); break;
                case 11: data = [1, 0, 255, 1, 8, 82, 0, 0]; break;
                case 18:
                    data = new byte[24]; data[0] = 1; data[1] = 16; data[2] = 3; data[3] = 2; data[4] = 2;
                    data[5] = StorageHealthy ? (byte)1 : (byte)0; data[6] = packet[8] == 0 ? (byte)2 : packet[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), 5); Put32(data, 16, Image.Crc32);
                    Put16(data, 20, Image.UsedBytes); Put16(data, 22, Image.SchemaVersion); break;
                case 24: data = new byte[20]; data[0] = 1; data[1] = 16; Identity.TryWriteBytes(data.AsSpan(4)); break;
                default: throw new InvalidOperationException("Discovery attempted non-read-only command " + command);
            }
            var response = new byte[32]; response[0] = 73; response[1] = 71; response[2] = 1; response[3] = command;
            response[4] = MalformedResponse ? (byte)(packet[4] + 1) : packet[4]; response[6] = (byte)data.Length; data.CopyTo(response, 8);
            return Task.FromResult(response);
        }
        public ValueTask DisposeAsync() { Disposals++; return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure); }
        static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), (ushort)value);
        static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }
}
