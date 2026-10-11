using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using InGlorious.Core;

if (args.Length == 2 && args[0] is "--owner" or "--open" or "--ping")
{
    if (!args[1].StartsWith("BackgroundTest.", StringComparison.Ordinal)) return 2;
    using var instance = new ApplicationInstance(args[1], () => Console.WriteLine("OPEN"));
    Console.WriteLine(instance.IsPrimary ? "Primary" : "Secondary");
    if (args[0] == "--owner") Console.ReadLine();
    else Console.WriteLine(await instance.NotifyExistingAsync(args[0] == "--open") ? "Acknowledged" : "Failed");
    return 0;
}

int checks = 0;
void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); checks++; }
Check(BackgroundApplicationSession.Requested(["--background"], false), "explicit background entry");
Check(!BackgroundApplicationSession.Requested([], false), "normal shortcut stays visible");
Check(!BackgroundApplicationSession.Requested(["--Background"], false), "flag is exact");
Check(!BackgroundApplicationSession.Requested(["--background-extra"], false), "flag cannot match a prefix");
Check(!BackgroundApplicationSession.Requested(["--background"], true), "offline smoke never creates a native tray or background service");

var events = new List<string>();
var tray = new FakeTray(() => events.Add("dispose"));
using (var session = new BackgroundApplicationSession(() => events.Add("show"), () => events.Add("initialize"), () => events.Add("close"), () => events.Add("hide")))
{
    Check(!session.HandleWindowClose(false), "window cannot hide before a reachable tray exists");
    session.Start(() => { events.Add("tray"); return tray; });
    Check(events.SequenceEqual(["tray", "initialize"]), "background configuration starts only after reachable tray, without any window show");
    session.Start(() => throw new Exception("duplicate tray creation"));
    Check(events.Count == 2, "background initialization runs once");
    session.Open();
    Check(events[^1] == "show", "tray opens existing window");
    int beforeHide = events.Count;
    Check(session.HandleWindowClose(false), "window close is canceled when it can hide to the tray");
    Check(events.Skip(beforeHide).SequenceEqual(["hide"]), "hiding neither initializes again nor requests actual application close");
    session.Open();
    Check(events[^1] == "show", "hidden window can be reopened");
    int beforeQuit = events.Count;
    Check(!session.HandleWindowClose(true), "explicit Quit continues through the window's normal close guards");
    Check(events.Count == beforeQuit, "explicit Quit never hides the owner of a pending-edit prompt");
    Check(session.HandleWindowClose(false, hasOpenDialog: true), "ordinary close keeps an active dialog reachable");
    Check(events[^1] == "show", "window with an open dialog is brought forward instead of hiding its children");
    session.Exit();
    Check(events.TakeLast(2).SequenceEqual(["show", "close"]), "exit delegates to normal window close with a visible owner");
    Check(!tray.Disposed, "canceled unsaved-profile/firmware close retains tray and application");
    session.Open();
    Check(events[^1] == "show", "window remains reachable after canceled exit");
    session.TrayUnavailable();
    Check(events.TakeLast(2).SequenceEqual(["dispose", "show"]), "lost notification area opens window and releases stale tray");
    Check(!session.HandleWindowClose(false), "lost tray restores normal guarded close instead of stranding a hidden process");
    session.Dispose(); session.Dispose();
    int count = events.Count; session.Open(); session.Exit(); session.TrayUnavailable();
    Check(events.Count == count, "late native callbacks cannot reopen a disposed app");
    Check(!session.HandleWindowClose(false), "disposed application never intercepts window closing");
    Check(tray.DisposeCount == 1, "tray is removed exactly once");
}
events.Clear();
using (var session = new BackgroundApplicationSession(() => events.Add("show"), () => events.Add("initialize"), () => events.Add("close"), () => events.Add("hide")))
{
    session.Start(() => null);
    Check(events.SequenceEqual(["show"]), "no tray shows normal window; its Loaded handler owns initialization");
    Check(!session.HandleWindowClose(false), "no tray permits normal guarded application exit");
}
events.Clear();
using (var session = new BackgroundApplicationSession(() => events.Add("show"), () => events.Add("initialize"), () => events.Add("close"), () => events.Add("hide")))
{
    session.Start(() => new FakeTray(() => events.Add("dispose")), startHidden: false);
    Check(events.SequenceEqual(["show"]), "normal launch remains visible and initializes through the window only");
    Check(session.HandleWindowClose(false) && events[^1] == "hide", "normal launch also supports closing to tray");
    session.Open();
    Check(events.SequenceEqual(["show", "hide", "show"]), "reopening uses the same window without replacing editor state");
    session.Start(() => throw new Exception("second normal startup"), startHidden: false);
    Check(events.Count == 3, "reopening does not create another tray or startup read");
}
events.Clear();
using (var session = new BackgroundApplicationSession(() => events.Add("show"), () => events.Add("initialize"), () => events.Add("close"), () => events.Add("hide")))
{
    session.Dispose();
    session.Start(() => throw new Exception("late asynchronous notification-area result"));
    Check(events.Count == 0, "notification-area discovery finishing after exit cannot recreate the app");
}
using (var session = new BackgroundApplicationSession(() => { }, () => { }, () => { }))
{
    session.Start(() => new FakeTray(() => { }));
    Check(!session.HandleWindowClose(false), "a session without a hide action never swallows close");
}

