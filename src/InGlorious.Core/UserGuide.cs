namespace InGlorious.Core;

public sealed record GuideSection(string Heading, string Body);
public sealed record GuideTopic(string Id, string Title, IReadOnlyList<GuideSection> Sections);

/// <summary>German source text; each presentation translates it through L.T.</summary>
public static class UserGuide
{
    public static IReadOnlyList<GuideTopic> Topics { get; } = Array.AsReadOnly(new GuideTopic[]
    {
        new("start", "Erste Schritte", Array.AsReadOnly(new GuideSection[]
        {
            new("Beim Öffnen", "Verbinde die Geräte per USB. Bei INGLORIOUS-Firmware liest die App zuerst die aktuellen Einstellungen. Mit Original-Firmware ist zuerst die Einrichtung nötig. Danach bearbeitest du Tastatur und GMMK Numpad getrennt. Ist ein Gerät nicht eindeutig erreichbar, bleibt ein lokales Profil mit einem Hinweis geöffnet."),
            new("Änderungen speichern", "Bearbeite die gewünschte Einstellung und bestätige sie mit „Übernehmen“. „Auf Tastatur übertragen“ speichert den gesamten Arbeitsstand auf dem ausgewählten Gerät. Warte auf die Bestätigung. Anschließend funktionieren Farben, Belegungen und Makros auch ohne App.")
        })),
        new("lighting", "Beleuchtung", Array.AsReadOnly(new GuideSection[]
        {
            new("Farben und Effekte", "Wähle Tasten oder Seiten. Atmen, Wellen und Glitzern verwenden deren Basisfarben. „Farbwechsel“ verwendet seine eigene Farbfolge, ohne Farbfolge einen Regenbogen. Regenbogen und Feuer erzeugen eigene Farben."),
            new("Global oder einzeln", "„Global“ gilt nur für die aktuelle Ebene und den gewählten Bereich. Eigene Effekte einzelner LEDs haben Vorrang, auch bei der Helligkeit. Mit „Globalen Effekt verwenden“ entfernst du diese Ausnahme."),
            new("Vorschau und Übernehmen", "„Farben“ und „Effekte“ zeigen die gespeicherte Beleuchtung mit ihren Effekten. Farbwähler und Farbfelder ändern die Basisfarbe. Eine eigene Farbfolge im Effekt „Farbwechsel“ verwendet stattdessen ihre Farben. Bestätige Änderungen mit „Übernehmen“ und danach mit „Auf Tastatur übertragen“.")
        })),
        new("bindings", "Tastenbelegung", Array.AsReadOnly(new GuideSection[]
        {
            new("Tasten und Drehgeber", "Wähle eine Taste in der Vorschau, suche eine Funktion und übernimm sie. Mehrere ausgewählte Tasten erhalten dieselbe Belegung. Die beiden Drehrichtungen des Drehgebers lassen sich getrennt belegen. Belegungen gelten für die oben ausgewählte Ebene."),
            new("Betriebssystemaktionen", "Unter „Betriebssystem-Aktion …“ wählst du Betriebssystem und Tastaturlayout. Die Aktion verwendet passende Tastenfolgen; ihre Wirkung hängt vom System und der geöffneten Anwendung ab. Prüfe die Auswahl, bevor du sie einer Taste zuweist.")
        })),
        new("macros", "Makros", Array.AsReadOnly(new GuideSection[]
        {
            new("Erstellen und zuweisen", "Erstelle ein Makro oder wähle „Aufnehmen …“. Prüfe danach die Tastendrücke, Freigaben und Pausen. Wähle das fertige Makro und weise es einer Taste auf der gewünschten Ebene zu."),
            new("Gemeinsame Bibliothek", "„Bibliothek / Import / Export …“ speichert Makros zur Wiederverwendung und tauscht sie als Dateien aus. Ein importiertes Makro wird in das aktuelle Profil kopiert. Änderungen an dieser Kopie verändern andere Profile nicht."),
            new("Auf dem Gerät", "Übertrage das Profil, damit die Makros ohne App laufen. Bei zu vielen Schritten meldet die App die Grenze; sie kürzt nichts automatisch. Probiere neue Makros zuerst in einer passenden, unkritischen Anwendung aus.")
        })),
        new("layers", "Ebenen", Array.AsReadOnly(new GuideSection[]
        {
            new("Vier Belegungen", "Ebene 0 ist die Basis, Ebenen 1 bis 3 bieten weitere Belegungen und Beleuchtungen. Die Auswahl oben ändert nur, welche Ebene du bearbeitest. Unter „Ebenen“ kannst du sie benennen, vergleichen oder in eine andere Ebene kopieren. Kopieren ersetzt deren Einstellungen."),
            new("Auf der Tastatur wechseln", "Wähle eine Taste und unter „Ebenenwechsel zuweisen“ das Ziel. „Beim Halten“ aktiviert es solange die Taste gedrückt ist. „Umschalten“ schaltet es mit erneutem Druck zurück. „Belegung darunter verwenden“ übernimmt auf höheren Ebenen die darunterliegende Tastenfunktion.")
        })),
        new("profiles", "Profile", Array.AsReadOnly(new GuideSection[]
        {
            new("Datei und Gerät", "Ein Profil enthält die Einstellungen für ein Gerät. „Profil speichern“ bewahrt sie als Datei auf dem Computer. „Auf Tastatur übertragen“ speichert sie im Gerät. Das sind zwei getrennte Schritte. Beim App-Start ausgelesene Einstellungen überschreiben deine Profildateien nicht."),
            new("Öffnen und wiederverwenden", "Im Bereich „Profile“ öffnest du gespeicherte Arbeitsstände oder legst neue an. Tastatur und GMMK Numpad behalten getrennte Arbeitsstände. Prüfe das ausgewählte Gerät, bevor du ein Profil überträgst."),
            new("Farben und Vorlagen", "Speichere häufige Farben als Favoriten. Beleuchtungsvorlagen übernehmen Farben und Effekte auf die aktuelle Ebene; Tastenbelegungen und Makros bleiben erhalten. Eigene Vorlagen lassen sich speichern, importieren und exportieren.")
        })),
        new("device", "Gerät", Array.AsReadOnly(new GuideSection[]
        {
            new("Regler und USB", "Wähle beim GMMK Numpad den Schieberegler in der Vorschau. Er kann Lautstärke, Helligkeit oder MIDI steuern. Unter „Gerät“ findest du USB-Abfragerate und Entprellzeit. Diese gelten für alle Ebenen."),
            new("Gemeinsames Dimmen", "Mit „Beleuchtung dimmen“ steuert der GMMK Numpad-Regler sein eigenes Licht ohne App. Soll er auch die GMMK PRO ISO dimmen, aktiviere das gemeinsame Dimmen und lasse INGLORIOUS Advanced im Hintergrund geöffnet."),
            new("Firmware und Rückkehr", "Über das Zahnrad richtest du ein Gerät ein. Unter „Gerät“ kannst du die Firmware aktualisieren oder das Original wiederherstellen. Wähle bei der Ersteinrichtung „Sichern und installieren“: Die App sichert und prüft zuerst das Original und installiert danach INGLORIOUS. Lasse App und USB-Verbindung aktiv, bis du zum erneuten Verbinden aufgefordert wirst."),
        })),
        new("standby", "Standby", Array.AsReadOnly(new GuideSection[]
        {
            new("Licht bei Inaktivität", "Unter „Beleuchtung → Standby“ aktivierst du eine Wartezeit und wählst Ausschalten, Dimmen oder Screensaver. Standby gilt für das ganze Gerät. Übernimm die Einstellung und übertrage sie auf die Tastatur; danach funktioniert sie ohne laufende App."),
            new("Wieder aufwecken", "Tasten, Drehgeber und eine bewusste Reglerbewegung beenden Standby. Der Screensaver wiederholt die komplette Lichtsequenz bis zur nächsten Eingabe. Deine normale Beleuchtung bleibt gespeichert. Im USB-Ruhezustand ist das Licht aus.")
        })),
        new("settings", "Einstellungen", Array.AsReadOnly(new GuideSection[]
        {
            new("Sprache und Hinweise", "Öffne das Zahnrad oben rechts. „Systemsprache“ folgt der Sprache deines Betriebssystems; alternativ wählst du eine der angebotenen Sprachen. Hilfetexte lassen sich ein- und ausblenden. Die Anleitung bleibt über „?“ erreichbar."),
            new("App und Tastatur", "Diese Einstellungen betreffen die App. Sie ändern weder Tastaturbelegungen noch das Tastaturlayout deines Betriebssystems. Hier kannst du auch den Ordner mit deinen gespeicherten App-Daten öffnen.")
        }))
    });
}
