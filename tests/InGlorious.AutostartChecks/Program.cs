using System.Xml;
using System.Xml.Linq;
using InGlorious.Core;
using Microsoft.Win32;

var count = 0;
void Check(bool result, string message) { count++; if (!result) throw new Exception(message); }
void Reject(Action action, string message)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or ArgumentException or XmlException) { count++; return; }
    throw new Exception(message);
}
var root = Path.Combine(Path.GetTempPath(), "inglorious-autostart-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var platform in new[] { AutostartPlatform.Linux, AutostartPlatform.MacOS })
    {
        var folder = Path.Combine(root, platform.ToString());
        var binary = Path.Combine(root, platform + " spaces & $percent% `quoted", "INGLORIOUS Advanced");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "inert fixture, never executed");
        var item = new AutostartRegistration(platform, binary, folder);
        Check(!item.IsEnabled() && !Directory.Exists(folder), "Reading a missing login item has no filesystem effects");
        item.SetEnabled(true);
        var original = File.ReadAllText(item.EntryPath!);
        Check(item.IsEnabled() && original.Contains("--background"), "Login opens in the background");
        if (platform == AutostartPlatform.Linux)
        {
            var command = original.Split('\n').Single(l => l.StartsWith("Exec="))[5..];
            Check(AutostartRegistration.ParseDesktopCommand(command) == binary, "Desktop spaces/metacharacters are one literal executable argument");
            File.WriteAllText(item.EntryPath!, original.Replace("Hidden=false", "Hidden=true"));
            Check(!item.IsEnabled(), "Linux Hidden=true is respected");
            File.WriteAllText(item.EntryPath!, original.Replace("X-GNOME-Autostart-enabled=true", "X-GNOME-Autostart-enabled=false"));
            Check(!item.IsEnabled(), "GNOME's disabled choice is respected");
            foreach (var bad in new[] { original.Replace("Exec=", "Exec=sh -c "), original + "Exec=/bin/false\n",
                original.Replace("--background", "--background --extra"), original.Replace("INGLORIOUS Advanced\" --background", "Other app\" --background"),
                original.Replace("%%", "%U"), original.Replace("X-INGLORIOUS-Managed=true", "X-INGLORIOUS-Managed=false"),
                original + "DBusActivatable=true\n", original + "TryExec=/unrelated/app\n", original + "OnlyShowIn=UnknownDesktop;\n",
                original.Replace("Hidden=false", "Hidden=unknown") })
            {
                File.WriteAllText(item.EntryPath!, bad);
                Reject(() => item.SetEnabled(false), "Unknown or injected desktop command must not be deleted");
                Check(File.ReadAllText(item.EntryPath!) == bad, "Rejected entry is preserved byte for byte");
            }
            Reject(() => new AutostartRegistration(platform, Path.Combine(root, "contains=equals", "INGLORIOUS Advanced"), folder), "Desktop entry specification forbids equals sign in executable path");
        }
        else
        {
            var doc = XDocument.Parse(original);
            var dict = doc.Root!.Element("dict")!;
            var arguments = dict.Elements("array").Single().Elements("string").Select(e => e.Value).ToArray();
            Check(arguments.SequenceEqual(new[] { binary, "--background" }), "LaunchAgent preserves the exact executable and one background argument");
            var keepAlive = dict.Elements("key").Single(key => key.Value == "KeepAlive").ElementsAfterSelf().First();
            Check(keepAlive.Name == "false", "Login item is not a persistent restart service");
            foreach (var extra in new[]
            {
                new XElement("dict", new XElement("key", "Program"), new XElement("string", "/unrelated/program")),
                new XElement("dict", new XElement("key", "StartInterval"), new XElement("integer", 1))
            })
            {
                var altered = XDocument.Parse(original);
                altered.Root!.Element("dict")!.Add(extra.Elements());
                var text = altered.ToString(); File.WriteAllText(item.EntryPath!, text);
                Reject(() => item.SetEnabled(false), "Unexpected launch behavior cannot be removed as ours");
                Reject(() => item.SetEnabled(true), "Unexpected launch behavior cannot be overwritten as ours");
                Check(File.ReadAllText(item.EntryPath!) == text, "Foreign launch behavior remains intact");
            }
            var disabledDoc = XDocument.Parse(original);
            disabledDoc.Root!.Element("dict")!.Add(new XElement("key", "Disabled"), new XElement("true"));
            File.WriteAllText(item.EntryPath!, disabledDoc.ToString());
            Check(!item.IsEnabled(), "Explicit Disabled launch item is recognized without rewriting it");
            var bad = original.Replace("--background", "--execute");
            File.WriteAllText(item.EntryPath!, bad);
            Reject(() => item.SetEnabled(false), "Unexpected launch arguments cannot be removed as ours");
            Check(File.ReadAllText(item.EntryPath!) == bad, "Rejected launch item remains intact");
            File.WriteAllText(item.EntryPath!, "<!DOCTYPE plist [<!ENTITY external SYSTEM 'https://example.invalid/never'>]><plist><dict>&external;</dict></plist>");
            Reject(() => item.IsEnabled(), "No external XML entity or network request");
        }
        File.WriteAllText(item.EntryPath!, original);
        // A later installation can update its own entry from a different application folder.
        var relocated = Path.Combine(root, platform + " replacement", "INGLORIOUS Advanced");
        Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
        File.WriteAllText(relocated, "inert replacement");
        var update = new AutostartRegistration(platform, relocated, folder);
        Check(update.IsEnabled(), "Moved installations recognize the existing app's entry");
        update.SetEnabled(true);
        Check(update.IsEnabled() && File.ReadAllText(item.EntryPath!) != original, "Explicit activation updates the launch path");
        update.SetEnabled(false);
        Check(!item.IsEnabled() && !File.Exists(item.EntryPath), "Disabling removes only the owned login file");
        update.SetEnabled(false);
        Check(!item.IsEnabled(), "Disabling a missing entry is idempotent");
        Check(!Directory.GetFiles(folder, "*.tmp-*").Any(), "No temporary files remain");
        Reject(() => new AutostartRegistration(platform, "relative/INGLORIOUS Advanced", folder), "Relative executable rejected");
        Reject(() => new AutostartRegistration(platform, binary, "relative"), "Relative config rejected");
        Reject(() => new AutostartRegistration(platform, Path.Combine(root, "dotnet"), folder), "Do not register a development host");
    }
    var windowsBinary = Path.Combine(root, "INGLORIOUS Advanced.exe");
    File.WriteAllText(windowsBinary, "inert Windows fixture");
    var fake = new FakeRunStore();
    var windows = new AutostartRegistration(AutostartPlatform.Windows, windowsBinary, null, fake);
    Check(!windows.IsEnabled() && fake.Writes == 0, "Missing Windows entry is disabled without writing");
    windows.SetEnabled(true);
    Check(fake.Value == '"' + windowsBinary + "\" --background" && windows.IsEnabled(), "Registry quotes executable and adds only the background switch");
    Check(windows.GetApprovalNotice() is null, "No OS override means no extra notice");
    foreach (byte status in new byte[] { 0, 2, 3, 6, 7, 255 })
    {
        var approval = new byte[12]; approval[0] = status; fake.Approval = approval;
        Check((windows.GetApprovalNotice() is null) == (status is 0 or 2), "Non-default Windows state is referred to system settings");
        windows.SetEnabled(true);
        Check(ReferenceEquals(fake.Approval, approval) && approval[0] == status, "Enabling our entry never mutates Windows' independent startup choice");
    }
    fake.Approval = new byte[] { 2 };
    Check(windows.GetApprovalNotice() == AppAutostart.WindowsApprovalNotice, "Unknown binary layout is not interpreted as enabled");
    fake.Approval = "unexpected";
    Check(windows.GetApprovalNotice() == AppAutostart.WindowsApprovalNotice, "Unexpected registry type is not interpreted as enabled");
    fake.ApprovalReadFails = true;
    Check(windows.IsEnabled() && windows.GetApprovalNotice() == AppAutostart.WindowsApprovalNotice, "Unreadable optional OS override preserves known registration state and shows a notice");
    windows.SetEnabled(false);
    Check(!windows.IsEnabled(), "Unreadable optional OS override does not prevent disabling our own registration");
    fake.ApprovalReadFails = false;
    fake.Approval = null;
    fake.Value = '"' + windowsBinary + '"';
    Check(windows.IsEnabled(), "Owned prior entry without arguments can be upgraded");
    windows.SetEnabled(true);
    foreach (var invalid in new[] { "cmd /c something", '"' + windowsBinary + "\" --unexpected", '"' + Path.Combine(root, "other.exe") + '"' })
    {
        fake.Value = invalid;
        Reject(() => windows.SetEnabled(false), "Foreign registry command cannot be deleted");
        Check(fake.Value == invalid, "Foreign registry value preserved");
    }
    fake.Value = '"' + windowsBinary + "\" --background";
    windows.SetEnabled(false);
    Check(fake.Value is null, "Explicit disable removes own value");
    if (OperatingSystem.IsWindows())
    {
        // Real Registry API round trip uses a unique inert test key, NEVER the Windows Run key.
        var key = @"Software\INGLORIOUS-Autostart-Checks\" + Guid.NewGuid().ToString("N");
        try
        {
            var native = new AutostartRegistration(AutostartPlatform.Windows, windowsBinary, null, new WindowsAutostartRunStore(key, "Fixture"));
            native.SetEnabled(true);
            Check(native.IsEnabled(), "Native registry write/read");
            native.SetEnabled(false);
            Check(!native.IsEnabled(), "Native registry delete/read");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }
    Console.WriteLine($"PASS: {count} autostart checks; only isolated files and an inert registry key. No login item, app or device started.");
}
finally
{
    if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
        !Path.GetFileName(root).StartsWith("inglorious-autostart-checks-", StringComparison.Ordinal)) throw new Exception("Unsafe cleanup");
    Directory.Delete(root, true);
}

sealed class FakeRunStore : IAutostartRunStore
{
    public string? Value;
    public int Writes;
    public object? Approval;
    public bool ApprovalReadFails;
    public object? ReadApproval() => ApprovalReadFails ? throw new UnauthorizedAccessException("inert fixture") : Approval;
    public string? Read() => Value;
    public void Write(string command) { Value = command; Writes++; }
    public void Delete() { Value = null; Writes++; }
}
