namespace InGlorious.Core;

/// <summary>Firmware guidance shared by the Windows and desktop apps.</summary>
public static class FirmwareMessages
{
    public static string RestoreSource(string? deviceId) => deviceId == RetainedLoaderLayout.DeviceId
        ? L.T("Die App verwendet deine Original-Sicherung. Fehlt sie, lädt die App die Firmware von Glorious über das Internet herunter.")
        : L.T("Die Wiederherstellung verwendet ausschließlich die passende Original-Sicherung dieses Geräts.");

    public static string ForExecutionFailure() => L.T("Das Firmware-Ergebnis ist nicht bestätigt. Starte den Vorgang nicht erneut und lasse die Tastatur vorerst im Update-Modus.");
}
