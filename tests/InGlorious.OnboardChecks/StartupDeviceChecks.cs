using InGlorious.Core;

internal static class StartupDeviceChecks
{
    private static readonly string[] DeviceIds = BoardDescriptorRegistry.ProtocolBoards.Select(board => board.DeviceId).ToArray();

    public static async Task Run(Action<bool, string> check)
    {
        static OnboardDevice Device(string id, string suffix = "one") => new(id, id, "offline-" + id + "-" + suffix) { UsbInstanceId = "offline-root-" + id + "-" + suffix };
        static Task<IReadOnlyList<OnboardDevice>> Found(params OnboardDevice[] devices) => Task.FromResult<IReadOnlyList<OnboardDevice>>(devices);
        static byte[] Defaults(string id) => OnboardSerializer.Prepare(ProjectService.Create(id)).Payload;
        static StartupDeviceResult Result(IReadOnlyList<StartupDeviceResult> results, string id) => results.Single(r => r.DeviceId == id);
        check(DeviceCatalog.All.Count == 16 && DeviceCatalog.All.All(d => d.SupportsOnboardConfiguration) && !DeviceCatalog.Get("gmmk3-pro-100-iso").IsOfflineOnly, "Sixteen exact protocol boards are available for read-only startup loading");
        void OnlyRead(ObservedTransport transport, string label)
        {
            check(transport.Inner.Commands.All(c => c is OnboardProtocol.Info or OnboardProtocol.Read or OnboardProtocol.GetExtensions), label + ": only configuration/capability reads, no write, abort, reconnect, brightness or lighting command");
            check(transport.Disposals == 1, label + ": transport disposed exactly once before returning");
        }
        void Failed(IReadOnlyList<StartupDeviceResult> results, string id, string label)
        {
            var result = Result(results, id);
            check(result.State == StartupDeviceState.ReadFailed && !result.Loaded && result.Project is null && result.Info is null, label + ": failure never adopts a partial model or stale info");
        }
        var opens = 0;
        var none = await StartupDeviceLoader.LoadAsync(_ => Found(), _ => { opens++; throw new Exception("Unexpected open"); });
        check(none.Count == DeviceCatalog.All.Count && DeviceIds.All(id => Result(none, id) is { State: StartupDeviceState.NotConnected, Loaded: false, Project: null, Info: null }), "No endpoints leaves all fifteen models disconnected without inventing a loaded configuration");
        check(opens == 0 && StartupDeviceLoader.SelectDevice("numpad", none) == "numpad", "No endpoints does not open a transport or change the preferred offline device");

        var unknown = await StartupDeviceLoader.LoadAsync(_ => Found(Device("unknown")), _ => { opens++; throw new Exception("Unexpected unknown open"); });
        check(opens == 0 && DeviceIds.All(id => Result(unknown, id).State == StartupDeviceState.NotConnected), "An unsupported model is never opened as a known board");

        foreach (var id in DeviceIds)
        {
            var device = Device(id);
            var bytes = Defaults(id);
            var transport = new ObservedTransport(new EmulatedDevice(id, bytes));
            var result = await StartupDeviceLoader.LoadAsync(_ => Found(device), selected =>
            {
                check(ReferenceEquals(selected, device), "Open receives the exact discovered interface and root binding");
                return new(transport, selected.DeviceId);
            });
            var loaded = Result(result, id);
            check(loaded.Loaded && loaded.State == StartupDeviceState.Loaded && loaded.Info is { Generation: 0, Persistent: true }, id + ": fresh firmware defaults load without pretending generation zero is a storage error");
            check(loaded.Project!.DeviceId == id && OnboardSerializer.Prepare(loaded.Project).Payload.SequenceEqual(bytes), id + ": full default project is reconstructed from the actual device bytes");
            check(loaded.Info!.ActiveCrc == OnboardProtocol.Crc32(bytes) && loaded.Info.BlobSize == bytes.Length && loaded.Info.SchemaVersion == 1, id + ": original generation, CRC, size and schema are retained");
            check(transport.Inner.Generation == 0 && transport.Inner.Active.SequenceEqual(bytes), id + ": opening the app cannot initialize or persist a default profile");
            check(transport.Inner.Commands.Count(c => c == OnboardProtocol.Info) == 4 && transport.Inner.Commands.Count(c => c == OnboardProtocol.Read) == (bytes.Length + 19) / 20, id + ": before, inner read pair and after INFO surround every bounded read chunk");
            OnlyRead(transport, id + " defaults");

            transport = new(new EmulatedDevice(id, bytes) { Persistent = false });
            result = await StartupDeviceLoader.LoadAsync(_ => Found(device), _ => new(transport, id));
            loaded = Result(result, id);
            check(loaded.Loaded && loaded.Info is { Persistent: false } && loaded.Project is not null, id + ": active configuration remains readable when storage is unavailable");
            check(loaded.Message == L.T("Konfiguration geladen. Der Gerätespeicher ist nicht verfügbar."), id + ": unavailable storage is a visible warning rather than confirmed persistence");
            OnlyRead(transport, id + " storage warning");

            var customized = ProjectService.Create(id);
            customized.Layers[2].LightingEffect.Mode = 2;
            customized.Layers[2].LightingEffect.Colors = ["#123456", "#ABCDEF"];
            customized.Standby.Enabled = true; customized.Standby.Mode = 2;
            customized.DeviceSettings.UsbPollingIntervalMs = 8;
            var customBytes = OnboardSerializer.Prepare(customized).Payload;
            transport = new(new EmulatedDevice(id, customBytes) { EffectsSupported = true, TimingSupported = true, ExtraFlags = 224, Generation = 77 });
            result = await StartupDeviceLoader.LoadAsync(_ => Found(device), _ => new(transport, id));
            loaded = Result(result, id);
            check(loaded.Loaded && loaded.Info!.Generation == 77 && loaded.Info.SchemaVersion == 4 && OnboardSerializer.Prepare(loaded.Project!).Payload.SequenceEqual(customBytes), id + ": current effects, sequences, timing and standby load without schema migration");
            OnlyRead(transport, id + " schema4");

            foreach (var fault in new[] { "crc", "sequence", "bad-header", "wrong-board", "schema", "generation-during-read", "generation-between-reads", "persistent-after-read", "staging-before", "staging-flag-only", "staging-after-read", "transport-error", "transport-timeout", "dispose-error" })
            {
                var inner = new EmulatedDevice(id, bytes);
                transport = new(inner);
                switch (fault)
                {
                    case "crc": inner.CorruptRead = true; break;
                    case "sequence": inner.WrongSequence = true; break;
                    case "bad-header": inner.Active[17] = 1; break; // Valid overall CRC, invalid config semantics.
                    case "wrong-board": transport = new(new EmulatedDevice(id == "numpad" ? "pro-iso" : "numpad", Defaults(id == "numpad" ? "pro-iso" : "numpad"))); break;
                    case "schema": inner.ReportedSchema = 5; break;
                    case "generation-during-read": transport.Before = (command, _, _) => { if (command == OnboardProtocol.Read) inner.Generation++; }; break;
                    case "generation-between-reads": transport.Before = (command, info, _) => { if (command == OnboardProtocol.Info && info == 2) inner.Generation++; }; break;
                    case "persistent-after-read": transport.Before = (command, info, _) => { if (command == OnboardProtocol.Info && info == 4) inner.Persistent = false; }; break;
                    case "staging-before": inner.ReportedStaging = 1; break;
                    case "staging-flag-only": inner.ExtraFlags = 4; break;
                    case "staging-after-read": transport.Before = (command, info, _) => { if (command == OnboardProtocol.Info && info == 4) inner.ExtraFlags = 4; }; break;
                    case "transport-error": transport.Before = (_, _, _) => throw new IOException("Synthetic read error"); break;
                    case "transport-timeout": transport.Before = (_, _, _) => throw new OperationCanceledException("Synthetic internal deadline"); break;
                    case "dispose-error": transport.DisposeFailure = true; break;
                }
                result = await StartupDeviceLoader.LoadAsync(_ => Found(device), _ => new(transport, id));
                check(result.Count == DeviceCatalog.All.Count && DeviceIds.Where(other => other != id).All(other => Result(result, other).State == StartupDeviceState.NotConnected), id + " " + fault + ": exactly one result per model; a failed read cannot invent another connected model");
                Failed(result, id, id + " " + fault);
                if (fault.StartsWith("staging-") && fault != "staging-after-read") check(!inner.Commands.Contains(OnboardProtocol.Read), id + ": existing upload is left alone without reading or aborting it");
                OnlyRead(transport, id + " " + fault);
            }

            var openFailure = await StartupDeviceLoader.LoadAsync(_ => Found(device), _ => throw new IOException("Synthetic open failure"));
            Failed(openFailure, id, id + " open failure");
            var duplicateOpens = 0;
            var ambiguous = await StartupDeviceLoader.LoadAsync(_ => Found(device, Device(id, "two")), _ => { duplicateOpens++; throw new Exception("An ambiguous device must not be opened"); });
            check(Result(ambiguous, id).State == StartupDeviceState.Ambiguous && !Result(ambiguous, id).Loaded && duplicateOpens == 0, id + ": multiple matching devices require explicit selection, never first-match loading");
            var duplicates = await StartupDeviceLoader.LoadAsync(_ => Found(device, device), _ => { duplicateOpens++; throw new Exception("Duplicated enumeration must not be silently accepted"); });
            check(Result(duplicates, id).State == StartupDeviceState.Ambiguous && duplicateOpens == 0, id + ": duplicated enumeration remains conservative");

            using var cancellation = new CancellationTokenSource();
            transport = new(new EmulatedDevice(id, bytes));
            transport.Before = (command, _, token) =>
            {
                check(token.CanBeCanceled, id + ": device I/O receives a bounded cancellation token");
                if (command == OnboardProtocol.Read) cancellation.Cancel();
            };
            try { await StartupDeviceLoader.LoadAsync(_ => Found(device), _ => new(transport, id), cancellation.Token); throw new Exception("Caller cancellation was swallowed"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { check(true, id + ": caller cancellation propagates instead of adopting a failed or partial read"); }
            OnlyRead(transport, id + " canceled read");
        }

        foreach (var failure in new Exception[] { new IOException("Synthetic enumeration failure"), new OperationCanceledException("Synthetic discovery timeout") })
        {
            var discovered = await StartupDeviceLoader.LoadAsync(_ => Task.FromException<IReadOnlyList<OnboardDevice>>(failure), _ => { opens++; throw new Exception("Open after failed enumeration"); });
            check(discovered.Count == DeviceCatalog.All.Count && DeviceIds.All(id => Result(discovered, id) is { State: StartupDeviceState.ReadFailed, Loaded: false }) && opens == 0, "Discovery errors are not misreported as disconnected devices and do not open handles");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await StartupDeviceLoader.LoadAsync(ct => Task.FromCanceled<IReadOnlyList<OnboardDevice>>(ct), _ => { opens++; throw new Exception("Canceled startup opened a device"); }, cancellation.Token); throw new Exception("Discovery cancellation was swallowed"); }
            catch (OperationCanceledException) { check(opens == 0, "Canceled discovery propagates without opening a device"); }
        }

        foreach (var unavailable in new[] { StartupDeviceState.NotConnected, StartupDeviceState.Ambiguous, StartupDeviceState.ReadFailed })
        {
            var pad = new ObservedTransport(new EmulatedDevice("numpad", Defaults("numpad")));
            OnboardDevice[] endpoints = unavailable switch
            {
                StartupDeviceState.NotConnected => new[] { Device("numpad") },
                StartupDeviceState.Ambiguous => [Device("pro-iso"), Device("pro-iso", "two"), Device("numpad")],
                _ => [Device("pro-iso"), Device("numpad")]
            };
            var results = await StartupDeviceLoader.LoadAsync(_ => Found(endpoints), selected => selected.DeviceId == "numpad" ? new(pad, "numpad") : throw new IOException("Synthetic PRO unavailable"));
            check(Result(results, "pro-iso").State == unavailable && Result(results, "numpad").Loaded, "One unavailable model never prevents the other device's valid read");
            check(StartupDeviceLoader.SelectDevice("pro-iso", results) == "numpad", "Successfully read device takes precedence over unavailable preferred model");
            OnlyRead(pad, "mixed result");
        }
        var both = new Dictionary<string, ObservedTransport>();
        var bothResults = await StartupDeviceLoader.LoadAsync(_ => Found(Device("pro-iso"), Device("numpad")), selected =>
        {
            var transport = new ObservedTransport(new EmulatedDevice(selected.DeviceId, Defaults(selected.DeviceId)));
            both.Add(selected.DeviceId, transport); return new(transport, selected.DeviceId);
        });
        check(bothResults.Count == DeviceCatalog.All.Count && bothResults.Count(r => r.Loaded) == 2 && new[] { "pro-iso", "numpad" }.All(id => Result(bothResults, id).Loaded) && BoardDescriptorRegistry.Experimental.All(board => Result(bothResults, board.DeviceId).State == StartupDeviceState.NotConnected), "Both connected stable boards load independently, absent experimental models stay disconnected");
        check(new[] { "pro-iso", "numpad" }.All(id => StartupDeviceLoader.SelectDevice(id, bothResults) == id), "Successfully read preferred model remains selected");
        check(StartupDeviceLoader.SelectDevice("unknown", bothResults) == "pro-iso", "Unknown preference selects the first actual successful result deterministically");
        check(BoardDescriptorRegistry.Experimental.All(board => StartupDeviceLoader.SelectDevice(board.DeviceId, bothResults) == "pro-iso"), "Connected supported devices take precedence over disconnected startup preferences");
        foreach (var transport in both.Values) OnlyRead(transport, "both models");
        var selective = new ObservedTransport(new EmulatedDevice("numpad", Defaults("numpad")));
        var subset = await StartupDeviceLoader.LoadAsync(_ => Found(Device("pro-iso"), Device("numpad")), selected =>
        {
            check(selected.DeviceId == "numpad", "Reconnect refresh never opens an unrelated connected model");
            return new(selective, selected.DeviceId);
        }, deviceIds: ["numpad"]);
        check(subset.Count == 1 && subset[0].DeviceId == "numpad" && subset[0].Loaded,
            "Reconnect refresh returns only its requested exact device and reconstructs current configuration");
        OnlyRead(selective, "selective reconnect read");
        var selectiveFailure = await StartupDeviceLoader.LoadAsync(_ => Task.FromException<IReadOnlyList<OnboardDevice>>(new IOException("Unavailable metadata")),
            _ => throw new Exception("Open after enumeration failure"), deviceIds: ["numpad"]);
        check(selectiveFailure.Count == 1 && selectiveFailure[0].DeviceId == "numpad" && selectiveFailure[0].State == StartupDeviceState.ReadFailed,
            "A selective refresh failure cannot invalidate other device sessions");
        var emptyRefresh = await StartupDeviceLoader.LoadAsync(_ => throw new Exception("Empty refresh must not discover hardware"),
            _ => throw new Exception("Empty refresh must not open hardware"), deviceIds: ["unsupported"]);
        check(emptyRefresh.Count == 0, "Empty or unsupported refresh requests do not enumerate or open hardware");
        foreach (var replacement in new[] { "removed", "different-path", "ambiguous", "query-failed" })
        {
            var discoveries = 0;
            var original = Device("numpad");
            var transport = new ObservedTransport(new EmulatedDevice("numpad", Defaults("numpad")));
            Task<IReadOnlyList<OnboardDevice>> Discover(CancellationToken _)
            {
                if (++discoveries == 1) return Found(original);
                return replacement switch
                {
                    "removed" => Found(),
                    "different-path" => Found(Device("numpad", "replacement")),
                    "ambiguous" => Found(original, Device("numpad", "replacement")),
                    _ => Task.FromException<IReadOnlyList<OnboardDevice>>(new IOException("Final connection check failed"))
                };
            }
            var result = await StartupDeviceLoader.LoadAsync(Discover, _ => new(transport, "numpad"), deviceIds: ["numpad"]);
            Failed(result, "numpad", "connection changed after read: " + replacement);
            OnlyRead(transport, "connection changed after read: " + replacement);
        }
        check(!new StartupDeviceResult("pro-iso", StartupDeviceState.Loaded).Loaded, "A bare Loaded enum without validated model and info is not a usable loaded result");
    }

    private sealed class ObservedTransport(EmulatedDevice inner) : IOnboardTransport
    {
        public EmulatedDevice Inner { get; } = inner;
        public Action<byte, int, CancellationToken>? Before;
        public bool DisposeFailure;
        public int Disposals, InfoCalls;
        public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
        {
            if (packet[3] == OnboardProtocol.Info) InfoCalls++;
            Before?.Invoke(packet[3], InfoCalls, cancellationToken);
            return await Inner.ExchangeAsync(packet, cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++; await Inner.DisposeAsync();
            if (DisposeFailure) throw new IOException("Synthetic dispose failure");
        }
    }
}
