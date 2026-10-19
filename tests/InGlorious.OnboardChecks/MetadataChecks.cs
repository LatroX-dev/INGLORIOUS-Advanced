using System.Text.Json;
using InGlorious.Core;

static class MetadataChecks
{
    const string Id = "gmmk3-pro-100-iso";
    const string Root = "USB\\VID_FEED&PID_1C10\\offline-metadata-fixture";
    static readonly Guid Installation = new("e266f47d-ec66-4319-bacb-066efbe38c61");
    static OnboardDevice Device() => new(Id, BoardDescriptorRegistry.Get(Id).RuntimeName,
        @"\\?\hid#vid_feed&pid_1c10&mi_01#offline-" + Guid.NewGuid().ToString("N")) { UsbInstanceId = Root };

    public static async Task Run(Action<bool, string> check)
    {
        check(OnboardHidTransport.HandleFlags(OnboardHidHandleMode.Metadata) == 0 &&
            OnboardHidTransport.HandleFlags(OnboardHidHandleMode.Reports) == 0x40000000,
            "Native metadata: synchronous query handles and cancellable overlapped report handles use distinct modes");
        var illegal = new Metadata { Mode = OnboardHidHandleMode.Reports };
        try { illegal.Manufacturer(); throw new Exception("Overlapped metadata query accepted"); }
        catch (OnboardMetadataQueryException error)
        {
            check(error.Operation == OnboardMetadataOperation.Manufacturer && error.Domain == OnboardNativeErrorDomain.Win32 && error.Code == 997,
                "Native metadata: fixture reproduces HidD query failure on an overlapped report handle");
        }

        var endpoint = Device(); var sessions = new List<Metadata>(); var reports = new DiscoveryChecks.Probe { Identity = Installation, StorageHealthy = true };
        int reportOpens = 0;
        IOnboardTransport Open() => OnboardHidTransport.OpenWithMetadata(endpoint,
            () => { var session = new Metadata(); sessions.Add(session); return session; },
            () =>
            {
                check(sessions.Count(s => !s.Disposed) == 1, "Native metadata: the pre-validation handle remains alive while report I/O is acquired");
                reportOpens++; return reports;
            });
        var first = Open(); var second = Open();
        check(reportOpens == 1 && sessions.Count == 3 && sessions.All(s => s.Disposed) && sessions.All(s => s.Calls.SequenceEqual(RequiredQueries)),
            "Native metadata: first acquisition validates fresh synchronous pre/post handles; shared acquisition revalidates without another report stream");
        check(reports.Commands.Count == 0, "Native metadata: opening and validating sends no RAW-HID or write commands");
        await first.DisposeAsync(); await second.DisposeAsync();
        check(reports.Disposals == 1, "Native metadata: shared report stream is disposed once after its last lease");

        foreach (var query in new[] { OnboardMetadataOperation.Attributes, OnboardMetadataOperation.Manufacturer, OnboardMetadataOperation.Product,
            OnboardMetadataOperation.ReportDescriptor, OnboardMetadataOperation.DeviceParent })
        {
            int factoryCalls = 0, opens = 0, enumerations = 0, pauses = 0;
            var log = new List<OnboardDiscoveryDiagnostic>(); var readers = new List<Metadata>();
            var nativeLog = new List<OnboardMetadataDiagnostic>();
            OnboardMetadata.DiagnosticSink = nativeLog.Add;
            endpoint = Device();
            var result = await Discover(endpoint, () =>
            {
                var metadata = new Metadata { FailureOperation = factoryCalls++ == 0 ? query : null,
                    ErrorDomain = query == OnboardMetadataOperation.DeviceParent ? OnboardNativeErrorDomain.ConfigRet : OnboardNativeErrorDomain.Win32,
                    ErrorCode = query == OnboardMetadataOperation.DeviceParent ? 0x0Du : 21u };
                readers.Add(metadata); return metadata;
            }, () => { opens++; return new DiscoveryChecks.Probe { Identity = Installation, StorageHealthy = true }; }, log,
                () => enumerations++, () => pauses++);
            OnboardMetadata.DiagnosticSink = null;
            check(!result.Failed && result.Targets.Count == 1 && opens == 1 && enumerations == 2 && pauses == 0 && factoryCalls == 3 && readers.All(r => r.Disposed),
                "Native metadata: failed " + query + " query recovers through a fresh complete native session without compounding discovery retries");
            check(log.Count == 0 && nativeLog.Count == 2 && nativeLog[0].Stage == OnboardMetadataStage.Open && nativeLog[0].Outcome == OnboardMetadataOutcome.Retry &&
                nativeLog[0].Operation == query && nativeLog[0].Domain == readers[0].ErrorDomain && nativeLog[0].Code == readers[0].ErrorCode &&
                nativeLog[1].Outcome == OnboardMetadataOutcome.Recovered && nativeLog[1].Attempt == 2,
                "Native metadata: every path records exact native failure and subsequent recovery without identity or private strings");
        }

        foreach (var code in new uint[] { 0, 5, 31, 50, 87, 997, 1117, uint.MaxValue })
        {
            var failure = new OnboardMetadataQueryException(OnboardMetadataOperation.Manufacturer, OnboardNativeErrorDomain.Win32, code);
            var diagnostic = OnboardTransferDiscovery.Classify(Id, OnboardDiscoveryStage.Open, 1, failure);
            check(diagnostic.NativeCode == code && diagnostic.Failure == OnboardDiscoveryFailure.MetadataReadFailed && !OnboardTransferDiscovery.IsTransient(diagnostic),
                "Native metadata: unknown, denied, invalid, pending or general read error stays distinguishable and does not retry: " + code);
        }
        foreach (var code in new uint[] { 0x0D, 0x13, 0x24 })
        {
            var failure = new OnboardMetadataQueryException(OnboardMetadataOperation.DeviceParent, OnboardNativeErrorDomain.ConfigRet, code);
            var diagnostic = OnboardTransferDiscovery.Classify(Id, OnboardDiscoveryStage.Open, 1, failure);
            check(diagnostic.NativeDomain == OnboardNativeErrorDomain.ConfigRet && !OnboardTransferDiscovery.IsTransient(diagnostic) &&
                OnboardMetadata.CanRetry(failure.Operation, failure.Domain, failure.Code) == (code != 0x13),
                "Native metadata: only the native layer owns CONFIGRET retry; its domain is never treated as Win32");
        }
        try { OnboardMetadata.RequireCapabilitiesStatus(unchecked((int)0xc0110001)); throw new Exception("Invalid preparsed data accepted"); }
        catch (OnboardMetadataQueryException error)
        {
            var diagnostic = OnboardTransferDiscovery.Classify(Id, OnboardDiscoveryStage.Open, 1, error);
            using var json = JsonDocument.Parse(OnboardDiscoveryDiagnostics.Format(diagnostic));
            check(error.Domain == OnboardNativeErrorDomain.NtStatus && !OnboardTransferDiscovery.IsTransient(diagnostic) &&
                json.RootElement.GetProperty("nativeCode").GetUInt32() == 0xc0110001 &&
                json.RootElement.GetProperty("nativeDomain").GetString() == "NtStatus",
                "Native metadata: full 32-bit HIDP NTSTATUS survives logging and malformed preparsed data is not retried");
        }
        OnboardMetadata.RequireWin32(true, OnboardMetadataOperation.Product, 997);
        OnboardMetadata.RequireCapabilitiesStatus(0x00110000);
        OnboardMetadata.RequireConfigRet(0, OnboardMetadataOperation.DeviceParent);
        check(true, "Native metadata: successful queries never interpret stale last-error or successful status codes as failures");

        foreach (var mutate in new Action<Metadata>[]
        {
            m => m.Vendor = 0x320f, m => m.ProductId = 0x1c01, m => m.ManufacturerText = "", m => m.ManufacturerText = "other",
            m => m.ProductText = "INGLORIOUS Numpad", m => m.Caps = m.Caps with { UsagePage = 1 },
            m => m.Caps = m.Caps with { Usage = 1 }, m => m.Caps = m.Caps with { InputBytes = 32 },
            m => m.Caps = m.Caps with { OutputBytes = 32 }, m => m.RootValue = "USB\\VID_FEED&PID_1C10\\different", m => m.RootValue = null
        })
        {
            int opens = 0, pauses = 0; var log = new List<OnboardDiscoveryDiagnostic>(); endpoint = Device();
            var result = await Discover(endpoint, () => { var metadata = new Metadata(); mutate(metadata); return metadata; },
                () => { opens++; return new DiscoveryChecks.Probe(); }, log, () => { }, () => pauses++);
            check(result.Failed && result.Targets.Count == 0 && opens == 0 && pauses == 0 && log.Single().Failure == OnboardDiscoveryFailure.InvalidReply,
                "Native metadata: successfully read but wrong identity, collection, report sizes or root remains a nonretryable rejection");
        }

        int postSessions = 0, postPauses = 0;
        var postLog = new List<OnboardDiscoveryDiagnostic>();
        var unusedReports = new DiscoveryChecks.Probe { DisposeFailure = new System.ComponentModel.Win32Exception(170) };
        var postResult = await Discover(Device(), () => new Metadata { ProductText = ++postSessions == 1 ? BoardDescriptorRegistry.Get(Id).RuntimeName : "wrong" },
            () => unusedReports, postLog, () => { }, () => postPauses++);
        check(postResult.Failed && postSessions == 2 && postPauses == 0 && unusedReports.Disposals == 1 && unusedReports.Commands.Count == 0 &&
            postLog.Single().Failure == OnboardDiscoveryFailure.InvalidReply,
            "Native metadata: fresh post-open identity rejection closes the unused report stream and a close failure cannot mask the rejection");

        await CheckEnumeration(check);
        CheckMissingRoot(check);
        await CheckNativeBudget(check);
    }

