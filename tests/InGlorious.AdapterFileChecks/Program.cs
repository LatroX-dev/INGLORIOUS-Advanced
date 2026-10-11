using System.Diagnostics;
using System.Reflection;
using System.Text;
using InGlorious.FirmwareAdapter;

if (args is ["--try-write", var childPath])
{
    try { File.WriteAllBytes(childPath, Encoding.UTF8.GetBytes("changed")); return 0; }
    catch (IOException) { return 23; }
}

string root = Path.Combine(Path.GetTempPath(), "inglorious-file-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
try
{
    byte[] original = Encoding.UTF8.GetBytes("verified input");
    Artifact artifact = Files.New(root, "input.bin", original, "synthetic-test");
    string path = Files.Child(root, artifact.Path);
    Reject(() => Files.Lock(path, original.Length - 1, artifact.Sha256), "Wrong input size must be rejected.");
    Reject(() => Files.Lock(path, original.Length, new string('0', 64)), "Wrong input digest must be rejected.");
    using (var releasedAfterFailure = Files.Lock(path, original.Length, artifact.Sha256).Stream)
        Check(releasedAfterFailure.CanRead, "A failed verification must release its handle.");

    var locked = Files.Lock(path, original.Length, artifact.Sha256);
    using (locked.Stream)
    {
        Check(locked.Bytes.AsSpan().SequenceEqual(original), "Keep the verified input in memory.");
        Reject(() => File.WriteAllBytes(path, [9]), "Concurrent writes must be rejected.");
        Check(Writer(path) == 23, "A separate managed process must also be unable to write.");
        Check(locked.Stream.Length == original.Length, "A rejected writer must not truncate the input.");
        byte[] contents = new byte[original.Length]; locked.Stream.ReadExactly(contents);
        Check(contents.AsSpan().SequenceEqual(original), "Rejected writers must leave input bytes unchanged.");
        if (OperatingSystem.IsWindows())
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "Windows must still permit the firmware tool to read.");
        else
        {
            // Like dfu-util, cat uses a native read without .NET sharing flags.
            // No USB library is loaded and no firmware process is started.
            var start = new ProcessStartInfo("/bin/cat") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(path);
            var result = Run(start);
            Check(result.ExitCode == 0 && result.Output == Encoding.UTF8.GetString(original), "Native readers must remain compatible with the POSIX advisory lock.");
        }
    }
    Check(Writer(path) == 0, "Disposing a verified input must release its lock.");
    Check(Encoding.UTF8.GetString(File.ReadAllBytes(path)) == "changed", "A writer can update the input only after release.");
    Reject(() => Files.Lock(path, original.Length, artifact.Sha256), "A later lock must recheck changed input.");
    Reject(() => Files.Child(root, "../outside.bin"), "Traversal must still be rejected.");
    if (!OperatingSystem.IsWindows())
    {
        string link = Path.Combine(root, "linked.bin");
        File.CreateSymbolicLink(link, path);
        Reject(() => Files.Lock(link, 7, Files.Hash(Encoding.UTF8.GetBytes("changed"))), "Linked inputs must still be rejected.");
        File.Delete(link);
    }
    Console.WriteLine($"PASS: {checks} adapter file checks; synthetic local files and child processes only. No hardware access.");
    return 0;
}
finally
{
    string full = Path.GetFullPath(root);
    string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || !Path.GetFileName(full).StartsWith("inglorious-file-checks-", StringComparison.Ordinal))
        throw new IOException("Refusing unsafe test cleanup.");
    Directory.Delete(full, true);
}

void Check(bool success, string message) { checks++; if (!success) throw new Exception(message); }
void Reject(Action action, string message)
{
    bool rejected = false;
    try { action(); } catch (IOException) { rejected = true; }
    Check(rejected, message);
}
int Writer(string path)
{
    string host = Environment.ProcessPath ?? throw new IOException("Test process path is unavailable.");
    var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--try-write"); start.ArgumentList.Add(path);
    return Run(start).ExitCode;
}
(int ExitCode, string Output) Run(ProcessStartInfo start)
{
    using var process = Process.Start(start) ?? throw new IOException("Test child process did not start.");
    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(10000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Test child process timed out."); }
    string output = stdout.GetAwaiter().GetResult(); _ = stderr.GetAwaiter().GetResult();
    return (process.ExitCode, output);
}
