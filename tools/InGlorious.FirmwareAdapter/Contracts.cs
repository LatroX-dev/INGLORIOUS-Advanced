using System.Text.Json;
using System.Text.Json.Serialization;

namespace InGlorious.FirmwareAdapter;

internal static class Json
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(Files.ReadBounded(path, 1024 * 1024), Options) ?? throw new InvalidDataException("Null JSON document.");
}

internal sealed record Selection(ushort Vid, ushort Pid, byte Bus, string PortPath, string? Serial = null, string? Transport = null);
internal sealed record AdapterRequest(int Schema, string Operation, string SessionDirectory,
    string? DeviceId = null, Selection? Selection = null, string? ExpectedFingerprintSha256 = null,
    string? BundlePath = null, string? BackupPath = null, bool ConfirmWrite = false,
    string? ExpectedBackupSha256 = null, Guid? ExpectedInstallationId = null,
    string? ExpectedCurrentSha256 = null);
internal sealed record Fingerprint(string Transport, ushort Vid, ushort Pid, byte Bus, string PortPath,
    string Serial, byte Configuration, byte Interface, byte Alternate, ushort DeviceVersion,
    uint? ChipId, byte? BootVersion, uint FlashBase, int FlashBytes, int SramBytes, string? MemoryMap,
    string? UniqueId, string IdentityStrength);
internal sealed record Artifact(string Path, long Bytes, string Sha256, string Kind);
internal sealed record ImageSpec(string Path, int Bytes, string Sha256);
internal sealed record Bundle(int Schema, string DeviceId, int BoardId, string Transport, uint FlashBase,
    int FlashBytes, int SramBytes, ImageSpec Image, string ReleaseId);
internal sealed record CaptureManifest(int Schema, string DeviceId, Fingerprint Fingerprint, string FingerprintSha256,
    Artifact[] Artifacts, string Scope, string[] ExcludedRegions, bool RestoreTested = false);
internal sealed record AdapterResponse(int Schema, string Operation, string SessionId, string Status,
    string? ErrorCode, string? DeviceId, Fingerprint? Fingerprint, string? FingerprintSha256,
    bool CanCapture, bool CanInstall, bool CanRestore, bool WriteAttempted, bool Verified,
    string EvidenceDirectory, Artifact[] Artifacts, Selection[]? Devices = null, string? Error = null,
    Guid? InstallationId = null, string[]? CompatibleDeviceIds = null);

internal sealed record Model(string DeviceId, int BoardId, string Transport, int FlashBytes, int SramBytes)
{
    internal const uint FlashBase = 0x08000000;
    internal static Model Get(string? id)
    {
        string[] ids = ["pro-iso", "numpad", "pro-ansi", "pro-rev2-ansi", "pro-rev2-iso",
            "gmmk2-65-ansi", "gmmk2-65-iso", "gmmk2-96-ansi", "gmmk2-96-iso",
            "gmmk3-65-ansi", "gmmk3-65-iso", "gmmk3-75-ansi", "gmmk3-75-iso", "gmmk3-100-ansi", "gmmk3-100-iso"];
        int index = Array.IndexOf(ids, id);
        if (index < 0) throw new InvalidDataException("Unknown model; no device will be opened.");
        bool stm = index is 0 or 2;
        return new(ids[index], index + 1, stm ? "stm32-dfuse" : "wb32-dfu", stm ? 262144 : 131072, stm ? 40960 : 28672);
    }
}

internal interface IDevice : IDisposable
{
    Fingerprint Probe(Model model);
    byte[] ReadAll(Model model);
    void WriteAll(Model model, byte[] image, Action beforeFirstWrite);
    object[] Log { get; }
}
internal interface IBackend : IDisposable
{
    Selection[] List();
    IDevice Open(Selection selection, Model model, string evidenceDirectory);
}
