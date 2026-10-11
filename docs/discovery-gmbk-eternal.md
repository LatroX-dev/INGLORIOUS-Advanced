# GMBK und GMMK Eternal: Entwicklungsbefund

Stand: 11. Oktober 2026. Beide Modelle sind **nicht für Gerätezugriffe freigegeben**: `readReady=false`, `writeReady=false`. Es wurde kein Gerät angesprochen und kein Herstellerprogramm gestartet. Die Anbindung ist noch nicht implementiert oder am Gerät getestet.

## Belegte Modelle und Funktionen

| Merkmal | GMBK 75% | GMMK Eternal 96% |
|---|---|---|
| Bauart | Membran, 80 ANSI-/81 ISO-Tasten | Mechanisch, MX-Hot-Swap |
| Layoutvarianten | ANSI und ISO | Produktseite: US, UK, ND; CORE: ANSI und ISO |
| Beleuchtung | Zehn RGB-Zonen mit Seitenbeleuchtung, kein RGB pro Taste | RGB pro Taste |
| Verbindung | USB-Kabel | USB-Kabel |
| Profile im Gerät | Drei | Drei |
| Höchste Abfragerate | 1.000 Hz | 1.000 Hz |
| QMK | Hersteller sagt ausdrücklich „No“ | Keine Unterstützung oder Portquelle in den geprüften Herstellerunterlagen belegt |
| VIA | Nicht belegt | Nicht belegt |

