using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace InGlorious.Core;

/// <summary>Maps diagnostic failures to fixed UI text. Raw process output is never display text.</summary>
public static class FirmwareMessages
{
    public static string ForUnsupportedSourceWithoutWrite() => L.T("Die vorhandene Firmware wird von diesem Update nicht unterstützt. Es wurde nichts auf die Tastatur geschrieben.");
    public static string ForSourceChangedWithoutWrite() => L.T("Die Firmware hat sich seit der Vorbereitung geändert. Es wurde nichts auf die Tastatur geschrieben. Bitte starte die Vorbereitung erneut.");
    public static string ForExecutionFailure() => L.T("Das Firmware-Ergebnis ist nicht bestätigt. Starte den Vorgang nicht erneut und lasse die Tastatur vorerst im Update-Modus.");
    public static string ForUser(string? raw)
    {
        if (raw == ForExecutionFailure() || raw == ForUnsupportedSourceWithoutWrite() || raw == ForSourceChangedWithoutWrite()) return raw;
        // The UI may sanitize a service result again. Preserve only exact known translated messages,
        // optionally prefixed with our own explicit outcome statement; arbitrary text is never trusted.
        foreach (var message in KnownErrors())
            if (raw == message || raw == L.T("Firmware-Ergebnis nicht bestätigt. Nicht automatisch wiederholen. ") + message || raw == L.T("Es wurde kein Flash-Vorgang gestartet. ") + message) return raw;
        var text = ExtractDiagnosticText(raw);
        bool Has(params string[] values) => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
        if (Has("WinUSB", "wrong-driver", "Treiber stimmt", "driver missing"))
            return L.T("Der benötigte USB-Treiber ist nicht bereit. Prüfe den Treiber, bevor du fortfährst.");
        if (Has("dauerte zu lange", "timed out", "timeout", "Zeitüberschreitung"))
            return L.T("Die Geräteprüfung hat zu lange gedauert. Bitte versuche es erneut.");
        if (Has("noch im Bootloader", "still in bootloader"))
            return L.T("Die Tastatur ist noch im Update-Modus. Trenne das Kabel und verbinde sie ohne gedrückte Tasten erneut.");
        if (Has("Normalmodus am bisherigen USB-Anschluss", "normale USB-Verbindung", "Wiederverbindungsprüfung"))
            return L.T("Warte auf das Gerät am bisherigen USB-Anschluss. Verbinde es ohne gedrückte Tasten erneut.");
        if (Has("Bootloader ist nicht", "Bootloader-Identität", "bootloader is not present", "matching bootloaders"))
            return L.T("Die Tastatur wurde noch nicht im Update-Modus erkannt. Verwende denselben USB-Anschluss und folge der angezeigten Tastenkombination.");
        if (Has("USB-Anschlussweg", "USB-Anschluss des Bootloaders"))
            return L.T("Der USB-Anschluss konnte nicht eindeutig geprüft werden. Bitte verbinde das Gerät erneut.");
        if (Has("Original-Sicherungen fehlen", "Erforderliche lokale Datei fehlt", "Firmware-Skripte fehlen", "Could not find file", "Could not find a part"))
            return L.T("Benötigte Firmware-Dateien oder die Original-Sicherung fehlen. Es wurde nichts heruntergeladen.");
        if (Has("PowerShell"))
            return L.T("Das benötigte Windows-Werkzeug ist nicht verfügbar. Die Firmware-Funktion bleibt gesperrt.");
        if (Has("weicht von der geprüften Version ab", "Pinned hash mismatch", "Pinned size mismatch", "Prüfsumme"))
            return L.T("Eine Firmware-Datei stimmt nicht mit der geprüften Version überein. Der Vorgang wurde gestoppt.");
        if (Has("Ausgangsfirmware", "pinned update source", "Unexpected data between old code", "Originalzustands", "original saved hash"))
            return L.T("Die Firmware auf dem Gerät passt nicht zu diesem Vorgang. Es wird nichts übertragen.");
        if (Has("Rücklesung", "readback differs", "Readback storage", "Vorher-Abbilder", "before-captures differ", "Independent firmware reads differ"))
            return L.T("Die gelesenen Daten stimmen nicht überein. Das Ergebnis ist nicht bestätigt; prüfe den Prüfbericht.");
        if (Has("aktuellen Plan prüfen", "aktuellen Vorgang", "aktueller, vollständig geprüfter Firmware-Vorgang"))
            return L.T("Bitte starte die Vorbereitung für diesen Vorgang erneut.");
        if (Has("Zugriff verweigert", "Access is denied", "being used by another process", "von einem anderen Prozess"))
            return L.T("Der Zugriff ist momentan blockiert. Schließe andere Tastaturprogramme und versuche es erneut.");
        return L.T("Der Vorgang konnte nicht abgeschlossen werden. Die Ursache wurde nicht eindeutig erkannt.");
    }

