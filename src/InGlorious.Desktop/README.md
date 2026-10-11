# INGLORIOUS Advanced – plattformübergreifende Vorschau

Avalonia-Vorschau für Windows, macOS und Linux, App 0.6.38. Die WPF-App ist die separate Windows-Ausgabe. Beide verwenden dieselben Profile, Konfigurationsformate und Validierungsregeln. Das aktuelle lokale Firmwareziel ist 0.9.2 (`0.9.2-color-output`, Build `0x00090201`). Das Numpad-Update und der vollständige Lichttest wurden über die Windows-Ausgabe bestätigt; dies ist keine Prüfung des Avalonia-Gerätezugriffs. Die vorhandenen Berichte belegen frühere 0.8.0-Updates über den Windows-Ablauf; sie belegen keinen praktischen Gerätezugriff über den Avalonia-Port. Eine öffentliche Veröffentlichung ist weiterhin offen.

## Funktionsumfang

Die Geräteauswahl zeigt nur verbundene Geräte. Unterstützt werden 15 Layoutvarianten: GMMK PRO Rev. 1 und Rev. 2 jeweils ANSI/ISO, GMMK Numpad, GMMK2 65% und 96% jeweils ANSI/ISO sowie GMMK3 65%, 75% und 100% jeweils ANSI/ISO. Die 13 zusätzlichen Firmwareports sind experimentell und können nur mit passender, positiv erkannter INGLORIOUS-Laufzeitfirmware konfiguriert werden. Original-Firmware wird ausschließlich bei den bisher freigegebenen Geräten GMMK PRO Rev. 1 ISO und GMMK Numpad erkannt. Für die 13 experimentellen Modelle gibt es keine freigegebene Originalgeräte-Erkennung, Einrichtung oder Wiederherstellung; der portable Firmwareinstaller bleibt insgesamt deaktiviert. Details stehen in docs/model-support.md.

Beim Öffnen werden die aktuellen Konfigurationen eindeutig verbundener INGLORIOUS-Geräte gelesen, bevor die Bearbeitung freigegeben wird. Gelesene Geräte erhalten saubere Arbeitsstände ohne Bindung an eine Profildatei. Gespeicherte Profile bleiben erhalten; fehlende, mehrdeutige oder nicht lesbare Geräte erhalten einen sichtbaren Hinweis beim lokalen Profil. Gerätewechsel während der Sitzung behalten eigene Änderungen. Die Offline-Vorschau liest keine Geräte.

Nach „Version prüfen“ zeigt der Gerätebereich unter **What’s new** die englischen Firmwareänderungen zwischen der ausgelesenen Version und dem lokalen Ziel. Unbekannte, aktuelle oder neuere Versionen erhalten keine ausstehenden Änderungen. Die Appsprache bleibt davon unabhängig.

Profile öffnen/speichern, getrennte Entwürfe pro Gerät, Rückgängig/Wiederholen, vier Ebenen, RGB pro Taste und Seiten-LED, separate Logo-Beleuchtung bei GMMK3 75%/100%, globale und individuelle Effekte mit Farbfolgen, HSV-Farbwähler und gemeinsame löschbare Farbfavoriten, Beleuchtungsvorlagen inklusive INGLORIOUS, Belegungen, Encoder, Makros mit Schrittbearbeitung und Aufnahme im fokussierten Fenster, Makrobibliothek mit Import/Export, Betriebssystemaktionen für Windows/macOS/Linux mit QWERTZ/QWERTY/AZERTY-Auswahl sowie Numpad-Regler und USB-/Entprellparameter.

„Auf Tastatur übertragen“ erstellt und prüft die vollständige Konfiguration. Erst der weitere Bestätigungsklick schreibt sie. Vorher wird der Gerätestand vollständig gelesen, dauerhaft als Datei gesichert und byteweise geprüft. Nach dem Schreiben folgen vollständiges Rücklesen und CRC-/Generationsvergleich. Ein unsicherer Schreibausgang wird nicht automatisch wiederholt. Nur die ausdrücklich registrierten INGLORIOUS-Endpunkte FEED:1C01 bis FEED:1C0F mit passenden Gerätezeichenfolgen und RAW-HID-Deskriptor sind zugelassen; Original-Firmware und Bootloader werden nicht beschrieben.

