using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace InGlorious.Core;

public sealed record AppAutostartState(bool Supported, bool Enabled, string? Error = null, string? Notice = null);

// Only explicit settings/installer actions change registration. Reading settings never enables it.
public static class AppAutostart
{
    public const string BackgroundArgument = "--background";
    internal const string EntryName = "INGLORIOUS Advanced";
    internal const string ApplicationId = "org.inglorious.advanced";
    internal const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string WindowsApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string WindowsApprovalNotice = "Prüfe den Autostart zusätzlich in den Windows-Einstellungen unter „Apps → Autostart“.";

    public static AppAutostartState GetState()
    {
        try { var entry = Current(); return new(true, entry.IsEnabled(), Notice: entry.GetApprovalNotice()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.Security.SecurityException or XmlException)
        { return new(false, false, ex.Message); }
    }

    public static void SetEnabled(bool enabled) => Current().SetEnabled(enabled);

    private static AutostartRegistration Current()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No application executable.");
        if (OperatingSystem.IsWindows())
            return new(AutostartPlatform.Windows, executable, null, new WindowsAutostartRunStore(WindowsRunKey, EntryName, WindowsApprovalKey));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(home)) throw new IOException("The user home directory is unavailable.");
        if (OperatingSystem.IsMacOS())
            return new(AutostartPlatform.MacOS, executable, Path.Combine(home, "Library", "LaunchAgents"));
        if (OperatingSystem.IsLinux())
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config)) config = Path.Combine(home, ".config");
            return new(AutostartPlatform.Linux, executable, Path.Combine(config, "autostart"));
        }
        throw new InvalidOperationException("This operating system has no supported login registration.");
    }
}

