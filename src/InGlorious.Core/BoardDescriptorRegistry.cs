using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;

namespace InGlorious.Core;

/// <summary>A reviewed wire layout, not permission to flash a device or evidence of its PCB revision.</summary>
public sealed record BoardDescriptor(string DeviceId, byte BoardId, int Rows, int Cols, int LedCount,
    int LegacySize, ushort? StockProductId, ushort RuntimeProductId, string ProductSuffix,
    bool IsExperimental = false, bool HasEncoder = true, bool HasSlider = false, int PayloadCapacity = 7136,
    bool IsEngineeringOnly = false, int FlashBytes = 131072, int SramBytes = 28672)
{
    public string RuntimeName => "INGLORIOUS" + ProductSuffix;
    public bool HasApprovedFirmwareManagement => !IsExperimental && !IsEngineeringOnly;
    public byte LightingTestPattern => HasSlider ? (byte)2 : (byte)1;
}

/// <summary>Explicit protocol identities. Experimental runtime support is not a stock-device identification or permission to flash.</summary>
public static class BoardDescriptorRegistry
{
    public const ushort StockVendorId = 0x320F;
    public const ushort RuntimeVendorId = 0xFEED;
    public static IReadOnlyList<BoardDescriptor> All { get; } = Array.AsReadOnly<BoardDescriptor>([
        new("pro-iso", 1, 11, 8, 99, 5140, 0x5046, 0x1C01, " PRO ISO", FlashBytes: 262144, SramBytes: 40960),
        new("numpad", 2, 5, 4, 31, 3780, 0x5088, 0x1C02, " Numpad", HasSlider: true),
        new("gmmk3-pro-100-iso", 16, 14, 8, 126, 5656, null, 0x1C10, " GMMK3 PRO 100 ISO",
            PayloadCapacity: 7648, FlashBytes: 262144, SramBytes: 36864)
    ]);
    public static IReadOnlyList<BoardDescriptor> Experimental { get; } = Array.AsReadOnly<BoardDescriptor>([
        new("pro-ansi", 3, 11, 8, 98, 5128, null, 0x1C03, " PRO ANSI", IsExperimental: true, FlashBytes: 262144, SramBytes: 40960),
        new("pro-rev2-ansi", 4, 11, 8, 98, 5128, null, 0x1C04, " PRO Rev2 ANSI", IsExperimental: true),
        new("pro-rev2-iso", 5, 11, 8, 99, 5140, null, 0x1C05, " PRO Rev2 ISO", IsExperimental: true),
        new("gmmk2-65-ansi", 6, 9, 8, 87, 4868, null, 0x1C06, " GMMK2 65 ANSI", IsExperimental: true, HasEncoder: false),
        new("gmmk2-65-iso", 7, 9, 8, 88, 4880, null, 0x1C07, " GMMK2 65 ISO", IsExperimental: true, HasEncoder: false),
        new("gmmk2-96-ansi", 8, 14, 8, 119, 5572, null, 0x1C08, " GMMK2 96 ANSI", IsExperimental: true, HasEncoder: false),
        new("gmmk2-96-iso", 9, 14, 8, 120, 5584, null, 0x1C09, " GMMK2 96 ISO", IsExperimental: true, HasEncoder: false),
        new("gmmk3-65-ansi", 10, 14, 8, 85, 5164, null, 0x1C0A, " GMMK3 65 ANSI", IsExperimental: true, PayloadCapacity: 7648),
        new("gmmk3-65-iso", 11, 14, 8, 86, 5176, null, 0x1C0B, " GMMK3 65 ISO", IsExperimental: true, PayloadCapacity: 7648),
        new("gmmk3-75-ansi", 12, 14, 8, 101, 5356, null, 0x1C0C, " GMMK3 75 ANSI", IsExperimental: true, PayloadCapacity: 7648),
        new("gmmk3-75-iso", 13, 14, 8, 102, 5368, null, 0x1C0D, " GMMK3 75 ISO", IsExperimental: true, PayloadCapacity: 7648),
        new("gmmk3-100-ansi", 14, 14, 8, 125, 5644, null, 0x1C0E, " GMMK3 100 ANSI", IsExperimental: true, PayloadCapacity: 7648),
        new("gmmk3-100-iso", 15, 14, 8, 126, 5656, null, 0x1C0F, " GMMK3 100 ISO", IsExperimental: true, PayloadCapacity: 7648)
    ]);
    public static IReadOnlyList<BoardDescriptor> ProtocolBoards { get; } = Array.AsReadOnly(All.Concat(Experimental).OrderBy(board => board.BoardId).ToArray());
    public static IReadOnlyList<BoardDescriptor> EngineeringOnly { get; } = Array.Empty<BoardDescriptor>();
    public static IReadOnlyList<BoardDescriptor> KnownBoards { get; } = Array.AsReadOnly(ProtocolBoards.Concat(EngineeringOnly).ToArray());
    public static bool TryGet(string? deviceId, [NotNullWhen(true)] out BoardDescriptor? board)
    { board = KnownBoards.FirstOrDefault(item => item.DeviceId == deviceId); return board is not null; }
    public static BoardDescriptor Get(string deviceId) => TryGet(deviceId, out var board) ? board : throw new InvalidDataException("Unbekanntes Firmware-Gerät.");
    public static BoardDescriptor GetByBoardId(byte boardId) => KnownBoards.FirstOrDefault(item => item.BoardId == boardId) ?? throw new InvalidDataException("Unbekannte Platine.");
    public static bool TryGetRuntimeUsb(int vendorId, int productId, [NotNullWhen(true)] out BoardDescriptor? board)
    { board = vendorId == RuntimeVendorId ? KnownBoards.FirstOrDefault(item => item.RuntimeProductId == productId) : null; return board is not null; }
    public static bool TryGetStockUsb(int vendorId, int productId, [NotNullWhen(true)] out BoardDescriptor? board)
    { board = vendorId == StockVendorId ? All.FirstOrDefault(item => item.StockProductId == productId) : null; return board is not null; }
    public static bool TryGetRuntimeHidPath(string? path, [NotNullWhen(true)] out BoardDescriptor? board, bool allowEngineering = false)
    {
        board = null;
        if (path is null || path.Length > 4096) return false;
        var match = Regex.Match(path, @"\A\\\\\?\\hid#vid_(?<vid>[0-9a-f]{4})&pid_(?<pid>[0-9a-f]{4})(?:&|#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && TryGetRuntimeUsb(Convert.ToUInt16(match.Groups["vid"].Value, 16), Convert.ToUInt16(match.Groups["pid"].Value, 16), out board) &&
            (!board.IsEngineeringOnly || EngineeringConfigurationPolicy.IsEnabled(board.DeviceId, allowEngineering));
    }
}

/// <summary>Explicit local configuration testing only; never enables public setup or firmware management.</summary>
public static class EngineeringConfigurationPolicy
{
    public const string DeviceId = "gmmk3-pro-100-iso";
    public const string RuntimeVersion = "0.11.2";
    public const uint RuntimeBuildId = 0x000B0201;
    public static bool IsEnabled(string deviceId, bool allowEngineering) => allowEngineering && deviceId == DeviceId;
    public static bool MatchesRuntime(string? version, uint buildId) => version == RuntimeVersion && buildId == RuntimeBuildId;
}

