using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InGlorious.Core;

/// <summary>Copies completed firmware evidence. This class never accesses a device or changes its source.</summary>
public sealed class FirmwareEvidenceStore
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INGLORIOUS Advanced", "Firmware");
    private readonly string _root;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public FirmwareEvidenceStore(string root) => _root = CanonicalPath(root);

    /// <summary>Returns only after every copied file has been reopened and hash-verified. On error the source remains intact; a partial destination is retained for inspection.</summary>
    public string Archive(string sourceDirectory, string deviceId, string operation, bool verified)
    {
        var model = deviceId switch { "pro-iso" => "GMMK PRO ISO", "numpad" => "GMMK Numpad", _ => throw new ArgumentException("Unbekanntes Gerätemodell.", nameof(deviceId)) };
        var action = operation switch { "Update" => "Update", "Install" => "Installation", "Restore" => "Wiederherstellung", _ => throw new ArgumentException("Unbekannter Firmware-Vorgang.", nameof(operation)) };
        var source = CanonicalPath(sourceDirectory);
        if (Contains(_root, source) || Contains(source, _root)) throw new IOException("Quell- und Archivordner dürfen sich nicht überschneiden.");
        CheckAncestors(source);
        CheckAncestors(_root);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Der Quellordner mit den Prüfdateien fehlt.");
        var entries = Inventory(source);
        if (!entries.Any(entry => !entry.Directory)) throw new InvalidDataException("Der Quellordner enthält keine Prüfdateien.");

        // One store-wide, exclusive file handle also prevents two instances choosing the same readable folder name.
        // The lock file is retained and never truncated. It contains no user data.
        Directory.CreateDirectory(_root);
        CheckAncestors(_root);
        var lockPath = Path.Combine(_root, ".archive.lock");
        CheckAncestors(lockPath);
        using var reservation = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckAncestors(lockPath);
        var parent = Path.Combine(_root, model);
        CheckAncestors(parent);
        Directory.CreateDirectory(parent);
        CheckAncestors(parent);
        var now = DateTimeOffset.Now;
        var stem = now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + " - " + action;
        var destination = Path.Combine(parent, stem);
        for (var suffix = 2; Path.Exists(destination); suffix++) destination = Path.Combine(parent, stem + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")");
        CheckAncestors(destination);
        Directory.CreateDirectory(destination);
        CheckAncestors(destination);
        var originals = Path.Combine(destination, "Originaldateien");
        Directory.CreateDirectory(originals);
        var files = new List<ArchivedFile>();

        foreach (var entry in entries)
        {
            var input = Child(source, entry.Path);
            var output = Child(originals, entry.Path);
            CheckAncestors(input);
            CheckAncestors(output);
            if (entry.Directory) { Directory.CreateDirectory(output); continue; }
            using var read = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            CheckAncestors(input);
            if (read.Length != entry.Bytes) throw new IOException("Eine Prüfdatei wurde während der Archivierung geändert.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var write = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = read.Read(buffer)) != 0) { hash.AppendData(buffer, 0, count); write.Write(buffer, 0, count); }
                write.Flush(flushToDisk: true);
            }
            var sha = Convert.ToHexString(hash.GetHashAndReset());
            if (read.Position != entry.Bytes || read.Length != entry.Bytes) throw new IOException("Die Länge einer Prüfdatei wurde während der Archivierung geändert.");
            VerifyFile(output, entry.Bytes, sha);
            files.Add(new(entry.Path.Replace(Path.DirectorySeparatorChar, '/'), entry.Bytes, sha));
        }

        // A source may be a freshly completed run directory. Reject additions/removals or changed bytes during copying.
        var finalEntries = Inventory(source);
        if (!entries.SequenceEqual(finalEntries)) throw new IOException("Der Quellordner wurde während der Archivierung geändert.");
        foreach (var file in files) VerifyFile(Child(source, file.Path), file.Bytes, file.Sha256);
        var manifest = new { schemaVersion = 1, createdAt = now, deviceId, product = model, operation, firmwareVerified = verified, archiveIntegrityVerified = true, sourceFiles = "Originaldateien", files };
        WriteNew(Path.Combine(destination, "Archivmanifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        var report = $"INGLORIOUS Advanced – Firmware-Prüfdateien\n\nGerät: {model}\nVorgang: {action}\nDatum: {now:dd.MM.yyyy HH:mm:ss zzz}\nFirmwareprüfung: {(verified ? "Vollständiger Rücklesevergleich bestätigt." : "Ergebnis nicht bestätigt. Details stehen in den Originaldateien.")}\n\nArchiv: {files.Count} Dateien kopiert und vollständig mit SHA-256 geprüft.\nDie unveränderten Originaldateien liegen im Unterordner Originaldateien.\nDateinamen, Dateigrößen und Prüfsummen stehen in Archivmanifest.json.\nDie Quelldateien wurden weder verschoben noch gelöscht.\n\nDie Archivprüfung bestätigt die Dateikopien. Sie bestätigt keinen normalen Gerätestart,\nkeine Tasten- oder Beleuchtungsprüfung und keinen praktisch durchgeführten Wiederherstellungstest.\n";
        WriteNew(Path.Combine(destination, "Bericht.txt"), report);
        return destination;
    }

    private sealed record Entry(string Path, bool Directory, long Bytes);
    private sealed record ArchivedFile(string Path, long Bytes, string Sha256);

    private static List<Entry> Inventory(string root)
    {
        var result = new List<Entry>();
        void Visit(string directory)
        {
            CheckAncestors(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(value => value, PathComparer))
            {
                CheckAncestors(path);
                var relative = Path.GetRelativePath(root, path);
                _ = Child(root, relative);
                var isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                result.Add(new(relative, isDirectory, isDirectory ? 0 : new FileInfo(path).Length));
                if (isDirectory) Visit(path);
            }
        }
        Visit(root);
        return result;
    }

    private static void VerifyFile(string path, long length, string sha)
    {
        CheckAncestors(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckAncestors(path);
        if (stream.Length != length || Convert.ToHexString(SHA256.HashData(stream)) != sha) throw new IOException("Eine archivierte Prüfdatei stimmt nicht mit der Quelle überein.");
    }

    private static void WriteNew(string path, string text)
    {
        CheckAncestors(path);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        VerifyFile(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Ein vollständiger Ordnerpfad ist erforderlich.");
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or "..")) throw new ArgumentException("Relative Pfadbestandteile sind nicht zulässig.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static bool Contains(string parent, string child) => string.Equals(parent, child, PathComparison) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);
    private static string Child(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is "." or "..")) throw new IOException("Ungültiger relativer Dateipfad.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (string.Equals(root, path, PathComparison) || !Contains(root, path)) throw new IOException("Eine Prüfdatei liegt außerhalb des erlaubten Ordners.");
        return path;
    }
    private static void CheckAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Verknüpfungen und Umleitungspunkte sind im Archivpfad nicht zulässig.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
