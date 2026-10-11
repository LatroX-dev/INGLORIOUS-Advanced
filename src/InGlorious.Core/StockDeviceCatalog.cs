using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace InGlorious.Core;

public enum StockMatchConfidence
{
    CatalogModel,
    AmbiguousRevision
}

/// <summary>
/// Vendor catalog identification, not proof of installed firmware, MCU, or device identity.
/// Names are CORE labels, not required USB product strings. Every match still needs
/// an independently verified hardware/backup path before any firmware operation.
/// </summary>
public sealed record StockDeviceMatch
{
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string CoreCatalogName { get; }
    public IReadOnlyList<string> CandidateDeviceIds { get; }
    public bool IsModelUnambiguous => CandidateDeviceIds.Count == 1;
    public StockMatchConfidence Confidence => IsModelUnambiguous
        ? StockMatchConfidence.CatalogModel : StockMatchConfidence.AmbiguousRevision;
    public bool RequiresHardwareVerification => true;
    public ushort ConfigurationUsagePage => 0xFF01;
    public ushort ConfigurationUsage => 0x0001;
    public ushort NotificationUsagePage => 0xFF00;
    public ushort NotificationUsage => 0xFF00;

    internal StockDeviceMatch(ushort vendorId, ushort productId, string coreCatalogName, params string[] candidates)
    {
        VendorId = vendorId;
        ProductId = productId;
        CoreCatalogName = coreCatalogName;
        CandidateDeviceIds = Array.AsReadOnly((string[])candidates.Clone());
    }
}

/// <summary>
/// Exact normal-mode VID/PID declarations extracted from Glorious CORE 2.1.21.
/// Source evidence and limitations: reference/glorious-stock-devices.json.
/// Does not enumerate devices or authorize any device operation.
/// </summary>
public static class StockDeviceCatalog
{
    // Both published QMK PRO revisions reuse 5044/5046. A PID alone must not
    // select the STM32 image over the incompatible WB32 image.
    public static IReadOnlyList<StockDeviceMatch> All { get; } = Array.AsReadOnly(new[]
    {
        new StockDeviceMatch(0x320F, 0x5044, "GMMK PRO ANSI", "pro-ansi", "pro-rev2-ansi"),
        new StockDeviceMatch(0x320F, 0x5046, "GMMK PRO ISO", "pro-iso", "pro-rev2-iso"),
        new StockDeviceMatch(0x320F, 0x5092, "GMMK PRO ANSI", "pro-rev2-ansi"),
        new StockDeviceMatch(0x320F, 0x5093, "GMMK PRO ISO", "pro-rev2-iso"),
        new StockDeviceMatch(0x320F, 0x5088, "GMMK Numpad", "numpad"),
        new StockDeviceMatch(0x320F, 0x5045, "GMMK V2 65 ANSI", "gmmk2-65-ansi"),
        new StockDeviceMatch(0x320F, 0x504A, "GMMK V2 65 ISO", "gmmk2-65-iso"),
        new StockDeviceMatch(0x320F, 0x504B, "GMMK V2 96 ANSI", "gmmk2-96-ansi"),
        new StockDeviceMatch(0x320F, 0x505A, "GMMK V2 96 ISO", "gmmk2-96-iso"),
        new StockDeviceMatch(0x342D, 0xE3CA, "GMMK 3 65 Wired ANSI", "gmmk3-65-ansi"),
        new StockDeviceMatch(0x342D, 0xE3D3, "GMMK 3 65 Wired ISO", "gmmk3-65-iso"),
        new StockDeviceMatch(0x342D, 0xE3C9, "GMMK 3 75 Wired ANSI", "gmmk3-75-ansi"),
        new StockDeviceMatch(0x342D, 0xE3D2, "GMMK 3 75 Wired ISO", "gmmk3-75-iso"),
        new StockDeviceMatch(0x342D, 0xE3C8, "GMMK 3 100 Wired ANSI", "gmmk3-100-ansi"),
        new StockDeviceMatch(0x342D, 0xE3D1, "GMMK 3 100 Wired ISO", "gmmk3-100-iso")
    });

    public static StockDeviceMatch? Match(ushort vid, ushort pid) =>
        All.FirstOrDefault(entry => entry.VendorId == vid && entry.ProductId == pid);
}
