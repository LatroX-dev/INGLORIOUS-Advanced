using System.IO;
using System.Text;
using System.Text.Json;

namespace InGlorious.Core;

/// <summary>Small local troubleshooting log. Deliberately has no endpoint, identity, exception text or configuration fields.</summary>
internal static class OnboardDiscoveryDiagnostics
{
    private static readonly object Gate = new();
    internal const int MaximumBytes = 64 * 1024;
    internal const string FileName = "device-discovery.jsonl";
    internal const string MetadataFileName = "device-metadata.jsonl";

    internal static string FormatMetadata(OnboardMetadataDiagnostic entry)
    {
        if (entry.DeviceId is not null && !BoardDescriptorRegistry.TryGet(entry.DeviceId, out _) ||
            !Enum.IsDefined(entry.Stage) || !Enum.IsDefined(entry.Outcome) || !Enum.IsDefined(entry.Operation) || !Enum.IsDefined(entry.Domain) ||
            entry.Attempt is < 1 or > OnboardMetadata.MaximumAttempts)
            throw new ArgumentException("Invalid metadata diagnostic fields.", nameof(entry));
        return JsonSerializer.Serialize(new { schema = 1, atUtc = DateTime.UtcNow, model = entry.DeviceId,
            stage = entry.Stage.ToString(), attempt = entry.Attempt, outcome = entry.Outcome.ToString(),
            metadataOperation = entry.Operation.ToString(), nativeDomain = entry.Domain.ToString(), nativeCode = entry.Code });
    }

    internal static void AppendMetadata(string directory, OnboardMetadataDiagnostic entry)
    {
        try { AppendLine(directory, MetadataFileName, FormatMetadata(entry)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    internal static string Format(OnboardDiscoveryDiagnostic entry)
    {
        if (!BoardDescriptorRegistry.TryGet(entry.DeviceId, out _) || !Enum.IsDefined(entry.Stage) || !Enum.IsDefined(entry.Failure) ||
            entry.Attempt is < 1 or > OnboardTransferDiscovery.MaximumAttempts || entry.Code is < 0 or > 65535 ||
            entry.MetadataOperation is { } operation && !Enum.IsDefined(operation) || entry.NativeDomain is { } domain && !Enum.IsDefined(domain) ||
            (entry.Failure == OnboardDiscoveryFailure.MetadataReadFailed) != (entry.MetadataOperation is not null && entry.NativeDomain is not null && entry.NativeCode is not null) ||
            entry.Failure != OnboardDiscoveryFailure.MetadataReadFailed && (entry.MetadataOperation is not null || entry.NativeDomain is not null || entry.NativeCode is not null))
            throw new ArgumentException("Invalid diagnostic fields.", nameof(entry));
        return JsonSerializer.Serialize(new { schema = 1, atUtc = DateTime.UtcNow, model = entry.DeviceId,
            stage = entry.Stage.ToString(), attempt = entry.Attempt, failure = entry.Failure.ToString(), code = entry.Code,
            metadataOperation = entry.MetadataOperation?.ToString(), nativeDomain = entry.NativeDomain?.ToString(), nativeCode = entry.NativeCode });
    }

    internal static void Append(string directory, OnboardDiscoveryDiagnostic entry)
    {
        // This best-effort log must not make a successful read fail.
        try
        {
            AppendLine(directory, FileName, Format(entry));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private static void AppendLine(string directory, string fileName, string text)
    {
        var line = Encoding.UTF8.GetBytes(text + "\n");
        lock (Gate)
        {
            var full = Path.GetFullPath(directory); AssertOrdinaryPath(full);
            Directory.CreateDirectory(full); AssertOrdinaryPath(full);
            var path = Path.Combine(full, fileName); AssertOrdinaryPath(path);
            var mode = File.Exists(path) && new FileInfo(path).Length + line.Length > MaximumBytes ? FileMode.Create : FileMode.Append;
            using var file = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
            file.Write(line);
        }
    }

    private static void AssertOrdinaryPath(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new IOException("Diagnostic path contains a link.");
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Diagnostic path contains a link.");
        }
    }
}
