using System.Collections;
using System.Reflection;
using System.Text.Json;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}
void RejectMutation(Action action, string message)
{
    try { action(); }
    catch (NotSupportedException) { checks++; return; }
    throw new InvalidOperationException(message);
}

using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("stock-evidence.json")!;
using var evidence = JsonDocument.Parse(stream);
var rows = evidence.RootElement.GetProperty("devices").EnumerateArray().ToArray();
Check(StockDeviceCatalog.All.Count == 15 && rows.Length == 15, "Exactly the known fifteen normal USB declarations.");
Check(StockDeviceCatalog.All.Select(x => (x.VendorId, x.ProductId)).Distinct().Count() == 15, "Normal identifiers must be unique.");
foreach (var row in rows)
{
    var vid = Convert.ToUInt16(row.GetProperty("vid").GetString(), 16);
    var pid = Convert.ToUInt16(row.GetProperty("pid").GetString(), 16);
    var item = StockDeviceCatalog.Match(vid, pid);
    Check(item is not null, "Every source-backed pair has a match.");
    Check(item!.CoreCatalogName == row.GetProperty("coreCatalogName").GetString(), "Name remains a CORE label.");
    Check(item.CandidateDeviceIds.SequenceEqual(row.GetProperty("candidateDeviceIds").EnumerateArray().Select(x => x.GetString()!)), "Candidates match the evidence.");
    Check(item.RequiresHardwareVerification && row.GetProperty("requiresHardwareVerification").GetBoolean(), "No catalog match grants a write path.");
    Check(!row.GetProperty("firmwareWriteAuthorized").GetBoolean(), "Evidence must not imply firmware authorization.");
    Check(row.GetProperty("exactUsbProductString").ValueKind == JsonValueKind.Null, "Unmeasured product strings are not invented.");
    Check(row.GetProperty("bcdDeviceIdentityRule").ValueKind == JsonValueKind.Null, "Updater versions are not silicon identification.");
    Check(item.ConfigurationUsagePage == 0xFF01 && item.ConfigurationUsage == 1 && item.NotificationUsagePage == 0xFF00 && item.NotificationUsage == 0xFF00, "CORE collection usages remain exact.");
    Check(item.IsModelUnambiguous == (item.CandidateDeviceIds.Count == 1), "Revision ambiguity is explicit.");
    RejectMutation(() => ((IList<string>)item.CandidateDeviceIds)[0] = "foreign", "Candidate list may not be modified.");
}
Check(StockDeviceCatalog.Match(0x320F, 0x5044)!.CandidateDeviceIds.SequenceEqual(new[] { "pro-ansi", "pro-rev2-ansi" }), "Reused ANSI PID must not choose STM32/WB32.");
Check(StockDeviceCatalog.Match(0x320F, 0x5046)!.CandidateDeviceIds.SequenceEqual(new[] { "pro-iso", "pro-rev2-iso" }), "Reused ISO PID must not choose STM32/WB32.");
Check(StockDeviceCatalog.Match(0x320F, 0x5044)!.Confidence == StockMatchConfidence.AmbiguousRevision, "Ambiguous confidence is preserved.");
Check(StockDeviceCatalog.Match(0x320F, 0x5092)!.CandidateDeviceIds.Single() == "pro-rev2-ansi", "Official WB updater maps ALT to the WB candidate.");
Check(StockDeviceCatalog.Match(0x320F, 0x5093)!.CandidateDeviceIds.Single() == "pro-rev2-iso", "ISO ALT does not impersonate proven rev1.");
Check(StockDeviceCatalog.All.SelectMany(x => x.CandidateDeviceIds).Distinct().Count() == 15, "Both existing devices and thirteen experimental models are represented.");
foreach (var (vid, pid) in new (ushort, ushort)[]
{
    (0,0), (0x504B,0x320F), (0x342D,0xE3C5), (0x342D,0xE3C6), (0x342D,0xE3C7),
    (0x342D,0xE3CE), (0x342D,0xE3CF), (0x342D,0xE3D0), // PRO must not alias generic GMMK3.
    (0x342D,0xE3DD), (0x342D,0xE3DE), (0x342D,0xE3DF), // HE excluded.
    (0x342D,0xE3CB), (0x342D,0xE3CC), (0x342D,0xE3CD), // Wireless excluded.
    (0x2442,0xB051), (0x2442,0xB052), (0x2442,0xB053), (0x342D,0xDFA0), (0x0483,0xDF11),
    (0x320F,0xB007), (0x320F,0xB00F), (0x3794,0xB000), (0x3794,0xB010),
    (0xFEED,0x1C01), (0xFEED,0x1C0F), (0x320F,0xE3CA), (0x342D,0x5044)
}) Check(StockDeviceCatalog.Match(vid,pid) is null, "Unknown, wrong vendor, bootloader and unrelated variants must fail closed.");
RejectMutation(() => ((IList<StockDeviceMatch>)StockDeviceCatalog.All).Clear(), "Catalog may not be modified.");
Check(StockDeviceCatalog.Match(0x342D,0xE3CA)!.CandidateDeviceIds.Single() == "gmmk3-65-ansi", "Failed mutations did not change a future lookup.");
Console.WriteLine($"PASS: {checks} stock catalog checks; pure metadata only, no device access.");
