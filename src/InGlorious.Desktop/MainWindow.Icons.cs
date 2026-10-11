using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private object ButtonContent(string source)
    {
        var shape = source switch
        {
            "⚙" or "Einstellungen" => "M9 3 L15 3 L16 6 L19 7 L21 10 L19 12 L21 15 L19 18 L16 18 L15 21 L9 21 L8 18 L5 18 L3 15 L5 12 L3 10 L5 7 L8 6 Z M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8",
            "↶" => "M8 5 L3 10 L8 15 M3 10 L15 10 Q21 10 21 17 L21 20",
            "↷" => "M16 5 L21 10 L16 15 M21 10 L9 10 Q3 10 3 17 L3 20",
            "Beleuchtung" or "Effekte" => "M12 2 L12 5 M12 19 L12 22 M2 12 L5 12 M19 12 L22 12 M5 5 L7 7 M17 17 L19 19 M5 19 L7 17 M17 7 L19 5 M12 7 A5 5 0 1 0 12 17 A5 5 0 1 0 12 7",
            "Tastenbelegung" or "Tasten" or "GMMK PRO ISO" or "GMMK Numpad" => "M2 5 L22 5 L22 19 L2 19 Z M5 9 L7 9 M10 9 L12 9 M15 9 L17 9 M19 9 L20 9 M5 13 L7 13 M10 13 L12 13 M15 13 L20 13 M6 16 L17 16",
            "Ebenen" => "M2 7 L12 2 L22 7 L12 12 Z M2 12 L12 17 L22 12 M2 17 L12 22 L22 17",
            "Makros" or "Makrobibliothek …" => "M5 3 L19 12 L5 21 Z",
            "Gerät" => "M12 22 L12 3 M8 7 L12 3 L16 7 M12 16 L5 12 L5 8 M12 14 L19 10 L19 6 M3 8 L7 8 M17 4 L21 4 L21 7 L17 7 Z",
            "Profile" or "Neues Profil" => "M5 2 L15 2 L20 7 L20 22 L5 22 Z M15 2 L15 7 L20 7 M8 12 L17 12 M8 16 L17 16",
            "Profil öffnen …" or "Importieren …" => "M2 6 L10 6 L12 9 L22 9 L20 20 L2 20 Z M2 6 L2 3 L9 3 L11 6 L20 6 L20 9",
            "Profil speichern" or "Speichern" or "Als Favorit speichern" or "Aktuelles Makro speichern" or "Als Vorlage speichern" => "M3 2 L18 2 L22 6 L22 22 L3 22 Z M7 2 L7 9 L17 9 L17 2 M7 22 L7 15 L18 15 L18 22",
            "Von Tastatur laden" or "Gerätestand lesen" => "M12 2 L12 17 M6 11 L12 17 L18 11 M3 17 L3 22 L21 22 L21 17",
            "Auf Tastatur übertragen" or "Exportieren …" => "M12 17 L12 2 M6 8 L12 2 L18 8 M3 17 L3 22 L21 22 L21 17",
            "Alle auswählen" => "M3 3 L21 3 L21 21 L3 21 Z M6 12 L10 16 L18 7",
            "Auswahl aufheben" or "Abbrechen" => "M5 5 L19 19 M19 5 L5 19",
            "Übernehmen" or "Anwenden" or "Zuweisen" => "M3 12 L9 18 L21 5",
            "Entfernen" or "Schritt entfernen" or "Verwerfen" => "M4 6 L20 6 M8 6 L8 3 L16 3 L16 6 M6 6 L7 22 L17 22 L18 6 M10 10 L10 18 M14 10 L14 18",
            "Beleuchtungsvorlagen …" => "M3 3 L10 3 L10 10 L3 10 Z M14 3 L21 3 L21 10 L14 10 Z M3 14 L10 14 L10 21 L3 21 Z M14 14 L21 14 L21 21 L14 21 Z",
            _ => null
        };
        if (shape is null) return L.T(source);
        var icon = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(shape), Stroke = Brush("#E4E7EB"), Fill = Brushes.Transparent, StrokeThickness = 1.7, Stretch = Stretch.Uniform, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (source is "↶" or "↷" or "⚙") return icon;
        return Row(icon, new TextBlock { Text = L.T(source), VerticalAlignment = VerticalAlignment.Center });
    }
}
