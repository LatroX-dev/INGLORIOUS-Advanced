using System.Buffers.Binary;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
async Task<Exception> Reject(Func<Task> action, string message)
{
    try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or OperationCanceledException or NotSupportedException) { checks++; return e; }
    throw new Exception(message);
}
foreach (var id in BoardDescriptorRegistry.ProtocolBoards.Select(board => board.DeviceId))
{
    foreach (var duration in new[] { 1, 4250, 4251, 21000, 60000 })
    foreach (var original in new byte[] { 0, 1, 128, 255 })
    {
        var f = new Fixture(id) { Brightness = original, DurationMs = duration, ExtensionFlags = duration == 21000 ? (ushort)3 : (ushort)1 };
        await f.Run();
        var polls = (duration + 249) / 250;
        Check(f.StartCount == 1 && f.StopCount == 1 && f.Clock == polls * 250 && !f.Active, "One autonomous start, idempotent cleanup after the complete advertised sequence: " + duration);
        Check(f.Delays.Count == polls && f.Delays.All(d => d == TimeSpan.FromMilliseconds(250)), "Only status observation every250ms; no host-driven LED timing");
        Check(f.Brightness == original && f.OpenCount == 2 && f.DisposeCount == 2 && f.ActiveSessions == 0, "Original brightness untouched, fresh cleanup handle, all sessions disposed");
        Check(f.Commands.All(c => c is 1 or 11 or 12) && f.Generation == 17 && f.Crc == 0x12345678, "Only INFO/extensions/overlay; no dimmer/config/read/upload/commit/abort/reconnect/flash commands");
        Check(f.CoreCount == polls + 2 && f.DiscoverCount == 1 && f.Opened.All(d => d.UsbInstanceId == f.Root), "CORE checked initially and throughout; physical root remains exact");
    }
    foreach (var state in new bool?[] { true, null })
    {
        var f = new Fixture(id) { CoreState = state };
        var error = await Reject(() => f.Run(), "Blocked CORE state accepted");
        Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.CoreRunning } && f.OpenCount == 0 && f.DiscoverCount == 0, "Known/unknown CORE is a friendly no-access failure");
    }
    {
        var f = new Fixture(id) { Failure = "slow-start-ack" };
        await f.Run();
        Check(f.StartCount == 1 && f.StopCount == 1 && !f.Active, "Start-response latency is not mistaken for early interruption");
    }
    foreach (var failure in new[] { "missing", "duplicate", "wrong-root", "null-root", "wrong-model", "stock-path" })
    {
        var f = new Fixture(id) { DiscoveryFailure = failure };
        var error = await Reject(() => f.Run(), "Invalid discovery accepted: " + failure);
        Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.DeviceUnavailable } && f.OpenCount == 0, "Invalid/ambiguous binding never opens: " + failure);
    }
    foreach (var failure in new[] { "unsupported", "no-capability", "nonpersistent", "staging", "wrong-board", "already-active", "extension-short", "extension-version", "extension-flags", "extension-duration", "extension-no-capability-duration", "extension-reserved" })
    {
        var f = new Fixture(id) { Failure = failure };
        var error = await Reject(() => f.Run(), "Invalid initial state accepted: " + failure);
        Check(f.StartCount == 0 && f.StopCount == 0 && f.DisposeCount == 1, "Invalid initial state cannot start/stop an overlay: " + failure);
        if (failure is "unsupported" or "no-capability") Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.FirmwareUpdateRequired }, "Old firmware gets explicit update requirement without global fallback");
    }
    foreach (var duration in new[] { 0, 60001, 65535 })
    {
        var f = new Fixture(id) { DurationMs = duration };
        await Reject(() => f.Run(), "An unbounded or zero advertised duration was accepted");
        Check(f.StartCount == 0 && f.StopCount == 0 && f.ActiveSessions == 0, "Invalid duration is rejected before starting any overlay: " + duration);
    }
    foreach (var failure in new[] { "never-ends", "clock-frozen", "slow-polls" })
    {
        var f = new Fixture(id) { DurationMs = 21000, Failure = failure };
        var error = await Reject(() => f.Run(), "A nonterminating overlay was accepted");
        Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.ConnectionLost } && f.StopCount == 1 && !f.Active && f.ActiveSessions == 0, "Reported-duration deadline and independent polling budget both stop safely: " + failure);
        Check(f.Delays.Count <= 104 && f.Commands.All(c => c is 1 or 11 or 12), "A stuck test never runs unbounded or modifies configuration");
    }
    foreach (var at in new[] { 1, 40, 84 })
    {
        var f = new Fixture(id) { DurationMs = 21000, CancelDelayAt = at };
        var error = await Reject(() => f.Run(), "Long test cancellation was ignored");
        Check(error is OperationCanceledException && f.StopCount == 1 && !f.Active && f.ActiveSessions == 0 && f.CleanupTokens.All(t => !t.IsCancellationRequested), "21-second test cancels and verifies cleanup with an independent token at poll " + at);
    }
    {
        var f = new Fixture(id) { DurationMs = 21000, Failure = "physical-input", PhysicalInputAt = 40 };
        var error = await Reject(() => f.Run(), "A prematurely interrupted21-second test was accepted");
        Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.Interrupted } && f.StopCount == 1 && f.Generation == 17 && f.Crc == 0x12345678, "A10-second stop is an interruption of an advertised21-second test, with persistent state unchanged");
    }
    foreach (var failure in new[] { "start-lost", "start-rejected", "start-idle", "status-lost", "status-malformed", "status-wrong-pattern", "status-zero-remaining", "status-too-long" })
    {
        var f = new Fixture(id) { Failure = failure, Brightness = 77 };
        var error = await Reject(() => f.Run(), "Bad overlay response accepted: " + failure);
        Check(f.StopCount == 1 && !f.Active && f.Brightness == 77 && f.ActiveSessions == 0, "Unknown start/result always stops overlay with fresh handle: " + failure);
        Check(f.Commands.All(c => c is 1 or 11 or 12), "Failure cleanup never uses dimmer/persistent APIs");
        if (failure == "start-rejected") Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.DeviceBusy }, "Busy firmware gets useful user message");
    }
    foreach (var at in new[] { 1, 8, 17 })
    {
        var f = new Fixture(id) { CancelDelayAt = at, Brightness = 43 };
        var error = await Reject(() => f.Run(), "Cancellation swallowed");
        Check(error is OperationCanceledException && f.StopCount == 1 && !f.Active && f.Brightness == 43 && f.CleanupTokens.All(t => !t.IsCancellationRequested), "Cancellation stops under independent live cleanup token without touching dimmer");
    }
    foreach (var failure in new[] { "start-timeout", "status-timeout", "service-deadline" })
    {
        var f = new Fixture(id) { Failure = failure, DurationMs = 21000, Brightness = 43 };
        var error = await Reject(() => f.Run(), "Internal timeout was accepted: " + failure);
        Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.ConnectionLost, InnerException: OperationCanceledException } && !f.Cancel.IsCancellationRequested,
            "Internal RAW/service timeout is a connection failure, not caller cancellation: " + failure);
        var timeout = (OperationCanceledException)error.InnerException!;
        Check(timeout.CancellationToken.IsCancellationRequested == (failure == "service-deadline"),
            "Both uncancelled transport tokens and cancelled internal deadline tokens remain distinguishable in diagnostics: " + failure);
        Check(f.StartCount == 1 && f.StopCount == 1 && !f.Active && f.OpenCount == 2 && f.DisposeCount == 2 && f.ActiveSessions == 0 && f.CleanupTokens.All(t => !t.IsCancellationRequested),
            "Internal timeout still stops and verifies under a fresh independent cleanup token: " + failure);
        Check(f.Generation == 17 && f.Crc == 0x12345678 && f.Brightness == 43 && f.Commands.All(c => c is 1 or 11 or 12),
            "Timeout cleanup never modifies saved configuration or dimmer: " + failure);
    }
    {
        var f = new Fixture(id) { Failure = "cleanup-timeout", DurationMs = 21000 };
        var error = await Reject(() => f.Run(), "Cleanup timeout was accepted");
        Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.CleanupUnconfirmed, InnerException: OperationCanceledException } && !f.Cancel.IsCancellationRequested,
            "Cleanup timeout retains the stronger unconfirmed-cleanup result instead of becoming caller cancellation");
        Check(f.StartCount == 1 && f.StopCount == 1 && !f.Active && f.ActiveSessions == 0 && f.DisposeCount == 2 && f.Generation == 17 && f.Crc == 0x12345678,
            "Failed cleanup acknowledgement still disposes both sessions without persistent changes");
    }
    foreach (var state in new bool?[] { true, null })
    {
        var f = new Fixture(id) { CoreAfterDelay = state, ChangeCore = true };
        var error = await Reject(() => f.Run(), "CORE beginning mid-test ignored");
        Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.CoreRunning } && f.StartCount == 1 && f.StopCount == 1 && !f.Active, "CORE midtest blocks observation and cleanup still stops");
    }
    foreach (var failure in new[] { "stop-lost", "stop-active", "cleanup-status", "generation-change", "crc-change", "cleanup-open" })
    {
        var f = new Fixture(id) { Failure = failure };
        var error = await Reject(() => f.Run(), "Unverified cleanup/configuration accepted: " + failure);
        Check(error is FirmwareLightingTestException typed && typed.Reason == (failure.EndsWith("-change") ? FirmwareLightingTestFailure.ConfigurationChanged : FirmwareLightingTestFailure.CleanupUnconfirmed), "Cleanup/config mismatch returns specific user-safe reason: " + failure);
        Check(f.ActiveSessions == 0 && f.Brightness == 128, "Cleanup failure closes sessions and never touches brightness");
    }
    {
        var f = new Fixture(id) { Failure = "physical-input", Brightness = 31 };
        var error = await Reject(() => f.Run(), "Premature overlay end accepted");
        Check(error is FirmwareLightingTestException { Reason: FirmwareLightingTestFailure.Interrupted } && f.StopCount == 1 && f.Brightness == 220, "Physical slider input aborts overlay; intentional new dimmer value is not restored/overwritten");
    }
    {
        var f = new Fixture(id) { DelayGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var first = f.Run(); await f.DelayStarted.Task;
        await Reject(() => f.Run(), "Concurrent overlay accepted");
        Check(f.OpenCount == 1 && f.StartCount == 1, "Second test cannot interrupt first");
        f.DelayGate.SetResult(); await first; await f.Run();
        Check(f.StartCount == 2 && f.StopCount == 2 && f.OpenCount == 4, "Guard releases after completion");
    }
    {
        var f = new Fixture(id); f.Cancel.Cancel();
        await Reject(() => f.Run(), "Pre-cancelled test accepted");
        Check(f.CoreCount == 0 && f.OpenCount == 0 && f.DiscoverCount == 0, "Pre-cancellation causes no discovery/access");
        await Reject(() => f.Service.BlinkAsync(id, @"USB\VID_320F&PID_5046\STOCK", f.Core, CancellationToken.None), "Stock root accepted");
        await Reject(() => f.Service.BlinkAsync("other", f.Root, f.Core, CancellationToken.None), "Unknown model accepted");
        Check(f.OpenCount == 0, "Invalid arguments never open HID");
    }
}
foreach (var (command, payload) in new (byte, byte[])[] { (11, [0]), (12, []), (12, [0, 0]), (12, [3]), (13, [0]), (14, []) })
    await Reject(() => { _ = OnboardProtocol.Packet(command, 1, payload); return Task.CompletedTask; }, "Malformed new command accepted");
