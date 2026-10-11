using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using InGlorious.Core;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string message)
{
    try { action(); } catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException) { checks++; return; }
    throw new Exception(message);
}
var sandbox = Path.Combine(Path.GetTempPath(), "inglorious-evidence-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
try
{
    var source = Path.Combine(sandbox, "source"); Directory.CreateDirectory(source);
    Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
    File.WriteAllBytes(Path.Combine(source, "before.bin"), Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
    File.WriteAllText(Path.Combine(source, "nested", "result.json"), "{\"readbackVerified\":true}");
    File.WriteAllText(Path.Combine(source, "Bericht.txt"), "Existing source report stays byte-identical.");
    File.WriteAllText(Path.Combine(source, "Archivmanifest.json"), "Original manifest is also preserved.");
    File.WriteAllBytes(Path.Combine(source, "zero.bin"), []);
    var original = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(source, file), File.ReadAllBytes);
    var root = Path.Combine(sandbox, "archive"); var store = new FirmwareEvidenceStore(root);
    Check(!Directory.Exists(root), "Construction never creates the archive root");
    Check(FirmwareEvidenceStore.DefaultRoot.EndsWith(Path.Combine("INGLORIOUS Advanced", "Firmware")), "Default uses the user-local INGLORIOUS archive root");
    Reject(() => store.Archive(source, "../numpad", "Update", true), "Unknown/traversal device ID accepted");
    Reject(() => store.Archive(source, "numpad", "../Update", true), "Traversal operation accepted");
    Reject(() => store.Archive(source, "numpad", "update", true), "Unknown case-sensitive operation accepted");
    Check(!Directory.Exists(root), "Invalid requests do not create the archive root");
    Reject(() => new FirmwareEvidenceStore("relative"), "Relative root accepted");
    Reject(() => new FirmwareEvidenceStore(Path.Combine(sandbox, "a", "..", "escape")), "Root traversal accepted");
    Reject(() => store.Archive(Path.Combine(source, "nested", ".."), "numpad", "Update", true), "Source traversal accepted");
    foreach (var overlap in new[] { source, Path.Combine(source, "child"), sandbox }) Reject(() => new FirmwareEvidenceStore(overlap).Archive(source, "pro-iso", "Update", true), "Overlapping roots accepted");
    Reject(() => store.Archive(Path.Combine(sandbox, "missing"), "numpad", "Update", true), "Missing source accepted");
    var empty = Path.Combine(sandbox, "empty"); Directory.CreateDirectory(empty);
    Reject(() => store.Archive(empty, "numpad", "Update", true), "Empty source accepted");

    var destinations = new List<string>();
    foreach (var id in new[] { "pro-iso", "numpad" }) foreach (var operation in new[] { "Update", "Install", "Restore" }) foreach (var verified in new[] { false, true })
    {
        var target = store.Archive(source, id, operation, verified); destinations.Add(target);
        Check(Directory.GetParent(target)!.Name == (id == "pro-iso" ? "GMMK PRO ISO" : "GMMK Numpad"), "Readable product folder");
        Check(Directory.Exists(Path.Combine(target, "Originaldateien", "nested", "empty")), "Empty source directories preserved");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "Archivmanifest.json")));
        var data = manifest.RootElement;
        Check(data.GetProperty("firmwareVerified").GetBoolean() == verified && data.GetProperty("archiveIntegrityVerified").GetBoolean(), "Firmware outcome and archive integrity are separate");
        Check(data.GetProperty("deviceId").GetString() == id && data.GetProperty("operation").GetString() == operation, "Manifest exact target and operation");
        Check(data.GetProperty("files").GetArrayLength() == original.Count, "Manifest covers every original file including empty ones");
        foreach (var record in data.GetProperty("files").EnumerateArray())
        {
            var relative = record.GetProperty("path").GetString()!;
            var bytes = File.ReadAllBytes(Path.Combine(target, "Originaldateien", relative));
            Check(bytes.SequenceEqual(original[relative.Replace('/', Path.DirectorySeparatorChar)]), "Copied source bytes exact");
            Check(record.GetProperty("bytes").GetInt64() == bytes.Length && record.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(bytes)), "Every manifest length/hash independently validated");
        }
        var report = File.ReadAllText(Path.Combine(target, "Bericht.txt"));
        Check(report.Contains("keinen praktisch durchgeführten Wiederherstellungstest") && !report.Contains(source), "Report does not claim restore/startup testing or expose a workspace source path");
        Check(verified == report.Contains("Firmwareprüfung: Vollständiger Rücklesevergleich bestätigt."), "Unverified result never reported as firmware success");
    }
    Check(destinations.Distinct().Count() == destinations.Count, "Same-second archives never overwrite previous archives");
    foreach (var pair in original) Check(File.ReadAllBytes(Path.Combine(source, pair.Key)).SequenceEqual(pair.Value), "Original source remains untouched");
    using (var locked = new FileStream(Path.Combine(source, "before.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Reject(() => store.Archive(source, "numpad", "Update", true), "Locked source must fail, not claim a verified archive");
    foreach (var pair in original) Check(File.ReadAllBytes(Path.Combine(source, pair.Key)).SequenceEqual(pair.Value), "Failed archive retains every original byte");
    using (var locked = new FileStream(Path.Combine(root, ".archive.lock"), FileMode.Open, FileAccess.Read, FileShare.None))
        Reject(() => store.Archive(source, "numpad", "Update", true), "Concurrent archive reservation must fail safely");

    var outside = Path.Combine(sandbox, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "sentinel"), "untouched");
    var link = Path.Combine(sandbox, "linked");
    DirectoryLink.Create(link, outside);
    Reject(() => new FirmwareEvidenceStore(link).Archive(source, "numpad", "Update", true), "Reparse destination accepted");
    Reject(() => new FirmwareEvidenceStore(Path.Combine(link, "child")).Archive(source, "numpad", "Update", true), "Reparse destination ancestor accepted");
    Reject(() => store.Archive(link, "numpad", "Update", true), "Reparse source accepted");
    try
    {
        File.CreateSymbolicLink(Path.Combine(source, "linked-file"), Path.Combine(outside, "sentinel"));
        Reject(() => store.Archive(source, "numpad", "Update", true), "Symlink inside source accepted");
        File.Delete(Path.Combine(source, "linked-file"));
        File.CreateSymbolicLink(Path.Combine(source, "dangling"), Path.Combine(outside, "missing"));
        Reject(() => store.Archive(source, "numpad", "Update", true), "Dangling symlink accepted");
        File.Delete(Path.Combine(source, "dangling"));
    }
    catch (IOException e) when ((e.HResult & 0xFFFF) == 1314)
    {
        Console.WriteLine("NOTE: File-symlink creation requires an unavailable Windows privilege. Actual directory-reparse rejection is tested with native junctions.");
    }
    DirectoryLink.Create(Path.Combine(source, "linked-dir"), outside);
    Reject(() => store.Archive(source, "numpad", "Update", true), "Linked directory inside source accepted");
    Directory.Delete(Path.Combine(source, "linked-dir"));
    Check(File.ReadAllText(Path.Combine(outside, "sentinel")) == "untouched" && Directory.GetFileSystemEntries(outside).Length == 1, "Reparse targets stay untouched");
    Directory.Delete(link);
    Console.WriteLine($"PASS {checks} firmware-evidence checks. Temporary files only; no device, process, installed app or real archive access.");
}
finally
{
    // The one generated temporary fixture is the only cleanup target.
    if (!Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path");
    Directory.Delete(sandbox, recursive: true);
}

static class DirectoryLink
{
    public static void Create(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        Directory.CreateDirectory(link);
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var printable = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var data = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)printable.Length).CopyTo(data, 14);
        substitute.CopyTo(data, 16); printable.CopyTo(data, 18 + substitute.Length);
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputBytes, IntPtr output, int outputBytes, out int returned, IntPtr overlapped);
}
