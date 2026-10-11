using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace InGlorious.Core;

/// <summary>One app per user; a second normal launch opens the existing window.</summary>
public sealed class ApplicationInstance : IDisposable
{
    private readonly Mutex _instance;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;
    private readonly Task? _server;
    private bool _disposed;
    public bool IsPrimary { get; }

    public ApplicationInstance(Action openWindow) : this(UserScope(), openWindow) { }

    internal ApplicationInstance(string scope, Action openWindow)
    {
        if (scope.Length is < 1 or > 80 || scope.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) throw new ArgumentException("Invalid instance scope.", nameof(scope));
        string name = "INGLORIOUS.Advanced." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32];
        // macOS's per-user TMPDIR can consume most of the Unix socket path limit.
        // .NET explicitly supports an absolute pipe path. CurrentUserOnly still
        // enforces peer credentials and private socket permissions on this path.
        _pipeName = OperatingSystem.IsWindows() ? name : "/tmp/" + name;
        _instance = new Mutex(false, name + ".Session", out bool first);
        IsPrimary = first;
        if (first)
        {
            try { _server = ServeAsync(CreatePipe(), openWindow); }
            catch { _instance.Dispose(); _shutdown.Dispose(); throw; }
        }
    }

    private static string UserScope()
    {
        string directory = Paths.UserDataRoot;
        if (OperatingSystem.IsWindows()) directory = directory.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..32];
    }

    private NamedPipeServerStream CreatePipe() => new(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ServeAsync(NamedPipeServerStream firstPipe, Action openWindow)
    {
        NamedPipeServerStream? pipe = firstPipe;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                bool connected = false;
                try
                {
                    pipe ??= CreatePipe();
                    // CurrentUserOnly checks the peer on Windows and Unix. The stream has
                    // no path, command, or arbitrary payload surface: only activate/ping.
                    await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                    connected = true;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), deadline.Token).ConfigureAwait(false);
                    var command = new byte[1];
                    await pipe.ReadExactlyAsync(command, deadline.Token).ConfigureAwait(false);
                    if (command[0] > 1) continue;
                    if (command[0] == 1) openWindow();
                    await pipe.WriteAsync(new byte[] { 6 }, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // A vanished/invalid peer must not terminate the primary app. A broken
                    // IPC environment never causes a secondary app to open devices instead.
                    try { await Task.Delay(100, _shutdown.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                finally
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // Closing the Windows stream preserves unread replies, unlike
                        // Disconnect. Keep the existing per-connection lifetime there.
                        pipe?.Dispose();
                        pipe = null;
                    }
                    else if (connected)
                    {
                        // Unix disposal also destroys the listener and drops queued
                        // clients. Disconnect only its accepted peer. Do this even when
                        // a failed read marked that connection broken.
                        pipe!.Disconnect();
                    }
                }
            }
        }
        finally { pipe?.Dispose(); }
    }

    public async Task<bool> NotifyExistingAsync(bool openWindow)
    {
        if (IsPrimary || _disposed) return false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var owner = new byte[4];
            await pipe.ReadExactlyAsync(owner, deadline.Token).ConfigureAwait(false);
            int processId = BitConverter.ToInt32(owner);
            if (processId <= 0) return false;
            if (openWindow && OperatingSystem.IsWindows()) AllowSetForegroundWindow((uint)processId);
            await pipe.WriteAsync(new byte[] { openWindow ? (byte)1 : (byte)0 }, deadline.Token).ConfigureAwait(false);
            var response = new byte[1];
            await pipe.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
            return response[0] == 6;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _shutdown.Cancel();
        // Drain the pipe before releasing the name, so a successor cannot race it.
        _server?.GetAwaiter().GetResult();
        _shutdown.Dispose(); _instance.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
}