    private static string[] KnownErrors() =>
    [
        ForUnsupportedSourceWithoutWrite(),
        ForSourceChangedWithoutWrite(),
        L.T("Die Firmware-Version konnte nicht geprüft werden."),
        L.T("Die erwartete Firmware-Version wurde noch nicht bestätigt."),
        L.T("Die USB-Verbindung konnte nicht geprüft werden. Die App versucht es erneut."),
        L.T("Der benötigte USB-Treiber ist nicht bereit. Prüfe den Treiber, bevor du fortfährst."),
        L.T("Die Geräteprüfung hat zu lange gedauert. Bitte versuche es erneut."),
        L.T("Die Tastatur ist noch im Update-Modus. Trenne das Kabel und verbinde sie ohne gedrückte Tasten erneut."),
        L.T("Warte auf das Gerät am bisherigen USB-Anschluss. Verbinde es ohne gedrückte Tasten erneut."),
        L.T("Die Tastatur wurde noch nicht im Update-Modus erkannt. Verwende denselben USB-Anschluss und folge der angezeigten Tastenkombination."),
        L.T("Der USB-Anschluss konnte nicht eindeutig geprüft werden. Bitte verbinde das Gerät erneut."),
        L.T("Benötigte Firmware-Dateien oder die Original-Sicherung fehlen. Es wurde nichts heruntergeladen."),
        L.T("Das benötigte Windows-Werkzeug ist nicht verfügbar. Die Firmware-Funktion bleibt gesperrt."),
        L.T("Eine Firmware-Datei stimmt nicht mit der geprüften Version überein. Der Vorgang wurde gestoppt."),
        L.T("Die Firmware auf dem Gerät passt nicht zu diesem Vorgang. Es wird nichts übertragen."),
        L.T("Die gelesenen Daten stimmen nicht überein. Das Ergebnis ist nicht bestätigt; prüfe den Prüfbericht."),
        L.T("Bitte starte die Vorbereitung für diesen Vorgang erneut."),
        L.T("Der Zugriff ist momentan blockiert. Schließe andere Tastaturprogramme und versuche es erneut."),
        L.T("Der Vorgang konnte nicht abgeschlossen werden. Die Ursache wurde nicht eindeutig erkannt.")
    ];

    internal static string ExtractDiagnosticText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4 * 1024 * 1024) return "";
        if (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[')) return "";
        var messages = new List<string>();
        var xmlStart = raw.IndexOf("<Objs", StringComparison.Ordinal);
        if (xmlStart >= 0 || raw.Contains("#< CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            // Read only PowerShell's Error/Warning streams, never Progress/Verbose/Information or invocation metadata.
            while (xmlStart >= 0)
            {
                var end = raw.IndexOf("</Objs>", xmlStart, StringComparison.Ordinal);
                if (end < 0) break;
                try
                {
                    using var reader = XmlReader.Create(new StringReader(raw[xmlStart..(end + 7)]), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                    var document = XDocument.Load(reader);
                    foreach (var node in document.Descendants().Where(node => (string?)node.Attribute("S") is "Error" or "error" or "Warning" or "warning"))
                    {
                        if (node.Name.LocalName == "S") messages.Add(Decode(node.Value));
                        else if (node.Name.LocalName == "Obj")
                        {
                            var message = node.Elements().FirstOrDefault(child => child.Name.LocalName == "ToString") ?? node.Descendants().FirstOrDefault(child => (string?)child.Attribute("N") == "Message");
                            if (message is not null) messages.Add(Decode(message.Value));
                        }
                    }
                }
                catch (XmlException) { /* Malformed diagnostics are never repaired into executable or display text. */ }
                xmlStart = raw.IndexOf("<Objs", end + 7, StringComparison.Ordinal);
            }
        }
        else messages.Add(raw);
        return string.Join("\n", messages.SelectMany(message => message.Replace("\r", "").Split('\n'))
            .Select(line => Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim())
            .Where(line => line.Length != 0 && !line.StartsWith('{') && !line.StartsWith('[') && !line.StartsWith('<') &&
                !Regex.IsMatch(line, @"^(?:\+|~|At |Bei |In |Line |Zeile |CategoryInfo\s*:|FullyQualifiedErrorId\s*:|ScriptStackTrace\s*:|PositionMessage\s*:|\d+\s*\|)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                !line.Contains("Get-PnpDevice", StringComparison.OrdinalIgnoreCase) && !line.Contains("$", StringComparison.Ordinal) && !line.Contains("ConvertTo-Json", StringComparison.OrdinalIgnoreCase)));
    }

    private static string Decode(string text) => Regex.Replace(text, @"_x([0-9a-fA-F]{4})_", match => ((char)Convert.ToUInt16(match.Groups[1].Value, 16)).ToString());
}
