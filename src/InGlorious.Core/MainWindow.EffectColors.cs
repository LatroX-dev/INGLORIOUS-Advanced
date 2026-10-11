using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InGlorious.Core;

public partial class MainWindow
{
    private List<string> _effectColors = [];
    private int _selectedEffectColor;
    private void SetEffectColors(IEnumerable<string> colors)
    { _effectColors = colors.ToList(); _selectedEffectColor = Math.Clamp(_selectedEffectColor, 0, Math.Max(0, _effectColors.Count - 1)); RenderEffectColors(); }
    private void RenderEffectColors()
    {
        if (EffectColorChips is null) return;
        EffectColorChips.Children.Clear();
        for (var i = 0; i < _effectColors.Count; ++i)
        {
            var index = i;
            var button = new Button { Content = (i + 1).ToString(), Background = Brush(_effectColors[i]), Foreground = Brush(PreviewRgb.FromHex(_effectColors[i]) is var rgb && rgb.R + rgb.G + rgb.B > 420 ? "#171A1D" : "#FFFFFF"),
                Width = 40, Height = 34, Padding = new Thickness(0), Margin = new Thickness(0, 0, 5, 5),
                BorderBrush = Brush(i == _selectedEffectColor ? "#FFFFFF" : "#454A52"), BorderThickness = new Thickness(i == _selectedEffectColor ? 2 : 1), ToolTip = _effectColors[i] };
            button.Click += (_, _) =>
            {
                _selectedEffectColor = index; var picker = new ColorPickerWindow(_effectColors[index], _palettePath) { Owner = this };
                var accepted = picker.ShowDialog() == true; LoadColorPalette();
                if (accepted) { _effectColors[index] = picker.SelectedHex; EffectColorsChanged(); } else RenderEffectColors();
            };
            EffectColorChips.Children.Add(button);
        }
        EffectAddColorButton.IsEnabled = _effectColors.Count < 8;
        EffectRemoveColorButton.IsEnabled = _effectColors.Count > 0;
        EffectColorLeftButton.IsEnabled = _selectedEffectColor > 0;
        EffectColorRightButton.IsEnabled = _selectedEffectColor + 1 < _effectColors.Count;
        var wasRefreshing = _refreshingEffects; _refreshingEffects = true;
        try { EffectColorPreset.ItemsSource = _customColors; EffectColorPreset.SelectedIndex = -1; }
        finally { _refreshingEffects = wasRefreshing; }
        EffectColorPreset.Visibility = _customColors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EffectColorsHint.Text = _effectColors.Count == 0 ? L.T("Ohne eigene Farben läuft der volle Farbkreis.") : L.T("Die Farben werden der Reihe nach weich überblendet.");
    }
    private void EffectColorsChanged()
    { RenderEffectColors(); UpdatePendingEditsHint(); RefreshLightingPreview(); }
    private void AddEffectColor_Click(object sender, RoutedEventArgs e)
    {
        if (_effectColors.Count >= 8) return;
        var picker = new ColorPickerWindow(EffectColorPreset.SelectedItem as string ?? (_effectColors.Count == 0 ? ColorHex.Text : _effectColors[^1]), _palettePath) { Owner = this };
        var accepted = picker.ShowDialog() == true; LoadColorPalette(); RenderEffectColors();
        if (!accepted) return;
        _effectColors.Add(picker.SelectedHex); _selectedEffectColor = _effectColors.Count - 1; EffectColorsChanged();
    }
    private void EffectColorPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingEffects || EffectColorPreset.SelectedItem is not string color || _effectColors.Count >= 8) return;
        _effectColors.Add(color); _selectedEffectColor = _effectColors.Count - 1; EffectColorsChanged();
    }
    private void RemoveEffectColor_Click(object sender, RoutedEventArgs e)
    { if (_effectColors.Count == 0) return; _effectColors.RemoveAt(_selectedEffectColor); _selectedEffectColor = Math.Max(0, _selectedEffectColor - 1); EffectColorsChanged(); }
    private void MoveEffectColor_Click(object sender, RoutedEventArgs e)
    {
        var next = _selectedEffectColor + (ReferenceEquals(sender, EffectColorLeftButton) ? -1 : 1);
        if (next < 0 || next >= _effectColors.Count) return;
        (_effectColors[next], _effectColors[_selectedEffectColor]) = (_effectColors[_selectedEffectColor], _effectColors[next]);
        _selectedEffectColor = next; EffectColorsChanged();
    }
}
