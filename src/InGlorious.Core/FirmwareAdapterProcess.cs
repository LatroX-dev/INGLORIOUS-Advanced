using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InGlorious.Core;

internal static class FirmwareAdapterProcess
{
    public static Task<JsonElement> DiscoverAsync() => InvokeAsync(new Dictionary<string, object?> { ["operation"] = "discover" }, "");

    public static async Task<JsonElement> InvokeAsync(IReadOnlyDictionary<string, object?> request, string evidenceDirectory)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Tools", "FirmwareAdapter");
        DeviceSetupService.AssertNoLinks(root);
        var locks = new List<FileStream>();
        try
        {
            // This resource is generated from the reviewed adapter publish, independently of device data.
            var assembly = typeof(DeviceSetupService).Assembly;
            var resource = assembly.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith(".SetupToolCatalog.json", StringComparison.Ordinal));
            if (resource is null) throw new SetupException(L.T("Die Firmware-Werkzeuge fehlen. Installiere die vollständige App erneut."));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var manifest = JsonDocument.Parse(stream);
            if (manifest.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Invalid firmware tools catalog.");
            var expectedFiles = manifest.RootElement.GetProperty("files").EnumerateArray()
                .Select(item => item.GetProperty("path").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
            if (actualFiles.Length != expectedFiles.Count || !actualFiles.All(expectedFiles.Contains))
                throw new SetupException(L.T("Die Firmware-Werkzeuge sind beschädigt. Installiere die App erneut."));
            foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var path = DeviceSetupService.ContainedFile(root, item.GetProperty("path").GetString()!);
                bool nativeTool = item.GetProperty("path").GetString() is "native/dfu-util" or "native/libusb-1.0.so.0" or "native/libusb-1.0.dylib";
                var file = VerifyToolFile(path, item.GetProperty("bytes").GetInt64(), item.GetProperty("sha256").GetString()!, nativeTool);
                if (file is not null) locks.Add(file);
            }
            if ((string)request["operation"]! == "discover") return await DiscoverCoreAsync(root);
            var callId = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(evidenceDirectory, "request-" + callId + ".json");
            var responsePath = Path.Combine(evidenceDirectory, "response-" + callId + ".json");
            DeviceSetupService.AssertNoLinks(evidenceDirectory);
            using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(request)); file.Flush(true); }
            var start = new ProcessStartInfo(Path.Combine(root, OperatingSystem.IsWindows() ? "InGlorious.FirmwareAdapter.exe" : "InGlorious.FirmwareAdapter"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add("--response"); start.ArgumentList.Add(responsePath);
            ClearLoaderOverrides(start);
            using var process = Process.Start(start) ?? throw new IOException("Firmware adapter could not start.");
            // The adapter bounds each native operation itself. Never kill an in-progress flash on a UI timer.
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var outText = await stdout; var errText = await stderr;
            if (outText.Length + errText.Length > 4 * 1024 * 1024) throw new IOException("Firmware adapter output exceeded its limit.");
            DeviceSetupService.AssertNoLinks(responsePath);
            if (!File.Exists(responsePath) || new FileInfo(responsePath).Length > 1024 * 1024) throw new IOException("Firmware adapter returned no usable result.");
            using var response = JsonDocument.Parse(File.ReadAllBytes(responsePath));
            if (response.RootElement.GetProperty("schema").GetInt32() != 1 ||
                response.RootElement.GetProperty("operation").GetString() != (string)request["operation"]!) throw new InvalidDataException("Firmware adapter operation mismatch.");
            if (process.ExitCode != 0 && response.RootElement.GetProperty("status").GetString() is "ok" or "verified")
                throw new InvalidDataException("Firmware adapter status mismatch.");
            return response.RootElement.Clone();
        }
        finally { foreach (var item in locks) item.Dispose(); }
    }

    internal static FileStream? VerifyToolFile(string path, long expectedBytes, string expectedHash, bool adapterOwnsNativeLock)
    {
        DeviceSetupService.AssertNoLinks(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length != expectedBytes || Convert.ToHexString(SHA256.HashData(file)) != expectedHash)
                throw new SetupException(L.T("Die Firmware-Werkzeuge sind beschädigt. Installiere die App erneut."));
            // On POSIX this read handle holds LOCK_SH, which conflicts with the
            // adapter's mandatory LOCK_EX. The adapter independently rechecks its
            // embedded native-tool pins and owns that lock before loading/running.
            if (!OperatingSystem.IsWindows() && adapterOwnsNativeLock) { file.Dispose(); return null; }
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static async Task<JsonElement> DiscoverCoreAsync(string root)
    {
        var start = new ProcessStartInfo(Path.Combine(root, OperatingSystem.IsWindows() ? "InGlorious.FirmwareAdapter.exe" : "InGlorious.FirmwareAdapter"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add("--discover");
        ClearLoaderOverrides(start);
        using var process = Process.Start(start) ?? throw new IOException("USB inventory could not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            // Only this metadata-only process is cancellable. Flash processes never use this timer.
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            throw new IOException("USB inventory timed out.");
        }
        string text = await output, diagnostics = await error;
        if (process.ExitCode != 0 || text.Length + diagnostics.Length > 1024 * 1024) throw new IOException("USB inventory could not be verified.");
        using var result = JsonDocument.Parse(text);
        if (result.RootElement.GetProperty("schema").GetInt32() != 1 || result.RootElement.GetProperty("operation").GetString() != "discover" || result.RootElement.GetProperty("status").GetString() != "ok")
            throw new InvalidDataException("USB inventory response mismatch.");
        return result.RootElement.Clone();
    }

    private static void ClearLoaderOverrides(ProcessStartInfo start)
    {
        foreach (string name in new[] { "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH", "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE" })
            start.Environment.Remove(name);
        start.Environment["LC_ALL"] = "C";
    }
}