    private static async Task CheckEnumeration(Action<bool, string> check)
    {
        var endpoints = new[] { Device(), Device() }; int enumerations = 0, opens = 0, pauses = 0, metadataPauses = 0;
        var log = new List<OnboardDiscoveryDiagnostic>();
        var result = await OnboardTransferDiscovery.FindAsync(Id, true, null, _ =>
        {
            var found = new List<OnboardDevice>(); enumerations++;
            for (int index = 0; index < endpoints.Length; index++)
            {
                int session = 0;
                if (OnboardHidTransport.IdentifyConnectedWithMetadata(endpoints[index].Path,
                    () => new Metadata { FailureOperation = enumerations == 1 && index == 1 && session++ == 0 ? OnboardMetadataOperation.Manufacturer : null },
                    pause: () => metadataPauses++) is { } device) found.Add(device);
            }
            return Task.FromResult<IReadOnlyList<OnboardDevice>>(found);
        }, device =>
        {
            opens++;
            return new(OnboardHidTransport.OpenWithMetadata(device, () => new Metadata(),
                () => new DiscoveryChecks.Probe { Identity = Installation, StorageHealthy = true }), Id);
        }, _ => { pauses++; return Task.CompletedTask; }, log.Add);
        check(!result.Failed && result.Targets.Count == 2 && opens == 2 && enumerations == 2 && pauses == 0 && metadataPauses == 1 && log.Count == 0,
            "Native metadata: fresh-session recovery retains both matching keyboards, never auto-selects one survivor");

        log.Clear(); opens = 0; pauses = 0;
        result = await OnboardTransferDiscovery.FindAsync(Id, true, null, _ =>
        {
            using var metadata = new Metadata { FailureOperation = OnboardMetadataOperation.Product, ErrorCode = 0 };
            OnboardHidTransport.Identify(metadata, endpoints[1].Path);
            return Task.FromResult<IReadOnlyList<OnboardDevice>>([endpoints[0]]);
        }, device => { opens++; return new(new DiscoveryChecks.Probe(), Id); }, _ => { pauses++; return Task.CompletedTask; }, log.Add);
        check(result.Failed && result.Targets.Count == 0 && opens == 0 && pauses == 0 && log.Single().NativeCode == 0,
            "Native metadata: unknown-error false return remains a failed scan, never an empty string or hidden candidate");
    }

