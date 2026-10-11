using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InGlorious.FirmwareAdapter;

internal sealed class NativeBackend : IBackend
{
    private readonly NativeUsb usb = new();
    public Selection[] List() => usb.Devices.Select(d => new Selection(d.Vendor, d.Product, d.Bus, d.PortPath,
        string.IsNullOrEmpty(d.Serial) ? null : d.Serial, d.Vendor == 0x0483 ? "stm32-dfuse" : "wb32-dfu")).ToArray();
    public IDevice Open(Selection selection, Model model, string evidenceDirectory)
    {
        if (selection.Transport != model.Transport) throw new InvalidDataException("MODEL_TRANSPORT_MISMATCH");
        if (model.Transport == "wb32-dfu") return usb.OpenSelected(selection);
        var matches = List().Where(d => d == selection).ToArray();
        if (matches.Length != 1) throw new IOException("SELECTED_DEVICE_NOT_UNIQUE");
        return new Stm32Device(selection, evidenceDirectory);
    }
    public void Dispose() => usb.Dispose();
}

internal sealed class Stm32Device : IDevice
{
    internal const string ToolHash = "662CC5C1C1992D0779C2F4201AE4770D60C6F56D8974CA71ABDCF35927B9F87D";
    private readonly Selection selection;
    private readonly string evidence;
    private readonly string tool;
    private readonly FileStream toolLock;
    private readonly List<object> commands = [];
    private int sequence;
    private Fingerprint? initial;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    public object[] Log => commands.ToArray();
    internal Stm32Device(Selection selection, string evidence)
    {
        if (selection.Vid != 0x0483 || selection.Pid != 0xDF11 || (selection.Bus == 0 && !OperatingSystem.IsMacOS()) ||
            !Regex.IsMatch(selection.PortPath, "^[1-9][0-9]{0,2}(?:\\.[1-9][0-9]{0,2}){0,7}$", RegexOptions.CultureInvariant) ||
            selection.PortPath.Split('.').Any(part => !byte.TryParse(part, out byte number) || number == 0) ||
            !Regex.IsMatch(selection.Serial ?? "", "^[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("STM_SELECTION_INVALID");
        this.selection = selection; this.evidence = evidence;
        var native = NativeToolCatalog.Verify(NativeToolCatalog.DfuName);
        tool = native.Path;
        toolLock = Files.Lock(tool, native.Bytes, native.Sha256).Stream;
    }
    internal string[] Arguments(string operation, string? file = null)
    {
        string[] prefix = ["-d", "0483:df11", "-S", selection.Serial!, "-p", selection.Bus + "-" + selection.PortPath, "-c", "1", "-i", "0", "-a", "0"];
        return operation switch
        {
            "list" => [.. prefix, "-l"],
            "read" when file != null => [.. prefix, "-s", "0x08000000:262144", "-U", file],
            "write" when file != null => [.. prefix, "-s", "0x08000000", "-D", file],
            _ => throw new InvalidDataException("STM_COMMAND_REJECTED")
        };
    }
    internal static Fingerprint ParseListing(string output, Selection selected, Model model)
    {
        if (model.Transport != "stm32-dfuse" || model.FlashBytes != 262144) throw new InvalidDataException("MODEL_GEOMETRY_MISMATCH");
        string pattern = "Found DFU: \\[0483:df11\\] ver=([0-9a-fA-F]{4}), devnum=[0-9]+, cfg=1, intf=0, path=\"([^\"]+)\", alt=0, name=\"([^\"]+)\", serial=\"([^\"]+)\"";
        var matches = Regex.Matches(output, pattern, RegexOptions.CultureInvariant).Cast<Match>().Where(m => m.Groups[2].Value == selected.Bus + "-" + selected.PortPath && m.Groups[4].Value == selected.Serial).ToArray();
        if (matches.Length != 1) throw new IOException("STM_DESCRIPTOR_NOT_UNIQUE");
        var found = matches[0]; string map = found.Groups[3].Value;
        ValidateMap(map);
        return new("stm32-dfuse", 0x0483, 0xDF11, selected.Bus, selected.PortPath, selected.Serial!, 1, 0, 0,
            ushort.Parse(found.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), null, null,
            Model.FlashBase, model.FlashBytes, model.SramBytes, map, null, "usb-serial-and-port");
    }
    internal static void ValidateMap(string map)
    {
        // Bootloader labels may pad the space before the address. Accept only ASCII
        // spaces there; the address, geometry, permissions and full input stay bound.
        var match = Regex.Match(map, "\\A@Internal Flash[ ]+/0x08000000/([0-9]+)\\*([0-9]+)([KBM])g\\z", RegexOptions.CultureInvariant);
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, out uint count) || !uint.TryParse(match.Groups[2].Value, out uint size)) throw new InvalidDataException("STM_MEMORY_MAP_UNSUPPORTED");
        uint unit = match.Groups[3].Value switch { "B" => 1, "K" => 1024, "M" => 1048576, _ => 0 };
        if (count != 128 || (ulong)size * unit != 2048 || (ulong)count * size * unit != 262144) throw new InvalidDataException("STM_MEMORY_MAP_UNSUPPORTED");
    }
    public Fingerprint Probe(Model model)
    {
        var current = ParseListing(Run("list"), selection, model);
        if (initial != null && initial != current) throw new IOException("STM_IDENTITY_CHANGED");
        return initial = current;
    }
    public byte[] ReadAll(Model model)
    {
        _ = Probe(model);
        string file = Files.Child(evidence, "dfu-read-" + sequence + ".bin");
        if (File.Exists(file)) throw new IOException("READ_TARGET_EXISTS");
        Run("read", file);
        using (var flush = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { flush.Flush(true); }
        byte[] result = Files.ReadBounded(file, model.FlashBytes);
        if (result.Length != model.FlashBytes) throw new IOException("STM_SHORT_READ");
        return result;
    }
    public void WriteAll(Model model, byte[] image, Action beforeFirstWrite)
    {
        if (model.FlashBytes != 262144 || image.Length != 262144) throw new InvalidDataException("STM_FULL_IMAGE_REQUIRED");
        _ = Probe(model);
        Artifact artifact = Files.New(evidence, "dfu-write-image.bin", image, "exact-write-image");
        using var imageLock = Files.Lock(Files.Child(evidence, artifact.Path), artifact.Bytes, artifact.Sha256).Stream;
        Run("write", Files.Child(evidence, artifact.Path), beforeFirstWrite);
    }
    private string Run(string operation, string? file = null, Action? beforeFirstWrite = null)
    {
        if (operation == "write" && beforeFirstWrite == null) throw new InvalidOperationException("EXPLICIT_WRITE_GATE_REQUIRED");
        string[] arguments = Arguments(operation, file);
        string name = "dfu-" + (++sequence).ToString("D3") + "-" + operation;
        if (elapsed.ElapsedMilliseconds >= 300000) throw new TimeoutException("OPERATION_TIMEOUT");
        var request = new { executable = tool, arguments, write = operation == "write", utc = DateTimeOffset.UtcNow };
        Files.New(evidence, name + ".request.json", Json.Bytes(request), "tool-request");
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["LC_ALL"] = "C";
        foreach (string arg in arguments) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        string stdout = "", stderr = ""; int? exit = null; bool timeout = false; string? error = null;
        Task<string>? outTask = null, errTask = null;
        try
        {
            beforeFirstWrite?.Invoke();
            if (!process.Start()) throw new IOException("TOOL_START_FAILED");
            outTask = process.StandardOutput.ReadToEndAsync(); errTask = process.StandardError.ReadToEndAsync();
            int remaining = Math.Max(1, 300000 - checked((int)elapsed.ElapsedMilliseconds));
            if (!process.WaitForExit(remaining)) { timeout = true; process.Kill(true); process.WaitForExit(); throw new TimeoutException("OPERATION_TIMEOUT"); }
            exit = process.ExitCode;
            stdout = outTask.GetAwaiter().GetResult(); stderr = errTask.GetAwaiter().GetResult();
            if (exit != 0) throw new IOException("DFU_TOOL_FAILED");
            if (stdout.Length + stderr.Length > 1024 * 1024) throw new IOException("DFU_OUTPUT_OVERSIZED");
            return stdout;
        }
        catch (Exception ex) { error = ex.Message; throw; }
        finally
        {
            if (outTask != null) stdout = outTask.GetAwaiter().GetResult();
            if (errTask != null) stderr = errTask.GetAwaiter().GetResult();
            commands.Add(new { request, exitCode = exit, timedOut = timeout, error });
            Files.New(evidence, name + ".stdout.txt", System.Text.Encoding.UTF8.GetBytes(stdout), "raw-tool-output");
            Files.New(evidence, name + ".stderr.txt", System.Text.Encoding.UTF8.GetBytes(stderr), "raw-tool-output");
            Files.New(evidence, name + ".result.json", Json.Bytes(commands[^1]), "tool-result");
        }
    }
    public void Dispose() => toolLock.Dispose();
}