foreach (var action in new byte[] { 0, 1, 2 }) Check(OnboardProtocol.Packet(12, 4, [action])[8] == action, "Exactly three overlay actions permitted");
foreach (var root in new[] { @"USB\VID_FEED&PID_1C01\SERIAL", @"usb\vid_feed&pid_1c02\7&device&0&1" })
{
    Check(OnboardHidTransport.IsPhysicalUsbRoot(root), "Physical USB root syntax accepted");
    var ids = new Dictionary<uint, string> { [1] = @"HID\VID_FEED&PID_1C01&MI_01&COL01\HID", [2] = @"USB\VID_FEED&PID_1C01&MI_01\CHILD", [3] = root };
    var visited = new List<uint>();
    Check(OnboardHidTransport.ResolveUsbRoot(1, n => { visited.Add(n); return ids.GetValueOrDefault(n); }, n => n + 1) == root && visited.SequenceEqual(new uint[] { 1, 2, 3 }), "CM parent traversal reaches physical root without model inference");
}
foreach (var bad in new string?[] { null, "", @"HID\VID_FEED&PID_1C01\x", @"USB\VID_FEED&PID_1C01&MI_00\x", "USB\\VID_FEED&PID_1C01\\bad\n", @"USB\VID_FEED&PID_1C01\x\y" })
    Check(!OnboardHidTransport.IsPhysicalUsbRoot(bad), "Nonphysical/malformed root rejected");
{
    var reads = 0; var parents = 0;
    Check(OnboardHidTransport.ResolveUsbRoot(1, _ => { reads++; return "HID\\child"; }, n => { parents++; return n + 1; }) is null && reads == 8 && parents == 7, "Traversal is bounded to eight devnodes");
    reads = 0;
    Check(OnboardHidTransport.ResolveUsbRoot(1, _ => { reads++; return "HID\\child"; }, _ => 1) is null && reads == 1, "Parent cycle terminates");
    Check(OnboardHidTransport.ResolveUsbRoot(1, _ => null, _ => throw new Exception("Must not read parent after ID error")) is null, "Native ID error remains unbound");
    Check(OnboardHidTransport.ResolveUsbRoot(1, _ => "HID\\child", _ => null) is null, "Native parent error remains unbound");
    Check(new OnboardDevice("numpad", "name", "path").UsbInstanceId is null, "Legacy three-argument constructor stays compatible");
}
Console.WriteLine($"PASS {checks} autonomous-lighting and native-parent-resolution checks. Injected discovery, protocol transport, monotonic time and delays only; no native enumeration or hardware calls.");