Bei genau einem passenden Gerät entfällt die Auswahl. Der kompakte Übertragungsdialog schließt nach bestätigtem Erfolg automatisch; bei Fehlern bleibt er offen. Ein erforderliches erneutes Verbinden nach Änderung der Abfragerate wird anschließend kurz im Hauptfenster angezeigt.

Firmwareinstallation und Wiederherstellung der Original-Firmware sind in dieser Ausgabe **deaktiviert**. Dafür steht weiterhin der separate, auf konkrete Geräte und Sicherungen begrenzte Windows-Ablauf zur Verfügung. Bluetooth wird nicht unterstützt.

Der Numpad-Schieberegler wird unter „Tastenbelegung → Schieberegler“ konfiguriert. Beleuchtungsvorlagen zeigen zunächst nur das aktuelle Gerätemodell; „Alle Geräte anzeigen“ erweitert die Liste, erlaubt aber keine Anwendung einer unpassenden Vorlage. Die gemeinsamen Farbfavoriten lassen sich hinzufügen und entfernen.

„Beleuchtung → Standby“ speichert geräteweit die Wartezeit von 10–3600 Sekunden und die Aktion **Aus**, **Dimmen** oder **Screensaver**. Der aktivierte Screensaver wiederholt nach der Wartezeit die gesamte 21-Sekunden-Sequenz bis zur nächsten Eingabe: alle LEDs weiß, kurze Dunkelphase, INGLORIOUS auf der PRO beziehungsweise 1–9 und 0 auf dem Numpad, rote Tropfen mit Füllung von unten und abschließend alle LEDs rot. Eingaben stellen die normale Beleuchtung wieder her; im USB-Ruhezustand bleibt sie aus. Standby und Screensaver laufen auf der Firmware ohne App-Timer. Gespeicherte Farben, Effekte und Dimmerwerte bleiben unverändert.

Screensaver benötigt RAW11-Flag `0x02` der Firmware 0.9.0, auch wenn die Aktion bereits ausgewählt, Standby aber noch deaktiviert ist. Fehlende Unterstützung sperrt die betreffende Konfiguration vor dem Schreiben; dadurch wird keine Firmware installiert und keine Versionsnummer abgeleitet. Die automatische Sequenz sperrt weder Konfigurationsübertragungen noch den Laufzeitdimmer. Der manuelle Test im Windows-Firmwareassistenten hat dagegen eine feste Laufzeit und sperrt beides während des Tests.

Gemeinsames Dimmen von Numpad und Tastatur benötigt INGLORIOUS im Hintergrund. Der lokale Numpad-Dimmer funktioniert auch ohne App. Farben, Effekte und Belegungen bleiben auf den Geräten gespeichert. Gemeinsames Dimmen ist standardmäßig aus und wird ausdrücklich in den Einstellungen aktiviert; beide Geräte müssen die flüchtige Helligkeitsschnittstelle unterstützen. Die App richtet keinen Autostart ein.

Die explizite Versionsantwort **RAW13** ist von Konfigurationsfähigkeiten getrennt. Ohne gültige Antwort bleibt die Laufzeitversion unbekannt; USB-Revision und Funktionsmerkmale werden nicht als Ersatz verwendet. Erkannte Original-Firmware kann über den auf konkrete Geräte und Sicherungen begrenzten Windows-Assistenten eingerichtet werden. Es gibt keine separate Bootloader-Versionsinspektion oder Online-Releasesuche.

## Installation und Start

Die Ausgabe ist portabel und besitzt noch keinen plattformübergreifenden Installer. Den vollständigen jeweiligen Publish-Ordner kopieren; einzelne EXE-/DLL-Dateien reichen nicht. Die mitgelieferte .NET-Laufzeit ist bei selbständigen Paketen enthalten. Für ein Gerät nur eine INGLORIOUS-App gleichzeitig zum Konfigurieren verwenden.

