using System.Diagnostics;
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
using (var session = new BackgroundApplicationSession(() => events.Add("show"), () => events.Add("initialize"), () => events.Add("close")))
{
    session.Start(() => { events.Add("tray"); return tray; });
    Check(events.SequenceEqual(["tray", "initialize"]), "background configuration starts only after reachable tray, without any window show");
    session.Start(() => throw new Exception("duplicate tray creation"));
    Check(events.Count == 2, "background initialization runs once");
    session.Open();
    Check(events[^1] == "show", "tray opens existing window");
    session.Exit();
    Check(events.TakeLast(2).SequenceEqual(["show", "close"]), "exit delegates to normal window close with a visible owner");
    Check(!tray.Disposed, "canceled unsaved-profile/firmware close retains tray and application");
    session.Open();
    Check(events[^1] == "show", "window remains reachable after canceled exit");
    session.TrayUnavailable();
    Check(events.TakeLast(2).SequenceEqual(["dispose", "show"]), "lost notification area opens window and releases stale tray");
    session.Dispose(); session.Dispose();
    int count = events.Count; session.Open(); session.Exit(); session.TrayUnavailable();
    Check(events.Count == count, "late native callbacks cannot reopen a disposed app");
    Check(tray.DisposeCount == 1, "tray is removed exactly once");
}
events.Clear();
using (var session = new BackgroundApplicationSession(() => events.Add("show"), () => events.Add("initialize"), () => events.Add("close")))
{
    session.Start(() => null);
    Check(events.SequenceEqual(["show"]), "no tray shows normal window; its Loaded handler owns initialization");
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
    using var otherUserScope = new ApplicationInstance(NewScope(), () => { });
    Check(otherUserScope.IsPrimary, "separate user scopes do not collide");
}
using (var successor = new ApplicationInstance(scope, () => { })) Check(successor.IsPrimary, "normal exit releases pipe and ownership for restart");

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