sealed class Fixture
{
    public string Id { get; }
    public string Root => $@"USB\VID_FEED&PID_{BoardDescriptorRegistry.Get(Id).RuntimeProductId:X4}\EXACT";
    public OnboardDevice Target => new(Id, "INGLORIOUS" + BoardDescriptorRegistry.Get(Id).ProductSuffix,
        $@"\\?\hid#vid_feed&pid_{BoardDescriptorRegistry.Get(Id).RuntimeProductId:x4}&mi_01#test") { UsbInstanceId = Root };
    public FirmwareLightingTestService Service { get; }
    public string DiscoveryFailure = "", Failure = "";
    public byte Brightness = 128;
    public uint Generation = 17, Crc = 0x12345678;
    public int CoreCount, DiscoverCount, OpenCount, DisposeCount, ActiveSessions, StartCount, StopCount, CancelDelayAt;
    public int DurationMs = 4250, PhysicalInputAt = 1;
    public ushort ExtensionFlags = 1;
    public long Clock, ActiveUntil;
    public bool Active => ActiveUntil > Clock;
    public bool? CoreState = false, CoreAfterDelay;
    public bool ChangeCore;
    public List<byte> Commands = [];
    public List<TimeSpan> Delays = [];
    public List<OnboardDevice> Opened = [];
    public List<CancellationToken> CleanupTokens = [];
    public CancellationTokenSource Cancel = new();
    public TaskCompletionSource? DelayGate;
    public TaskCompletionSource DelayStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Fixture(string id)
    {
        Id = id;
        Service = new(Discover, Open, async (time, token) => {
            token.ThrowIfCancellationRequested(); Delays.Add(time);
            if (Failure != "clock-frozen") Clock += Failure == "slow-polls" ? 1000 : (long)time.TotalMilliseconds;
            DelayStarted.TrySetResult();
            if (DelayGate is not null) await DelayGate.Task.WaitAsync(token);
            if (ChangeCore) CoreState = CoreAfterDelay;
            if (Failure == "physical-input" && Delays.Count == PhysicalInputAt) { ActiveUntil = 0; Brightness = 220; }
            // Emulate the service's own expired deadline, without cancelling the caller or sleeping.
            if (Failure == "service-deadline") throw new OperationCanceledException("Synthetic service deadline", new CancellationToken(canceled: true));
            if (CancelDelayAt == Delays.Count) { Cancel.Cancel(); token.ThrowIfCancellationRequested(); }
        }, () => Clock);
    }
    public Task Run() => Service.BlinkAsync(Id, Root, Core, Cancel.Token);
    public Task<bool?> Core() { CoreCount++; return Task.FromResult(CoreState); }
    private IReadOnlyList<OnboardDevice> Discover()
    {
        DiscoverCount++;
        return DiscoveryFailure switch {
            "missing" => [], "duplicate" => [Target, Target], "wrong-root" => [Target with { UsbInstanceId = Root + "OTHER" }],
            "null-root" => [Target with { UsbInstanceId = null }], "wrong-model" => [Target with { DeviceId = "wrong" }],
            "stock-path" => [Target with { Path = @"\\?\hid#vid_320f&pid_5088&mi_01#stock" }], _ => [Target]
        };
    }
    private OnboardClient Open(OnboardDevice device)
    {
        if (ActiveSessions != 0) throw new Exception("Old session not closed before cleanup");
        if (OpenCount == 1 && Failure == "cleanup-open") throw new IOException("Synthetic device removal");
        OpenCount++; ActiveSessions++; Opened.Add(device);
        return new(new Transport(this, OpenCount), Id);
    }
    private sealed class Transport(Fixture f, int session) : IOnboardTransport
    {
        private bool disposed;
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException(nameof(Transport));
            token.ThrowIfCancellationRequested(); if (session % 2 == 0) f.CleanupTokens.Add(token);
            var command = packet[3]; f.Commands.Add(command); byte[] payload; byte status = 0;
            switch (command)
            {
                case 1:
                    var shape = OnboardSerializer.Shape(f.Id); payload = new byte[24];
                    payload[0] = f.Failure == "wrong-board" ? (byte)(shape.Board == 1 ? 2 : 1) : shape.Board; payload[1] = (byte)shape.Rows; payload[2] = (byte)shape.Cols; payload[3] = 4; payload[4] = (byte)shape.Leds; payload[5] = 32; payload[6] = 128;
                    payload[7] = f.Failure == "nonpersistent" ? (byte)120 : f.Failure == "staging" ? (byte)125 : (byte)121;
                    if (BoardDescriptorRegistry.Get(f.Id).HasSlider) payload[7] |= 2;
                    U16(payload, 8, shape.Size); U16(payload, 10, 768);
                    U32(payload, 12, f.Generation + (session == 2 && f.Failure == "generation-change" ? 1u : 0));
                    U32(payload, 16, f.Crc + (session == 2 && f.Failure == "crc-change" ? 1u : 0));
                    U16(payload, 20, f.Failure == "staging" ? 1 : 0); U16(payload, 22, 1); break;
                case 11:
                    if (f.Failure == "unsupported") { status = 9; payload = []; break; }
                    payload = new byte[8]; U16(payload, 0, 1); U16(payload, 2, f.ExtensionFlags); U16(payload, 4, f.DurationMs);
                    if (f.Failure == "no-capability") { U16(payload, 2, 0); U16(payload, 4, 0); }
                    if (f.Failure == "extension-short") payload = new byte[7];
                    if (f.Failure == "extension-version") U16(payload, 0, 2);
                    if (f.Failure == "extension-flags") U16(payload, 2, 4);
                    if (f.Failure == "extension-duration") U16(payload, 4, 0);
                    if (f.Failure == "extension-no-capability-duration") U16(payload, 2, 0);
                    if (f.Failure == "extension-reserved") U16(payload, 6, 1);
                    break;
                case 12:
                    var action = packet[8];
                    if (action == 1)
                    {
                        f.StartCount++;
                        if (f.Failure == "start-rejected") { status = 3; payload = []; break; }
                        f.ActiveUntil = f.Clock + f.DurationMs;
                        if (f.Failure == "start-lost") throw new IOException("Synthetic lost start acknowledgement");
                        if (f.Failure == "start-timeout") throw new OperationCanceledException("Synthetic RAW start timeout", token);
                    }
                    else if (action == 2)
                    {
                        f.StopCount++; f.ActiveUntil = 0;
                        if (f.Failure == "stop-lost") throw new IOException("Synthetic lost stop acknowledgement");
                        if (f.Failure == "cleanup-timeout") throw new OperationCanceledException("Synthetic RAW cleanup timeout", token);
                    }
                    else if (action != 0) throw new Exception("Invalid overlay action");
                    if (action == 0 && session == 1 && f.StartCount > 0 && f.Failure == "status-lost") throw new IOException("Synthetic lost status");
                    if (action == 0 && session == 1 && f.StartCount > 0 && f.Failure == "status-timeout") throw new OperationCanceledException("Synthetic RAW status timeout", token);
                    var active = f.Active || (f.Failure == "already-active" && f.StartCount == 0) || (action == 2 && f.Failure == "stop-active") ||
                        (f.Failure is "never-ends" or "clock-frozen" or "slow-polls" && f.StartCount > 0 && f.StopCount == 0);
                    var pattern = BoardDescriptorRegistry.Get(f.Id).LightingTestPattern;
                    payload = new byte[4]; payload[0] = active ? (byte)1 : (byte)0; payload[1] = active ? pattern : (byte)0;
                    U16(payload, 2, active ? (int)Math.Max(1, f.ActiveUntil - f.Clock) : 0);
                    if (action == 1 && f.Failure == "start-idle") payload = new byte[4];
                    if (action == 1 && f.Failure == "slow-start-ack") f.Clock += 500;
                    if (action == 0 && session == 1 && f.StartCount > 0)
                    {
                        if (f.Failure == "status-malformed") payload[0] = 2;
                        if (f.Failure == "status-wrong-pattern") payload[1] = (byte)(pattern == 1 ? 2 : 1);
                        if (f.Failure == "status-zero-remaining") U16(payload, 2, 0);
                        if (f.Failure == "status-too-long") U16(payload, 2, f.DurationMs + 1);
                    }
                    if (action == 0 && session == 2 && f.Failure == "cleanup-status") payload[1] = 1;
                    break;
                default: throw new Exception("FORBIDDEN dimmer/persistent/USB command: " + command);
            }
            var reply = OnboardProtocol.Packet(1, packet[4], payload); reply[3] = command; reply[5] = status; return Task.FromResult(reply);
        }
        public ValueTask DisposeAsync() { if (!disposed) { disposed = true; f.DisposeCount++; f.ActiveSessions--; } return ValueTask.CompletedTask; }
        private static void U16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);
        private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}