- **Windows:** `win-x64/INGLORIOUS Advanced.exe` starten. Die Vorschau ersetzt die vorhandene Windows-App nicht.
- **macOS:** passendes Paket `osx-arm64` für Apple Silicon oder `osx-x64` für Intel verwenden. Es ist kein signiertes/notarisiertes `.app`-Bundle. Der ausführbaren Datei bei Bedarf über das Terminal mit `chmod +x 'INGLORIOUS Advanced'` Ausführungsrechte geben und sie aus ihrem Ordner starten. macOS kann eine ausdrückliche Freigabe in Datenschutz & Sicherheit verlangen. Keine globale Abschaltung von Gatekeeper erforderlich.
- **Linux:** `linux-x64` vollständig kopieren, mit `chmod +x 'INGLORIOUS Advanced'` ausführbar machen und starten. Die grafischen Systembibliotheken der Distribution müssen verfügbar sein. HID benötigt Zugriffsrechte für den Benutzer auf die **INGLORIOUS**-RAW-Schnittstelle; ein fehlender Zugriff wird als Fehler angezeigt. Die Anwendung nicht dauerhaft als root starten. Eine distributionsspezifische udev-Regel sollte ausschließlich VID `feed`, die benötigte PID aus `1c01` bis `1c0f` freigeben, nicht alle Eingabegeräte.

Außerhalb des Quellcode-Arbeitsordners liegen Profile und Einstellungen unter `INGLORIOUS Advanced/data` im lokalen App-Datenverzeichnis der Plattform; unter Windows ist das `%LOCALAPPDATA%\INGLORIOUS Advanced\data`. Im Arbeitsordner wird dessen `data`-Verzeichnis verwendet. Der Zahnrad-Dialog öffnet den Ordner. Profile lassen sich über „Profil öffnen“ importieren; Makros und Beleuchtungsvorlagen haben eigene portable JSON-Formate. Die Sprache folgt standardmäßig der Systemsprache, mit Englisch als Rückfall und zwölf manuell wählbaren Sprachen.

## Build und Offline-Prüfung

Benötigt .NET SDK 9. Pakete sind exakt auf Avalonia 12.1.4 und HidSharp 2.6.4 festgelegt.

```powershell
dotnet build src/InGlorious.Desktop/InGlorious.Desktop.csproj -c Release
dotnet run --project src/InGlorious.Desktop -c Release -- --smoke artifacts/desktop-smoke.txt --screenshot artifacts/desktop-pro.png
dotnet run --project src/InGlorious.Desktop -c Release -- --minimum --screenshot artifacts/desktop-minimum.png
dotnet publish src/InGlorious.Desktop -c Release -r win-x64 --self-contained true -o artifacts/desktop-preview/win-x64
dotnet publish src/InGlorious.Desktop -c Release -r linux-x64 --self-contained true -o artifacts/desktop-preview/linux-x64
dotnet publish src/InGlorious.Desktop -c Release -r osx-arm64 --self-contained true -o artifacts/desktop-preview/osx-arm64
dotnet publish src/InGlorious.Desktop -c Release -r osx-x64 --self-contained true -o artifacts/desktop-preview/osx-x64
```

`--smoke` und `--screenshot` sperren Geräteerkennung, HID, Benutzerdaten-Schreibvorgänge, Datei- und Aufnahmedialoge sowie Hintergrund-Synchronisation. Nur die ausdrücklich angegebenen Prüfbericht-/Bilddateien werden geschrieben. Die Tests prüfen alle 15 Layoutvarianten, Modell-/Undo-Kopien, Farbsynchronisierung, unabhängige Logo-Farben und -Effekte, Konfigurations-Roundtrips, Makro- und Presetkopien sowie erlaubte/abgewiesene HID-Deskriptoren anhand synthetischer Bytes.

Die Windows-Oberfläche wird in dieser Umgebung offline gebaut, geprüft und gerendert. Cross-Publishing bestätigt **keinen** erfolgreichen Start oder Gerätezugriff auf macOS/Linux. Diese Plattformen und der neue portable HID-Transport benötigen noch praktische Tests auf den jeweiligen Rechnern. Die vorhandenen Firmware-Hardwaretests der WPF-App gelten nicht automatisch als Transporttest dieses Ports.

Technische Grundlagen: [Avalonia](https://docs.avaloniaui.net/docs/get-started), [HidSharp](https://github.com/IntergatedCircuits/HidSharp).

Die Wortmarke verwendet die eingebettete statische Schrift Oxanium SemiBold (600); eine Systeminstallation ist nicht erforderlich. Die SIL Open Font License liegt im Paket unter `Licenses/Oxanium-OFL.txt`.
