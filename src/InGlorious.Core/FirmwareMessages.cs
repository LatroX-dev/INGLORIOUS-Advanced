namespace InGlorious.Core;

/// <summary>Shared wording for a firmware write whose outcome could not be confirmed.</summary>
public static class FirmwareMessages
{
    public static string ForExecutionFailure() => L.T("Das Firmware-Ergebnis ist nicht bestätigt. Starte den Vorgang nicht erneut und lasse die Tastatur vorerst im Update-Modus.");
}