    private static void CheckMissingRoot(Action<bool, string> check)
    {
        var endpoint = Device();
        foreach (var resolver in new Func<string?>[]
        {
            () => null,
            () => "HID\\VID_FEED&PID_1C10\\collection",
            () => "USB\\VID_FEED&PID_1C10&MI_01\\interface",
            () => OnboardHidTransport.ResolveUsbRoot(1, _ => "HID\\collection", _ => 1),
            () => OnboardHidTransport.ResolveUsbRoot(1, _ => "HID\\collection", node => node + 1)
        })
        {
            using var metadata = new Metadata();
            try { OnboardHidTransport.IdentifyConnected(metadata, endpoint.Path, resolver); throw new Exception("Unresolved root published"); }
            catch (InvalidDataException error)
            {
                check(!OnboardTransferDiscovery.IsTransient(OnboardTransferDiscovery.Classify(Id, OnboardDiscoveryStage.Enumeration, 1, error)),
                    "Native metadata: production discovery rejects missing, interface-only, cyclic or over-depth ancestry without retry");
            }
        }
        int metadataOpens = 0, reportOpens = 0;
        try
        {
            OnboardHidTransport.OpenWithMetadata(endpoint with { UsbInstanceId = null },
                () => { metadataOpens++; return new Metadata(); }, () => { reportOpens++; return new DiscoveryChecks.Probe(); });
            throw new Exception("Endpoint without physical root opened");
        }
        catch (InvalidDataException)
        {
            check(metadataOpens == 0 && reportOpens == 0, "Native metadata: explicit Open cannot bypass root validation for an ordinary supported keyboard");
        }
        using var valid = new Metadata();
        check(OnboardHidTransport.IdentifyConnected(valid, endpoint.Path, () => Root)?.UsbInstanceId == Root,
            "Native metadata: production discovery preserves the positively resolved physical USB root");
    }

