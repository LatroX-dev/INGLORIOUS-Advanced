namespace InGlorious.Core;

/// <summary>Reviewed physical controls, separate from runtime support and hardware acceptance.</summary>
public sealed record HardwareControls(string DeviceId, bool HasOsSwitch = false, int ProfilePositions = 0,
    bool HasConnectionSwitch = false)
{
    public bool HasAny => HasOsSwitch || ProfilePositions > 0 || HasConnectionSwitch;
    public bool ProfileSwitchSupported => DeviceId == "gmmk3-pro-100-iso";
    public bool WirelessSupported => false;
}

public static class HardwareControlCatalog
{
    public const string MinimumFirmwareVersion = "0.11.9";
    // Keep identities explicit: a retail-name prefix does not establish a shared PCB or switch wiring.
    public static IReadOnlyList<HardwareControls> All { get; } = Array.AsReadOnly<HardwareControls>([
        new("pro-iso"), new("numpad", HasConnectionSwitch: true),
        new("pro-ansi"), new("pro-rev2-ansi"), new("pro-rev2-iso"),
        new("gmmk2-65-ansi"), new("gmmk2-65-iso"), new("gmmk2-96-ansi"), new("gmmk2-96-iso"),
        new("gmmk3-65-ansi", true, 3), new("gmmk3-65-iso", true, 3),
        new("gmmk3-75-ansi", true, 3), new("gmmk3-75-iso", true, 3),
        new("gmmk3-100-ansi", true, 3), new("gmmk3-100-iso", true, 3),
        new("gmmk3-pro-100-iso", true, 3)
    ]);

    public static HardwareControls? Find(string deviceId) => All.FirstOrDefault(item => item.DeviceId == deviceId);
    public static bool HasOsSwitch(string deviceId) => Find(deviceId)?.HasOsSwitch == true;

    public static string OsSwitchText(string deviceId, OnboardHardwareControlStatus? status, bool testStarted)
    {
        if (!HasOsSwitch(deviceId)) return "";
        if (status is null) return L.T(testStarted
            ? "Für den Schaltertest ist ein Firmware-Update erforderlich."
            : "Windows / macOS: Test starten.");
        if (status.DeviceId != deviceId) throw new ArgumentException("Hardware-control status model mismatch.", nameof(status));
        return status.Pending
            ? L.Format("Windows / macOS: {0} → {1}. Lasse alle Tasten los.",
                status.MacActive ? "macOS" : "Windows", status.MacPosition ? "macOS" : "Windows")
            : L.Format("Windows / macOS: {0}", status.MacActive ? "macOS" : "Windows");
    }

    public static string UnsupportedText(string deviceId) => Find(deviceId) switch
    {
        { ProfilePositions: > 0, ProfileSwitchSupported: false } => L.T("Der Profilschalter wird noch nicht unterstützt."),
        { HasConnectionSwitch: true } => L.T("Verbindungsschalter: nur USB. Bluetooth wird nicht unterstützt."),
        _ => ""
    };

    internal static void RunOfflineChecks(Action<bool, string> check)
    {
        check(All.Select(item => item.DeviceId).Order().SequenceEqual(BoardDescriptorRegistry.ProtocolBoards.Select(item => item.DeviceId).Order()) &&
            All.Select(item => item.DeviceId).Distinct().Count() == All.Count,
            "Hardware controls: every supported model has one explicit inventory entry");
        check(!HasOsSwitch("gmmk3-pro-75-ansi") && Find("gmmk3-unknown") is null,
            "Hardware controls: similar retail names never inherit switch capabilities");
        check(All.Count(item => item.HasOsSwitch) == 7 && All.Count(item => item.ProfileSwitchSupported) == 1 &&
            Find("gmmk3-pro-100-iso")?.ProfileSwitchSupported == true && All.All(item => !item.WirelessSupported),
            "Hardware controls: only independently mapped board16 offers profile switching; wireless remains unsupported");
        foreach (var item in All)
        {
            check((OsSwitchText(item.DeviceId, null, false).Length > 0) == item.HasOsSwitch,
                "Hardware controls: no phantom OS test for " + item.DeviceId);
            if (!item.HasOsSwitch) continue;
            check(OsSwitchText(item.DeviceId, null, true) == L.T("Für den Schaltertest ist ein Firmware-Update erforderlich."),
                "Hardware controls: older firmware keeps a readable update hint: " + item.DeviceId);
            check(OsSwitchText(item.DeviceId, new(item.DeviceId, false, false, false), true) == L.Format("Windows / macOS: {0}", "Windows") &&
                OsSwitchText(item.DeviceId, new(item.DeviceId, true, true, false), true) == L.Format("Windows / macOS: {0}", "macOS"),
                "Hardware controls: Windows and macOS states use reported firmware values: " + item.DeviceId);
            check(OsSwitchText(item.DeviceId, new(item.DeviceId, true, false, true), true) ==
                L.Format("Windows / macOS: {0} → {1}. Lasse alle Tasten los.", "Windows", "macOS"),
                "Hardware controls: pending switch distinguishes selected and active mode: " + item.DeviceId);
        }
    }
}
