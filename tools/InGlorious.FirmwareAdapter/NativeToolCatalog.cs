using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InGlorious.FirmwareAdapter;

/// <summary>Native tools are pinned in the adapter at publish time, never discovered on PATH.</summary>
internal static class NativeToolCatalog
{
    internal static string RuntimeIdentifier => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsMacOS() ? "osx" : throw new PlatformNotSupportedException("Unsupported USB platform.")) + "-" +
        (RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new PlatformNotSupportedException("A 64-bit USB platform is required.") });
    internal static string LibraryName => OperatingSystem.IsWindows() ? "libusb-1.0.dll" : OperatingSystem.IsMacOS() ? "libusb-1.0.dylib" : "libusb-1.0.so.0";
    internal static string DfuName => OperatingSystem.IsWindows() ? "dfu-util-static.exe" : "dfu-util";

    internal static NativeLibraryEvidence Verify(string name)
    {
        if (name != LibraryName && name != DfuName) throw new InvalidDataException("NATIVE_TOOL_NOT_ALLOWED");
        string path = Files.Child(Path.Combine(AppContext.BaseDirectory, "native"), name);
        Files.NoLinks(path);
        if (!File.Exists(path)) throw new IOException("BUNDLED_NATIVE_TOOL_MISSING");
        long bytes = new FileInfo(path).Length;
        if (bytes is < 1 or > 32 * 1024 * 1024) throw new IOException("BUNDLED_NATIVE_TOOL_SIZE_INVALID");
        string hash = Files.Hash(Files.ReadBounded(path, 32 * 1024 * 1024));
        var assembly = typeof(NativeToolCatalog).Assembly;
        string? resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(".NativeToolCatalog.json", StringComparison.Ordinal));
        if (resource is null)
        {
            // Windows builds use reviewed hashes of the reproducible pinned-source build.
            if (RuntimeIdentifier != "win-x64" || hash != (name == LibraryName ? NativeUsb.DllSha256 : Stm32Device.ToolHash))
                throw new IOException("NATIVE_TOOL_CATALOG_REQUIRED");
        }
        else
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var document = JsonDocument.Parse(stream);
            Validate(document.RootElement, RuntimeIdentifier, name, bytes, hash);
        }
        return new(path, bytes, hash);
    }

    internal static void Validate(JsonElement catalog, string runtime, string name, long bytes, string hash)
    {
        if (catalog.GetProperty("schema").GetInt32() != 1 || catalog.GetProperty("runtimeIdentifier").GetString() != runtime)
            throw new InvalidDataException("NATIVE_TOOL_RUNTIME_MISMATCH");
        string[] names = runtime == "win-x64" ? ["libusb-1.0.dll", "dfu-util-static.exe"] :
            runtime is "osx-x64" or "osx-arm64" ? ["libusb-1.0.dylib", "dfu-util"] :
            runtime == "linux-x64" ? ["libusb-1.0.so.0", "dfu-util"] : throw new InvalidDataException("NATIVE_TOOL_RUNTIME_UNSUPPORTED");
        var files = catalog.GetProperty("files").EnumerateArray().ToArray();
        if (files.Length != 2 || !files.Select(x => x.GetProperty("path").GetString()).Order().SequenceEqual(names.Order()))
            throw new InvalidDataException("NATIVE_TOOL_CATALOG_INVALID");
        foreach (var item in files)
            if (item.GetProperty("bytes").GetInt64() <= 0 || !Regex.IsMatch(item.GetProperty("sha256").GetString() ?? "", "\\A[0-9A-F]{64}\\z"))
                throw new InvalidDataException("NATIVE_TOOL_CATALOG_INVALID");
        var selected = files.Single(x => x.GetProperty("path").GetString() == name);
        if (selected.GetProperty("bytes").GetInt64() != bytes || selected.GetProperty("sha256").GetString() != hash)
            throw new InvalidDataException("BUNDLED_NATIVE_TOOL_CHANGED");
    }
}
