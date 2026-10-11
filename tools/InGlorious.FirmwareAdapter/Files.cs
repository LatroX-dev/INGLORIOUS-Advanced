using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InGlorious.FirmwareAdapter;

internal static class Files
{
    // dfu-util opens/closes its USB handle for each command. Keep one process-wide
    // transaction owner across probing, capture, programming and both readbacks.
    internal static IDisposable AcquireDeviceOperation()
    {
        var mutex = new Mutex(false, (OperatingSystem.IsWindows() ? @"Global\" : "") + "INGLORIOUS.Advanced.FirmwareAdapter.Operation");
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("DEVICE_OPERATION_BUSY");
            return new DeviceOperationLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class DeviceOperationLease(Mutex mutex) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); }
        }
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static async Task WriteProcessInputAsync(Stream input, ReadOnlyMemory<byte> bytes)
    {
        // Closing the pipe supplies EOF. The native consumer never reopens a
        // mutable filesystem path for these already verified image bytes.
        await using (input) await input.WriteAsync(bytes).ConfigureAwait(false);
    }
    internal static void NoLinks(string path)
    {
        string full = Path.GetFullPath(path);
        FileSystemInfo? item = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        while (item != null)
        {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Links/reparse points are not accepted.");
            item = item is DirectoryInfo d ? d.Parent : ((FileInfo)item).Directory;
        }
    }
    internal static byte[] ReadBounded(string path, int max)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 0 || stream.Length > max) throw new InvalidDataException("Unexpected file size.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    internal static string Child(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("Artifact must use a relative path.");
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(Path.Combine(folder, relative));
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Artifact escapes its directory.");
        NoLinks(full); return full;
    }
    internal static Artifact New(string folder, string name, byte[] bytes, string kind)
    {
        string path = Child(folder, name);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        var saved = ReadBounded(path, Math.Max(1, bytes.Length));
        if (!saved.AsSpan().SequenceEqual(bytes)) throw new IOException("Persisted artifact differs from its source.");
        return new(name, saved.Length, Hash(saved), kind);
    }
    internal static (FileStream Stream, byte[] Bytes) Lock(string path, long length, string hash)
    {
        NoLinks(path);
        // Unix maps every sharing mode except None to a shared advisory lock;
        // FileShare.Read alone therefore does not exclude another managed writer.
        // Native dfu-util can still read while the exclusive advisory lock is held.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None);
        try
        {
            RequirePosixLock(stream.SafeFileHandle);
            if (length < 1 || length > 32 * 1024 * 1024 || stream.Length != length) throw new IOException("Artifact length mismatch.");
            byte[] bytes = new byte[checked((int)length)]; stream.ReadExactly(bytes);
            if (!string.Equals(Hash(bytes), hash, StringComparison.Ordinal)) throw new IOException("Artifact checksum mismatch.");
            stream.Position = 0; return (stream, bytes);
        }
        catch { stream.Dispose(); throw; }
    }
    private static void RequirePosixLock(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows()) return;
        // FileStream deliberately ignores unsupported-lock errors on Unix. A
        // firmware input must instead fail closed if the filesystem cannot lock.
        const int exclusiveNonblocking = 2 | 4; // LOCK_EX | LOCK_NB on Linux/macOS.
        int result = OperatingSystem.IsLinux() ? LinuxFlock(handle, exclusiveNonblocking)
            : OperatingSystem.IsMacOS() ? MacFlock(handle, exclusiveNonblocking)
            : throw new PlatformNotSupportedException("Firmware file locking is not supported on this platform.");
        if (result != 0) throw new IOException("Artifact could not be locked for verification.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    }
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int LinuxFlock(SafeFileHandle handle, int operation);
    [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    private static extern int MacFlock(SafeFileHandle handle, int operation);
    internal static string NewSession(string requested)
    {
        if (!Path.IsPathFullyQualified(requested)) throw new IOException("Session directory must be absolute.");
        string path = Path.GetFullPath(requested);
        NoLinks(path);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Session directory already exists.");
        Directory.CreateDirectory(path);
        New(path, "session.lock", Guid.NewGuid().ToByteArray(), "session-nonce");
        return path;
    }
}
