using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace InGlorious.Core;

public record BackupReport(string Root, int FileCount, int VerifiedCount, IReadOnlyList<string> Issues, bool HasVerifiedDeviceDump)
{
    public int MatchingInternalFlashCaptures { get; init; }
}

public static class BackupService
{
    public static Task<BackupReport> InspectAsync() => Task.Run(() => Inspect(Path.Combine(Paths.WorkspaceRoot, "backups")));
    public static BackupReport Inspect(string root)
    {
        var issues = new List<string>();
        var count = 0;
        var verified = 0;
        var matchingCaptures = 0;
        if (!Directory.Exists(root)) return new(root, 0, 0, ["Noch keine gesicherten Dateien vorhanden.", "Vollständiges Geräte-Firmwarebackup fehlt."], false);
        foreach (var manifestPath in Directory.EnumerateFiles(root, "*.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(p => Path.GetFileName(p) is "manifest.json" or "sources.sha256.json"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var isPackage = Path.GetFileName(manifestPath) == "sources.sha256.json";
                var entries = isPackage ? json.RootElement : json.RootElement.GetProperty("artifacts");
                var manifestRoot = Path.GetDirectoryName(manifestPath)!;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var verifiedEntries = new List<(string Name, long Bytes, string Hash)>();
                foreach (var entry in entries.EnumerateArray())
                {
                    count++;
                    var relative = entry.GetProperty(isPackage ? "name" : "path").GetString()!;
                    try
                    {
                        var fullPath = ResolveInside(manifestRoot, relative);
                        if (!seen.Add(fullPath)) throw new InvalidDataException("Doppelter Manifest-Eintrag.");
                        var expectedSize = entry.GetProperty("bytes").GetInt64();
                        var expectedHash = entry.GetProperty("sha256").GetString();
                        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (stream.Length != expectedSize) throw new InvalidDataException("Dateigröße stimmt nicht überein.");
                        var actual = Convert.ToHexString(SHA256.HashData(stream));
                        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 stimmt nicht überein.");
                        verified++;
                        verifiedEntries.Add((relative, expectedSize, actual));
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
                    { issues.Add($"{relative}: {e.Message}"); }
                }
                if (!isPackage && json.RootElement.TryGetProperty("captureStatus", out var status) && status.GetString() == "two-matching-internal-flash-reads-not-restore-tested"
                    && entries.GetArrayLength() == 2 && verifiedEntries.Count == 2
                    && verifiedEntries[0].Bytes >= 32768 && verifiedEntries[0].Bytes == verifiedEntries[1].Bytes
                    && verifiedEntries[0].Hash == verifiedEntries[1].Hash
                    && json.RootElement.TryGetProperty("range", out var range) && range.GetProperty("start").GetString() == "0x08000000"
                    && range.GetProperty("bytes").GetInt64() == verifiedEntries[0].Bytes)
                {
                    matchingCaptures++;
                    issues.Add(L.F($"Zwei übereinstimmende interne Sicherungen: {Path.GetFileName(manifestRoot)} · {verifiedEntries[0].Bytes:N0} Bytes."));
                }
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException or FormatException or OverflowException)
            { issues.Add($"Manifest {Path.GetFileName(manifestPath)}: {e.Message}"); }
        }
        if (count == 0) issues.Add("Keine prüfbaren Backup-Manifeste gefunden.");
        issues.Add("Paketkopien und PC-Profile sind kein vollständiges Firmwarebackup des Geräts.");
        // A JSON flag, file name or successful SHA hash is never evidence of a complete device dump.
        return new(root, count, verified, issues, false) { MatchingInternalFlashCaptures = matchingCaptures };
    }

    public static string ResolveInside(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Ungültiger Pfad im Manifest.");
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Datei liegt außerhalb des Backup-Ordners.");
        for (var item = new FileInfo(full) as FileSystemInfo; item != null && item.FullName.Length >= fullRoot.Length - 1; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Verknüpfungen in Backup-Pfaden sind nicht erlaubt.");
        return full;
    }
}
