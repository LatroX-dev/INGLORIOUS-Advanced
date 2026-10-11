using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace InGlorious.Core;

public partial class MainWindow
{
    private void FavoriteColor_TextChanged(object sender, TextChangedEventArgs e) => UpdateFavoriteSelection();
    private void UpdateFavoriteSelection()
    {
        if (CustomColorSwatches is null || SaveColorButton is null || DeleteFavoriteButton is null) return;
        string? selected = null;
        try { selected = ColorPaletteStore.NormalizeColor(ColorHex.Text); } catch (InvalidDataException) { }
        SaveColorButton.IsEnabled = _paletteError is null && selected is not null && !_customColors.Contains(selected);
        DeleteFavoriteButton.IsEnabled = _paletteError is null && selected is not null && _customColors.Contains(selected);
        foreach (var swatch in CustomColorSwatches.Children.OfType<Button>())
        {
            var active = swatch.Tag is string color && color == selected;
            swatch.BorderBrush = Brush(active ? "#EF646B" : "#353A40");
            swatch.BorderThickness = new Thickness(active ? 2 : 1);
            if (swatch.Content is Border sample)
            {
                var rgb = ParseColor((string)swatch.Tag);
                sample.Child = active ? new TextBlock { Text = "✓", FontSize = 15, FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(.2126 * rgb.R + .7152 * rgb.G + .0722 * rgb.B > 145 ? "#141618" : "#FFFFFF"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null;
            }
        }
    }
    private void DeleteFavorite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var color = ColorPaletteStore.NormalizeColor(ColorHex.Text);
            if (!_customColors.Contains(color)) { Status(L.T("Wähle zuerst eine gespeicherte Favoritenfarbe.")); return; }
            RemoveFavorite(color);
        }
        catch (InvalidDataException ex) { Status(ex.Message); }
    }
    private void RemoveFavorite(string color)
    {
        try
        {
            _customColors = ColorPaletteStore.Remove(_palettePath, color); _paletteError = null;
            RenderColorPalette(); RenderEffectColors(); Status(L.T("Favorit gelöscht. Farben im Profil bleiben unverändert."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { Status(ex.Message); }
    }
}
