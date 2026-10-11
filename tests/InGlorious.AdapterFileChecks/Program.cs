using System.Diagnostics;
using System.Reflection;
using System.Text;
using InGlorious.FirmwareAdapter;

if (args is ["--try-write", var childPath])
{
    try { File.WriteAllBytes(childPath, Encoding.UTF8.GetBytes("changed")); return 0; }
    catch (IOException) { return 23; }
}
if (args is ["--try-operation"])
{
    try { using var operation = Files.AcquireDeviceOperation(); return 0; }
    catch (IOException) { return 23; }
}
if (args is ["--hold-operation"])
{
    using var operation = Files.AcquireDeviceOperation();
    Console.WriteLine("held"); Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    return 0;
}
if (args is ["--hash-input"])
{
    using var input = Console.OpenStandardInput();
    using var received = new MemoryStream();
    input.CopyTo(received);
    Console.WriteLine(received.Length + ":" + Files.Hash(received.ToArray()));
    return 0;
}

string root = Path.Combine(Path.GetTempPath(), "inglorious-file-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
try
{
    using (Files.AcquireDeviceOperation())
        Check(OperationChild() == 23, "A second process cannot enter an active device transaction.");
    Check(OperationChild() == 0, "A completed transaction releases the process-wide device lock.");
    try { using var operation = Files.AcquireDeviceOperation(); throw new IOException("synthetic failure"); }
    catch (IOException) { }
    Check(OperationChild() == 0, "A failed transaction releases the process-wide device lock.");
    using (var child = Process.Start(OperationStart("--hold-operation")) ?? throw new IOException("Test child process did not start."))
    {
        try
        {
            string? ready = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Check(ready == "held", "The separate process holds the device lock before the crash test.");
            Reject(() => Files.AcquireDeviceOperation(), "The parent cannot overlap a child transaction.");
        }
        finally
        {
            if (!child.HasExited) child.Kill(true);
            child.WaitForExit();
        }
    }
    Check(OperationChild() == 0, "An exited owner does not leave the device transaction locked.");
    var payload = Enumerable.Range(0, 262144).Select(i => (byte)(i * 17 + 23)).ToArray();
    var payloadFile = Files.New(root, "pipe-source.bin", payload, "synthetic-test");
    string payloadPath = Files.Child(root, payloadFile.Path);
    var verified = Files.Lock(payloadPath, payloadFile.Bytes, payloadFile.Sha256);
    using (verified.Stream)
    {
        if (!OperatingSystem.IsWindows())
        {
            // POSIX permits renaming a locked inode. The pipe must use retained
            // bytes, even when the original pathname now contains other data.
            File.Move(payloadPath, Files.Child(root, "renamed-source.bin"));
            File.WriteAllBytes(payloadPath, [1, 2, 3]);
        }
        var start = OperationStart("--hash-input"); start.RedirectStandardInput = true;
        using var child = Process.Start(start) ?? throw new IOException("Test child process did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var input = Files.WriteProcessInputAsync(child.StandardInput.BaseStream, verified.Bytes);
        try
        {
            if (!child.WaitForExit(10000)) throw new TimeoutException("Pipe test child did not finish.");
            input.GetAwaiter().GetResult();
            Check(child.ExitCode == 0 && output.GetAwaiter().GetResult().Trim() == "262144:" + payloadFile.Sha256,
                "The child receives the full verified binary image and EOF, independently of its pathname.");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(true); child.WaitForExit(); }
        }
    }
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
int OperationChild()
    => Run(OperationStart("--try-operation")).ExitCode;
ProcessStartInfo OperationStart(string argument)
{
    string host = Environment.ProcessPath ?? throw new IOException("Test process path is unavailable.");
    var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(argument);
    return start;
}
(int ExitCode, string Output) Run(ProcessStartInfo start)
{
    using var process = Process.Start(start) ?? throw new IOException("Test child process did not start.");
    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(10000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Test child process timed out."); }
    string output = stdout.GetAwaiter().GetResult(); _ = stderr.GetAwaiter().GetResult();
    return (process.ExitCode, output);
}