string NewScope() => "BackgroundTest." + Guid.NewGuid().ToString("N");
var scope = NewScope();
int activated = 0;
using (var primary = new ApplicationInstance(scope, () => Interlocked.Increment(ref activated)))
{
    Check(primary.IsPrimary, "first app owns the scope");
    using var secondary = new ApplicationInstance(scope, () => throw new Exception("secondary callback"));
    Check(!secondary.IsPrimary, "second app never initializes a second hardware service");
    Check(await secondary.NotifyExistingAsync(false), "background second start acknowledges without activation");
    Check(activated == 0, "autostart does not reveal already-running window");
    Check(await secondary.NotifyExistingAsync(true), "normal second start activates existing app");
    Check(activated == 1, "activation is delivered exactly once");
    for (int i = 0; i < 128; i++)
    {
        Check(await secondary.NotifyExistingAsync(i % 2 == 0), "back-to-back launches retain the listener");
        Check(activated == 2 + i / 2, "each normal launch activates once; background launches remain hidden");
    }
    foreach (bool invalidCommand in new[] { false, true })
    {
        using (var peer = new NamedPipeClientStream(".", PipeName(primary), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await peer.ConnectAsync(deadline.Token);
            await peer.ReadExactlyAsync(new byte[4], deadline.Token);
            if (invalidCommand)
            {
                await peer.WriteAsync(new byte[] { 2 }, deadline.Token);
                Check(await peer.ReadAsync(new byte[1], deadline.Token) == 0, "invalid command closes only its own connection without acknowledgement");
            }
        }
        Check(await secondary.NotifyExistingAsync(false), "a malformed or vanished peer does not stop the primary listener");
        Check(activated == 65, "malformed and background peers do not activate the window");
    }
    using var otherUserScope = new ApplicationInstance(NewScope(), () => { });
    Check(otherUserScope.IsPrimary, "separate user scopes do not collide");
}
using (var successor = new ApplicationInstance(scope, () => { })) Check(successor.IsPrimary, "normal exit releases pipe and ownership for restart");

foreach (bool waitingForCommand in new[] { false, true })
{
    string shutdownScope = NewScope();
    using var primary = new ApplicationInstance(shutdownScope, () => throw new Exception("shutdown must not activate a window"));
    using var peer = new NamedPipeClientStream(".", PipeName(primary), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    if (waitingForCommand)
    {
        await peer.ConnectAsync(deadline.Token);
        await peer.ReadExactlyAsync(new byte[4], deadline.Token);
    }
    await Task.Run(primary.Dispose).WaitAsync(deadline.Token);
    Check(true, "shutdown cancels an idle listener or connected peer without faulting Dispose");
    using var successor = new ApplicationInstance(shutdownScope, () => { });
    Check(successor.IsPrimary, "shutdown releases ownership even with an unfinished client");
}

if (!OperatingSystem.IsWindows())
{
    // Unix accepts a connection into the listener's queue while the previous
    // activation is still executing. It must survive that client's disconnect.
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var primary = new ApplicationInstance(NewScope(), () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); });
    string pipeName = PipeName(primary);
    using var first = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var queued = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var owner = new byte[4];
    try
    {
        await first.ConnectAsync(deadline.Token);
        await first.ReadExactlyAsync(owner, deadline.Token);
        await first.WriteAsync(new byte[] { 1 }, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        await queued.ConnectAsync(deadline.Token);
        Check(true, "next Unix client is connected before the first activation completes");
    }
    finally { release.TrySetResult(); }
    var ack = new byte[1];
    await first.ReadExactlyAsync(ack, deadline.Token);
    Check(ack[0] == 6, "first queued-client fixture receives acknowledgement");
    await queued.ReadExactlyAsync(owner, deadline.Token);
    Check(BitConverter.ToInt32(owner) == Environment.ProcessId, "queued client reaches the same primary listener");
    await queued.WriteAsync(new byte[] { 0 }, deadline.Token);
    await queued.ReadExactlyAsync(ack, deadline.Token);
    Check(ack[0] == 6, "queued Unix client survives the previous client's disconnect");
}

scope = NewScope();
using (var owner = Start("--owner", scope))
{
    Check(await Line(owner) == "Primary", "primary process acquired ownership");
    using (var ping = Start("--ping", scope))
    {
        Check(await Line(ping) == "Secondary", "autostart child detects primary process");
        Check(await Line(ping) == "Acknowledged", "cross-process background ping");
        await Finish(ping);
    }
    using (var open = Start("--open", scope))
    {
        Check(await Line(open) == "Secondary", "normal child detects primary process");
        Check(await Line(open) == "Acknowledged", "cross-process normal open");
        Check(await Line(owner) == "OPEN", "owner receives normal launch activation");
        await Finish(open);
    }
    await owner.StandardInput.WriteLineAsync();
    await Finish(owner);
}
using (var after = new ApplicationInstance(scope, () => { })) Check(after.IsPrimary, "completed child leaves no stale single-instance marker");

Console.WriteLine($"PASS {checks} background/instance checks; native tray, hardware and user settings were not accessed.");
return 0;

static string PipeName(ApplicationInstance instance) => (string)(typeof(ApplicationInstance).GetField("_pipeName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!);
static Process Start(string mode, string scope)
{
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(mode); start.ArgumentList.Add(scope);
    return Process.Start(start) ?? throw new InvalidOperationException("Test helper could not start.");
}
static async Task<string> Line(Process process) => await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)) ?? throw new InvalidOperationException("Test helper ended before a result.");
static async Task Finish(Process process)
{
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    if (process.ExitCode != 0) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
}
sealed class FakeTray(Action dispose) : IDisposable
{
    public int DisposeCount { get; private set; }
    public bool Disposed => DisposeCount != 0;
    public void Dispose() { DisposeCount++; dispose(); }
}
