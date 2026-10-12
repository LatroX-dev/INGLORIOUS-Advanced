using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private void LightingPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_readingDevice || !ResolvePendingEdits()) return;
        CommitNames();
        try
        {
            var dialog = new LightPresetsWindow(_project, _layerIndex) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedPreset is not { } preset) return;
            ApplyLightingPreset(preset);
        }
        catch (Exception ex) { ShowError(L.T("Beleuchtungsvorlage konnte nicht übernommen werden."), ex); }
    }
    private void ApplyLightingPreset(LightingPreset preset)
    {
        var candidate = LightingPresetStore.Apply(_project, _layerIndex, preset);
        OnboardSerializer.ValidateEditorProject(candidate);
        if (Fingerprint(candidate) != Fingerprint(_project))
        { BeginChange(); _project = candidate; }
        // A preset applies to the complete layer. Show its actual effects instead
        // of leaving the preview in the colors-only editor mode.
        _activeTab = 0; _effectForSelection = false;
        _showLightingEffects = Layer.LightingEffect.Mode != 0 || Layer.LightingEffect.Brightness != 255 || Layer.LedEffects.Count > 0;
        RefreshAll();
        FinishChange(L.F($"Beleuchtungsvorlage „{preset.Name}“ auf Ebene {_layerIndex} übernommen."), false);
    }
}