Primärquellen: [GMBK-Produktseite](https://www.gloriousgaming.com/products/gmbk-75-keyboard), [GMBK-Anleitung](https://www.gloriousgaming.com/pages/gmbk-75-keyboard-product-guide), [Eternal-Produktseite](https://www.gloriousgaming.com/products/gmmk-eternal-96-keyboard), [Eternal-Anleitung](https://www.gloriousgaming.com/pages/gmmk-eternal-96-keyboard-product-guide).

Die GMBK-Anleitung beschreibt CORE für Beleuchtung, Profile, Makros und Belegungen. Die Eternal-Anleitung bestätigt, dass Profile im Gerät gespeichert werden. Das belegt keine verlustfreie vollständige Sicherung aller internen Einstellungen durch eine Ersatzsoftware.

## Lokale Herstellerbelege

Die vorhandene, unveränderte Kopie von CORE 2.1.21 / Build 2.1.21.4 enthält eigene Gerätebeschreibungen und Handler. Herkunft und ursprüngliche Dateiprüfung stehen in `backups/initial/manifest.json`. Der SHA256 von `backups/initial/package-source/out/main/index.js` wurde erneut geprüft:

`9DE8428785A7A0AEE5AA965A782D41C891152411CB47051FA386AC802149BB75`

Das ist proprietärer Anwendungscode, keine veröffentlichte Firmwarequelle. Er wird weder in öffentliche Quellen kopiert noch ausgeführt. Die folgenden Werte sind Herstellerdeklarationen aus diesem Paket; es sind keine Messungen an realen GMBK-/Eternal-Geräten.

| Variante | Normalmodus VID:PID | Im Descriptor genannter Bootloader |
|---|---|---|
| GMBK ANSI | `3794:B000` | `3794:B002` |
| GMBK ISO | `3794:B001` | `3794:B003` |
| Eternal ANSI | `3794:B010` | `3794:B012` |
| Eternal ISO | `3794:B011` | `3794:B012` |

Fundstellen: GMBK-Deskriptoren Zeilen 12557 und 12639; Eternal-Deskriptoren Zeilen 37774 und 37915. Beim Eternal beschreibt ein Kommentar im Updater zusätzlich einen beobachteten ISP-Modus `0603:1020`. Diese Abweichung ist ungeklärt. Die Angaben sind deshalb **keine Flash- oder Bootloaderfreigabe**.

## Protokollansätze

GMBK verwendet Feature-Reports mit Report-ID `7`. Die Gerätebeschreibung nennt für Set `FF01:0001`, für Get `FF00:FF00`; der Handler liest Feature-Reports allerdings über den Write-Handle. Allgemeine Puffer sind 264 Byte groß, einzelne Befehle verwenden andere Längen. Diese Transportdetails müssen am konkreten Gerät geprüft werden.

| GMBK-Funktion | Set | Get |
|---|---|---|
| Aktives Profil und Ebene | `01` | `81` |
| Beleuchtung | `02` | `82` |
| Tastenbelegungen | `03` | `83` |
| Geräteeinstellungen | `11` | `91` |
| Makro | `12` | `92` |

Fundstellen: Reportaufbau ab Zeile 48147, Handler ab 48679, Transport ab 49081. Der Zonenhandler modelliert zehn Zonen. Eine Ersatzanbindung darf daraus keine unabhängige Beleuchtung jeder Taste machen. Die vorhandene Makro-/Ebenenimplementierung ist nicht mit dem INGLORIOUS-Konfigurationsformat gleichzusetzen.

Eternal verwendet einen anderen Herstellerkanal: 64-Byte-Pakete über Interrupt OUT/IN, Report-ID `9`, Usage `FF60:0061`. Belegte Leseoperationen sind `82` (Info), `84` (Konfiguration) und `F1` (Benutzerdaten). Fundstellen: Handler ab Zeile 68702, Anfrage-/Antwortverarbeitung ab 68963. Die Usage ist allein kein Nachweis für QMK oder VIA. Der Handler enthält separate Wege für Einzelbeleuchtung, Makros, Belegungen und Leistungseinstellungen; ein gemeinsamer generischer GMBK-/Eternal-Transport wäre falsch.

## Firmware und Sicherung

MCU, Flash-/RAM-Größe, elektrische Matrix, LED-Treiberverdrahtung sowie ein verlässlicher Firmware-Readback sind für beide Modelle **unbekannt**. Der gepinnte QMK-Checkout `7a1bbf37c5c07da4ea0a162bb139083a46ef40a1` und die am 11. Oktober abgefragte [Upstream-Verzeichnisliste](https://api.github.com/repos/qmk/qmk_firmware/contents/keyboards/gmmk) enthalten nur `gmmk2`, `numpad` und `pro`. Das ist ein begrenzter Suchbefund, kein Beweis gegen spätere oder externe Ports.

GMBK nutzt einen separat gelieferten `FWUpdate.exe`. Eternal sucht `FWUpdate.exe`, `UpdateFirmware.dll` und eine zum VID/PID passende `.upf`-Datei mit `BLESINO`-Kennung; Fundstellen 67518, 67694 und 70030. Daraus lässt sich weder der genaue MCU noch ein ungefährlicher Dump-Befehl ableiten. Ein Hersteller-Updatepaket ersetzt keinen ausgelesenen Originalzustand.

Die Factory-Reset-Kombinationen aus den Produktanleitungen löschen Einstellungen. Sie dürfen nicht als belegter Sicherungs- oder Bootloaderweg verwendet werden. Vorhandene STM32-/WB32-Backuphelfer für PRO und Numpad sind nicht automatisch anwendbar.

## Sinnvoller nächster Schritt

Eine getrennte Anbindung an die **Originalfirmware** erscheint anhand der CORE-Handler untersuchbar. Dafür zuerst am jeweiligen Gerät die exakten Interfaces, reine Info-/Konfigurationslesevorgänge, Antwortzuordnung und vollständige Exportierbarkeit prüfen. Erst danach gezielt persistente Schreiboperationen mit nachprüfbarer Rücklesung freigeben. Für einen eigenen Firmwareport fehlen wesentlich mehr Hardwaredaten.

Die maschinenlesbaren Metadaten mit Quellen und offenen Punkten stehen in `reference/discovery/gmbk-eternal-20261011.json`. Es werden keine Tastenmatrix, LED-Indizes oder neuen Runtime-IDs geraten. Insbesondere ist Eternal nicht aufgrund der Größe „96%“ mit dem GMMK2 96 gleichzusetzen.