internal enum AutostartPlatform { Windows, MacOS, Linux }
internal interface IAutostartRunStore
{
    string? Read();
    void Write(string command);
    void Delete();
    object? ReadApproval() => null;
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAutostartRunStore(string keyPath, string name, string? approvalKey = null) : IAutostartRunStore
{
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, false);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        if (value is not string text || key!.GetValueKind(name) != RegistryValueKind.String)
            throw new IOException("The existing startup entry is not an application command.");
        return text;
    }
    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, true);
        key.SetValue(name, command, RegistryValueKind.String);
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
        key?.DeleteValue(name, false);
    }
    public object? ReadApproval()
    {
        // Read only. The operating system's independent choice must survive app upgrades.
        if (approvalKey is null) return null;
        using var key = Registry.CurrentUser.OpenSubKey(approvalKey, false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
}

internal sealed class AutostartRegistration
{
    private readonly AutostartPlatform _platform;
    private readonly string _executable;
    private readonly string? _directory;
    private readonly IAutostartRunStore? _run;
    internal string? EntryPath => _directory is null ? null : Path.Combine(_directory,
        AppAutostart.ApplicationId + (_platform == AutostartPlatform.MacOS ? ".plist" : ".desktop"));

    internal AutostartRegistration(AutostartPlatform platform, string executable, string? directory, IAutostartRunStore? run = null)
    {
        _platform = platform;
        _executable = ValidateExecutable(executable, platform);
        _directory = directory;
        _run = run;
        if (platform == AutostartPlatform.Windows && run is null) throw new ArgumentException("Missing registry store.");
        if (platform != AutostartPlatform.Windows && (directory is null || !Path.IsPathFullyQualified(directory)))
            throw new ArgumentException("Autostart requires an absolute user directory.");
    }

    internal bool IsEnabled()
    {
        if (_platform == AutostartPlatform.Windows)
        {
            var value = _run!.Read();
            if (value is null) return false;
            ValidateWindowsCommand(value);
            return true;
        }
        var content = ReadEntry();
        return content is not null && ValidateEntry(content);
    }

    internal string? GetApprovalNotice()
    {
        if (_platform != AutostartPlatform.Windows || !IsEnabled()) return null;
        object? approval;
        try { approval = _run!.ReadApproval(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable optional OS override must not prevent disabling our known Run entry.
            return AppAutostart.WindowsApprovalNotice;
        }
        // This is not a stable writable API. Accept the known neutral/enabled records;
        // direct other records to Windows, without guessing, deleting or re-enabling them.
        // Independent reference: electron/shell/browser/browser_win.cc, login-item reader.
        if (approval is null || approval is byte[] { Length: 12 } bytes &&
            (bytes[0] is 0 or 2) && bytes.Skip(1).All(value => value == 0)) return null;
        return AppAutostart.WindowsApprovalNotice;
    }

    internal void SetEnabled(bool enabled)
    {
        if (_platform == AutostartPlatform.Windows)
        {
            // Do not replace a command owned by another application at this value name.
            _ = IsEnabled();
            if (enabled)
            {
                RequireExecutable();
                var command = '"' + _executable + "\" " + AppAutostart.BackgroundArgument;
                if (command.Length > 260) throw new IOException("The startup command exceeds the Windows Run limit.");
                _run!.Write(command);
            }
            else _run!.Delete();
            return;
        }
        var before = ReadEntry();
        if (before is not null) _ = ValidateEntry(before);
        if (!enabled)
        {
            if (before is not null) File.Delete(EntryPath!);
            return;
        }
        RequireExecutable();
        AssertNoLinks(_directory!);
        Directory.CreateDirectory(_directory!);
        AssertNoLinks(_directory!);
        var temporary = EntryPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(BuildEntry());
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (ReadEntry() != before) throw new IOException("The startup entry changed while saving.");
            File.Move(temporary, EntryPath!, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void RequireExecutable()
    {
        if (!File.Exists(_executable)) throw new IOException("The application executable is missing.");
        if (_platform == AutostartPlatform.MacOS && _executable.Contains("/AppTranslocation/", StringComparison.Ordinal))
            throw new IOException("Move the app to Applications before enabling login startup.");
    }

    private static string ValidateExecutable(string value, AutostartPlatform platform)
    {
        if (!Path.IsPathFullyQualified(value) || value.Any(char.IsControl) || value.Contains('"'))
            throw new ArgumentException("An absolute application path without control characters is required.");
        if (platform == AutostartPlatform.Linux && value.Contains('='))
            throw new ArgumentException("Desktop entry executable paths cannot contain an equals sign.");
        var name = platform == AutostartPlatform.Windows ? "INGLORIOUS Advanced.exe" : "INGLORIOUS Advanced";
        if (!Path.GetFileName(value).Equals(name, platform == AutostartPlatform.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Autostart is available only from the application executable.");
        return Path.GetFullPath(value);
    }

    private static void ValidateWindowsCommand(string command)
    {
        if (command.Length is < 3 or > 260 || command[0] != '"') throw new IOException("The startup value is not owned by this application.");
        var end = command.IndexOf('"', 1);
        if (end < 0 || (command[(end + 1)..] != "" && command[(end + 1)..] != " " + AppAutostart.BackgroundArgument))
            throw new IOException("The startup value contains unexpected arguments.");
        _ = ValidateExecutable(command[1..end], AutostartPlatform.Windows);
    }

    private string? ReadEntry()
    {
        AssertNoLinks(EntryPath!);
        if (!File.Exists(EntryPath)) return null;
        if (new FileInfo(EntryPath!).Length > 32768) throw new IOException("The startup entry is too large.");
        return File.ReadAllText(EntryPath!);
    }

    private string BuildEntry()
    {
        if (_platform == AutostartPlatform.MacOS)
            return new XDocument(new XDeclaration("1.0", "UTF-8", null),
                new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", AppAutostart.ApplicationId),
                    new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", _executable), new XElement("string", AppAutostart.BackgroundArgument)),
                    new XElement("key", "RunAtLoad"), new XElement("true"),
                    new XElement("key", "KeepAlive"), new XElement("false"),
                    new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua")))).ToString();
        return "[Desktop Entry]\nType=Application\nName=INGLORIOUS Advanced\nExec=" + DesktopCommand(_executable) +
            "\nTerminal=false\nHidden=false\nX-GNOME-Autostart-enabled=true\nX-INGLORIOUS-Managed=true\n";
    }

    // Desktop Entry strings are unescaped before the Exec command is parsed; percent is a field code.
    internal static string DesktopCommand(string executable)
    {
        var quoted = executable.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        return '"' + quoted.Replace("\\", "\\\\") + "\" " + AppAutostart.BackgroundArgument;
    }

    private bool ValidateEntry(string content)
    {
        if (_platform == AutostartPlatform.Linux)
        {
            var lines = content.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
            if (lines.FirstOrDefault() != "[Desktop Entry]" || lines.Skip(1).Any(line => line.StartsWith('['))) throw new IOException("Unknown startup entry.");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines.Skip(1))
            {
                var equal = line.IndexOf('=');
                if (equal <= 0 || !fields.TryAdd(line[..equal], line[(equal + 1)..])) throw new IOException("Invalid startup entry.");
            }
            if (fields.GetValueOrDefault("Type") != "Application" || fields.GetValueOrDefault("X-INGLORIOUS-Managed") != "true" ||
                fields.GetValueOrDefault("Name") != AppAutostart.EntryName || !fields.TryGetValue("Exec", out var exec) ||
                fields.Keys.Any(key => key is not ("Type" or "Name" or "Exec" or "Terminal" or "Hidden" or "X-GNOME-Autostart-enabled" or "X-INGLORIOUS-Managed")) ||
                fields.GetValueOrDefault("Terminal") != "false" || fields.GetValueOrDefault("Hidden") is not ("true" or "false") ||
                fields.GetValueOrDefault("X-GNOME-Autostart-enabled") is not ("true" or "false"))
                throw new IOException("The startup entry is not owned by this application.");
            _ = ValidateExecutable(ParseDesktopCommand(exec), AutostartPlatform.Linux);
            return fields.GetValueOrDefault("Hidden") != "true" && fields.GetValueOrDefault("X-GNOME-Autostart-enabled") != "false";
        }
        using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root;
        var dict = root?.Element("dict");
        if (root?.Name != "plist" || dict is null || root.Elements().Count() != 1) throw new IOException("Invalid login item.");
        var nodes = dict.Elements().ToArray();
        if (nodes.Length % 2 != 0) throw new IOException("Invalid login item.");
        var entries = new Dictionary<string, XElement>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Length; i += 2)
            if (nodes[i].Name != "key" || !entries.TryAdd(nodes[i].Value, nodes[i + 1])) throw new IOException("Invalid login item.");
        if (entries.Keys.Any(key => key is not ("Label" or "ProgramArguments" or "RunAtLoad" or "KeepAlive" or "LimitLoadToSessionType" or "Disabled")) ||
            entries.GetValueOrDefault("Label") is not { Name.LocalName: "string", Value: AppAutostart.ApplicationId } ||
            entries.GetValueOrDefault("RunAtLoad")?.Name is not { } runAtLoad || (runAtLoad != "true" && runAtLoad != "false") ||
            entries.GetValueOrDefault("KeepAlive")?.Name != "false" ||
            entries.GetValueOrDefault("LimitLoadToSessionType") is not { Name.LocalName: "string", Value: "Aqua" } ||
            (entries.TryGetValue("Disabled", out var disabled) && disabled.Name != "true" && disabled.Name != "false") ||
            entries.GetValueOrDefault("ProgramArguments") is not { Name.LocalName: "array" } args)
            throw new IOException("The login item is not owned by this application.");
        var arguments = args.Elements().ToArray();
        if (arguments.Length != 2 || arguments.Any(a => a.Name != "string") || arguments[1].Value != AppAutostart.BackgroundArgument)
            throw new IOException("Unexpected login item arguments.");
        _ = ValidateExecutable(arguments[0].Value, AutostartPlatform.MacOS);
        return entries.GetValueOrDefault("RunAtLoad")?.Name == "true" && entries.GetValueOrDefault("Disabled")?.Name != "true";
    }

    internal static string ParseDesktopCommand(string command)
    {
        var decoded = new StringBuilder();
        for (var i = 0; i < command.Length; i++)
        {
            if (command[i] != '\\') { decoded.Append(command[i]); continue; }
            if (++i >= command.Length || command[i] != '\\') throw new IOException("Invalid desktop string escape.");
            decoded.Append('\\');
        }
        command = decoded.ToString();
        const string suffix = "\" --background";
        if (!command.StartsWith('"') || !command.EndsWith(suffix, StringComparison.Ordinal)) throw new IOException("Unexpected desktop command.");
        var encodedPath = command[1..^suffix.Length];
        decoded.Clear();
        for (var i = 0; i < encodedPath.Length; i++)
        {
            var c = encodedPath[i];
            if (c == '\\')
            {
                if (++i >= encodedPath.Length || !"\\\"`$".Contains(encodedPath[i])) throw new IOException("Invalid desktop command escape.");
                decoded.Append(encodedPath[i]);
            }
            else if (c == '%')
            {
                if (++i >= encodedPath.Length || encodedPath[i] != '%') throw new IOException("Unexpected desktop field code.");
                decoded.Append('%');
            }
            else
            {
                if (c is '"' or '`' or '$') throw new IOException("Unescaped desktop command character.");
                decoded.Append(c);
            }
        }
        return decoded.ToString();
    }

    private static void AssertNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var item = new FileInfo(current);
            if (item.LinkTarget is not null || (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) ||
                (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Autostart does not modify linked paths.");
        }
    }
}