    private static async Task CheckNativeBudget(Action<bool, string> check)
    {
        foreach (var failFirst in new[] { false, true })
        {
            var sessions = 0;
            try
            {
                OnboardHidTransport.IdentifyConnectedWithMetadata(Device().Path, () =>
                {
                    var metadata = new Metadata();
                    if (sessions++ == 0 && failFirst)
                    { metadata.FailureOperation = OnboardMetadataOperation.Product; metadata.ErrorCode = 995; }
                    else { metadata.ProductId = 0x1c01; metadata.ProductText = BoardDescriptorRegistry.Get("pro-iso").RuntimeName; }
                    return metadata;
                }, pause: () => { });
                throw new Exception("Different model published under the original interface");
            }
            catch (InvalidDataException)
            {
                check(sessions == (failFirst ? 2 : 1), "Native metadata: successful attributes for another model cannot reclassify a matching RAW interface");
            }
        }
        foreach (var capabilities in new[] { new OnboardHidCapabilities(1, 6, 9, 2), new(12, 1, 3, 0), new(0xff60, 0x61, 32, 33) })
        {
            var nonRaw = new Metadata { Caps = capabilities, FailureOperation = OnboardMetadataOperation.Manufacturer, ErrorCode = 995 };
            var found = OnboardHidTransport.IdentifyConnectedWithMetadata(Device().Path, () => nonRaw, pause: () => throw new Exception("Non-RAW retry"));
            check(found is null && nonRaw.Disposed && !nonRaw.Calls.Contains(OnboardMetadataOperation.Manufacturer) &&
                !nonRaw.Calls.Contains(OnboardMetadataOperation.Product) && !nonRaw.Calls.Contains(OnboardMetadataOperation.DeviceParent),
                "Native metadata: positively identified keyboard/consumer/wrong-size collections do not issue strings or root queries");
        }

        var diagnostics = new List<OnboardMetadataDiagnostic>();
        var priorSink = OnboardMetadata.DiagnosticSink;
        OnboardMetadata.DiagnosticSink = diagnostics.Add;
        try
        {
            var endpoint = Device(); var readers = new List<Metadata>(); var reportStreams = new List<DiscoveryChecks.Probe>();
            var pauses = 0;
            var opened = OnboardHidTransport.OpenWithMetadata(endpoint, () =>
            {
                var reader = new Metadata { FailureOperation = readers.Count == 1 ? OnboardMetadataOperation.Product : null, ErrorCode = 995 };
                readers.Add(reader); return reader;
            }, () => { var stream = new DiscoveryChecks.Probe(); reportStreams.Add(stream); return stream; }, pause: () => pauses++);
            check(readers.Count == 4 && readers.All(r => r.Disposed) && pauses == 1 && reportStreams.Count == 2 &&
                reportStreams[0].Disposals == 1 && reportStreams.All(r => r.Commands.Count == 0),
                "Native metadata: post-open string 995 closes the unused report handle and repeats full fresh pre/post metadata only");
            await opened.DisposeAsync();
            check(reportStreams.All(r => r.Disposals == 1) && diagnostics.Select(d => d.Outcome).SequenceEqual(new[] { OnboardMetadataOutcome.Retry, OnboardMetadataOutcome.Recovered }),
                "Native metadata: recovered post-open attempt disposes both streams and records retry/recovery once");

            var closeReaders = 0; var closeOpens = 0; var closePauses = 0;
            var unclosed = new DiscoveryChecks.Probe { DisposeFailure = new IOException("private close failure") };
            try
            {
                OnboardHidTransport.OpenWithMetadata(Device(), () => new Metadata
                { FailureOperation = closeReaders++ == 1 ? OnboardMetadataOperation.Product : null, ErrorCode = 995 },
                    () => { closeOpens++; return unclosed; }, pause: () => closePauses++);
                throw new Exception("Unconfirmed report close retried");
            }
            catch (OnboardMetadataQueryException failure)
            {
                check(failure.Code == 995 && failure.CleanupFailed && closeReaders == 2 && closeOpens == 1 && closePauses == 0 &&
                    unclosed.Disposals == 1 && unclosed.Commands.Count == 0,
                    "Native metadata: failed cleanup preserves the original native failure but never reopens a potentially live report handle");
            }

            diagnostics.Clear(); readers.Clear(); reportStreams.Clear(); pauses = 0; endpoint = Device();
            try
            {
                OnboardHidTransport.OpenWithMetadata(endpoint, () =>
                {
                    var reader = new Metadata
                    {
                        FailureOperation = readers.Count == 1 ? OnboardMetadataOperation.Product : null, ErrorCode = 995,
                        RootValue = readers.Count == 2 ? "USB\\VID_FEED&PID_1C10\\different-device" : Root
                    };
                    readers.Add(reader); return reader;
                }, () => { var stream = new DiscoveryChecks.Probe(); reportStreams.Add(stream); return stream; }, pause: () => pauses++);
                throw new Exception("Changed root accepted after retry");
            }
            catch (InvalidDataException)
            {
                check(readers.Count == 3 && readers.All(r => r.Disposed) && pauses == 1 && reportStreams.Count == 1 && reportStreams[0].Disposals == 1 &&
                    reportStreams[0].Commands.Count == 0 && diagnostics.Last().Outcome == OnboardMetadataOutcome.Rejected,
                    "Native metadata: fresh root change during retry rejects before acquiring another report stream");
            }

            foreach (var code in new uint[] { 0, 5, 31, 50, 87, 997, 1117 })
            {
                var sessions = 0; pauses = 0;
                try
                {
                    OnboardHidTransport.IdentifyConnectedWithMetadata(Device().Path,
                        () => { sessions++; return new Metadata { FailureOperation = OnboardMetadataOperation.Product, ErrorCode = code }; }, pause: () => pauses++);
                    throw new Exception("Native query false accepted");
                }
                catch (OnboardMetadataQueryException failure)
                {
                    check(failure.Code == code && sessions == 1 && pauses == 0,
                        "Native metadata: failed read with unknown/nontransient code has no native retry: " + code);
                }
            }

            diagnostics.Clear(); var enumerationCalls = 0; var reportCalls = 0; var nativeSessions = 0; var outerPauses = 0;
            var endpoints = new[] { Device(), Device() };
            var failedScan = await OnboardTransferDiscovery.FindAsync(Id, true, null, _ =>
            {
                enumerationCalls++;
                var found = new List<OnboardDevice>();
                for (var index = 0; index < endpoints.Length; index++)
                {
                    var current = index;
                    var device = OnboardHidTransport.IdentifyConnectedWithMetadata(endpoints[index].Path, () =>
                    {
                        nativeSessions++;
                        return new Metadata { FailureOperation = current == 1 ? OnboardMetadataOperation.Manufacturer : null, ErrorCode = 995 };
                    }, pause: () => { });
                    if (device is not null) found.Add(device);
                }
                return Task.FromResult<IReadOnlyList<OnboardDevice>>(found);
            }, device => { reportCalls++; return new(new DiscoveryChecks.Probe(), Id); },
                _ => { outerPauses++; return Task.CompletedTask; });
            check(failedScan.Failed && failedScan.Targets.Count == 0 && enumerationCalls == 1 && nativeSessions == 3 && reportCalls == 0 && outerPauses == 0 &&
                diagnostics.Select(d => d.Outcome).SequenceEqual(new[] { OnboardMetadataOutcome.Retry, OnboardMetadataOutcome.Failed }),
                "Native metadata: an exhausted second candidate blocks the whole scan after exactly two native attempts without another discovery retry");

            foreach (var rejectSecondAttempt in new Action<Metadata>[]
                { m => m.Caps = m.Caps with { UsagePage = 1 }, m => m.ProductText = "wrong product" })
            {
                diagnostics.Clear(); enumerationCalls = 0; reportCalls = 0; outerPauses = 0;
                var secondSessions = 0;
                var changedScan = await OnboardTransferDiscovery.FindAsync(Id, true, null, _ =>
                {
                    enumerationCalls++;
                    var found = new List<OnboardDevice>();
                    for (var index = 0; index < endpoints.Length; index++)
                    {
                        var current = index;
                        var device = OnboardHidTransport.IdentifyConnectedWithMetadata(endpoints[index].Path, () =>
                        {
                            var metadata = new Metadata();
                            if (current == 1)
                            {
                                if (secondSessions++ == 0)
                                { metadata.FailureOperation = OnboardMetadataOperation.Product; metadata.ErrorCode = 995; }
                                else rejectSecondAttempt(metadata);
                            }
                            return metadata;
                        }, pause: () => { });
                        if (device is not null) found.Add(device);
                    }
                    return Task.FromResult<IReadOnlyList<OnboardDevice>>(found);
                }, device => { reportCalls++; return new(new DiscoveryChecks.Probe(), Id); },
                    _ => { outerPauses++; return Task.CompletedTask; });
                check(changedScan.Failed && changedScan.Targets.Count == 0 && enumerationCalls == 1 && secondSessions == 2 &&
                    reportCalls == 0 && outerPauses == 0 && diagnostics.Last().Outcome == OnboardMetadataOutcome.Rejected,
                    "Native metadata: a second endpoint that becomes nonmatching after 995 invalidates the scan instead of hiding ambiguity");
            }

            // A diagnostic observer is untrusted best-effort infrastructure, never part of read success.
            OnboardMetadata.DiagnosticSink = _ => throw new InvalidOperationException("private diagnostic failure");
            var attempts = 0;
            var recovered = OnboardHidTransport.IdentifyConnectedWithMetadata(Device().Path,
                () => new Metadata { FailureOperation = attempts++ == 0 ? OnboardMetadataOperation.Product : null, ErrorCode = 995 }, pause: () => { });
            check(recovered?.DeviceId == Id && attempts == 2, "Native metadata: a failing local diagnostic observer cannot alter native recovery");
        }
        finally { OnboardMetadata.DiagnosticSink = priorSink; }

        var entry = new OnboardMetadataDiagnostic(Id, OnboardMetadataStage.Enumeration, 1, OnboardMetadataOutcome.Retry,
            OnboardMetadataOperation.Product, OnboardNativeErrorDomain.Win32, 995);
        using var formatted = JsonDocument.Parse(OnboardDiscoveryDiagnostics.FormatMetadata(entry));
        check(formatted.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[]
            { "schema", "atUtc", "model", "stage", "attempt", "outcome", "metadataOperation", "nativeDomain", "nativeCode" }.Order()),
            "Native metadata: diagnostic fields contain no device/user strings, paths, serials, payloads or exception text");
    }

    private static Task<OnboardTransferDiscoveryResult> Discover(OnboardDevice device, Func<IOnboardMetadataSession> metadata,
        Func<IOnboardTransport> reports, List<OnboardDiscoveryDiagnostic> log, Action enumerate, Action pause) =>
        OnboardTransferDiscovery.FindAsync(Id, true, null,
            _ => { enumerate(); return Task.FromResult<IReadOnlyList<OnboardDevice>>([device]); },
            endpoint => new(OnboardHidTransport.OpenWithMetadata(endpoint, metadata, reports), Id),
            _ => { pause(); return Task.CompletedTask; }, log.Add);

    private static readonly OnboardMetadataOperation[] RequiredQueries = [OnboardMetadataOperation.Attributes,
        OnboardMetadataOperation.ReportDescriptor, OnboardMetadataOperation.ReportCapabilities, OnboardMetadataOperation.Manufacturer,
        OnboardMetadataOperation.Product, OnboardMetadataOperation.DeviceParent];

    sealed class Metadata : IOnboardMetadataSession
    {
        public OnboardHidHandleMode Mode = OnboardHidHandleMode.Metadata;
        public bool Disposed;
        public ushort Vendor = 0xfeed, ProductId = 0x1c10;
        public string ManufacturerText = "INGLORIOUS", ProductText = BoardDescriptorRegistry.Get(Id).RuntimeName;
        public string? RootValue = Root;
        public OnboardHidCapabilities Caps = new(0xff60, 0x61, 33, 33);
        public OnboardMetadataOperation? FailureOperation;
        public OnboardNativeErrorDomain ErrorDomain = OnboardNativeErrorDomain.Win32;
        public uint ErrorCode = 21;
        public List<OnboardMetadataOperation> Calls = [];
        private void Query(OnboardMetadataOperation operation)
        {
            if (Disposed) throw new ObjectDisposedException("Metadata fixture");
            Calls.Add(operation);
            if (OnboardHidTransport.HandleFlags(Mode) != 0)
                OnboardMetadata.RequireWin32(false, operation, 997);
            if (operation == FailureOperation)
            {
                if (ErrorDomain == OnboardNativeErrorDomain.Win32) OnboardMetadata.RequireWin32(false, operation, unchecked((int)ErrorCode));
                else if (ErrorDomain == OnboardNativeErrorDomain.ConfigRet) OnboardMetadata.RequireConfigRet(ErrorCode, operation);
                else OnboardMetadata.RequireCapabilitiesStatus(unchecked((int)ErrorCode));
            }
        }
        public OnboardHidAttributes Attributes() { Query(OnboardMetadataOperation.Attributes); return new(Vendor, ProductId); }
        public string Manufacturer() { Query(OnboardMetadataOperation.Manufacturer); return ManufacturerText; }
        public string Product() { Query(OnboardMetadataOperation.Product); return ProductText; }
        public OnboardHidCapabilities Capabilities() { Query(OnboardMetadataOperation.ReportDescriptor); Query(OnboardMetadataOperation.ReportCapabilities); return Caps; }
        public string? UsbRoot() { Query(OnboardMetadataOperation.DeviceParent); return RootValue; }
        public void Dispose() => Disposed = true;
    }
}
