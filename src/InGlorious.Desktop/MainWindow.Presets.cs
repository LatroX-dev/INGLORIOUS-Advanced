using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private void CheckPresetDevice(LightingPreset preset)
    {
        if (preset.DeviceId != _state.Device.Id) throw new InvalidDataException(L.T("Diese Beleuchtungsvorlage gehört zu einem anderen Gerätemodell."));
    }
    private LightingPresetEntry[] CurrentDevicePresets(IEnumerable<LightingPresetEntry> entries) => entries.Where(e => e.Preset.DeviceId == _state.Device.Id).ToArray();
    private void ApplyLightingPreset(LightingPreset preset)
    {
        CheckPresetDevice(preset);
        var candidate = LightingPresetStore.Apply(_state.Project, _state.LayerIndex, preset);
        OnboardSerializer.ValidateEditorProject(candidate);
        _state.Change(p => p.Layers = candidate.Layers);
        _page = "Beleuchtung"; _showStandby = false; _globalEffect = true;
        _showEffects = _state.Layer.LightingEffect.Mode != 0 || _state.Layer.LightingEffect.Brightness != 255 || _state.Layer.LedEffects.Count > 0;
        Changed(); Build();
    }
    private async Task OpenLightingPresetsAsync()
    {
        if (!await GuardNavigation() || Program.Offline) return;
        await CreateLightingPresetsWindow().ShowDialog(this);
    }
    private Window CreateLightingPresetsWindow(IReadOnlyList<LightingPresetEntry>? offlineEntries = null)
    {
        var window = new Window { Title = L.T("Beleuchtungsvorlagen"), Width = 780, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { Name = "LightingPresetList", Height = 280 }; var name = Input(_state.Project.Name); var status = Text("", true); status.Name = "LightingPresetStatus";
        var showAll = new CheckBox { Name = "ShowAllPresetDevices", Content = L.T("Alle Geräte anzeigen"), IsChecked = false };
        var apply = new Button { Name = "ApplyLightingPreset", Content = L.T("Anwenden"), IsEnabled = false }; apply.Classes.Add("primary");
        var delete = new Button { Content = L.T("Entfernen"), IsEnabled = false };
        string? warning = null;
        void UpdateSelection()
        {
            var row = list.SelectedItem as PresetRow;
            apply.IsEnabled = row is not null && row.Entry.Preset.DeviceId == _state.Device.Id;
            delete.IsEnabled = row is { Entry.IsBuiltIn: false };
            status.Text = row is not null && !apply.IsEnabled ? L.T("Diese Beleuchtungsvorlage gehört zu einem anderen Gerätemodell.") : warning ?? "";
        }
        void Reload()
        {
            var selected = (list.SelectedItem as PresetRow)?.Entry;
            var entries = offlineEntries ?? LightingPresetStore.Discover(out warning);
            var rows = (showAll.IsChecked == true ? entries : CurrentDevicePresets(entries)).Select(e => new PresetRow(e)).ToArray();
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(r => r.Entry == selected) ?? rows.FirstOrDefault(r => r.Entry.Preset.DeviceId == _state.Device.Id);
            UpdateSelection();
        }
        showAll.IsCheckedChanged += (_, _) => Reload(); list.SelectionChanged += (_, _) => UpdateSelection();
        void Failure(Exception ex) => status.Text = ex.Message;
        void Safe(Action action) { try { action(); } catch (Exception ex) { Failure(ex); } }
        apply.Click += (_, _) => Safe(() =>
        {
            if (list.SelectedItem is not PresetRow row) return;
            ApplyLightingPreset(row.Entry.Preset); window.Close();
        });
        var save = new Button { Content = L.T("Als Vorlage speichern") };
        save.Click += (_, _) => Safe(() => { if (Program.Offline) return; LightingPresetStore.SaveToLibrary(LightingPresetStore.Capture(_state.Project, _state.LayerIndex, name.Text ?? "")); Reload(); });
        var import = new Button { Content = L.T("Importieren …") };
        import.Click += async (_, _) =>
        {
            if (Program.Offline) return;
            try
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(new() { Title = L.T("Beleuchtungsvorlage importieren"), AllowMultiple = false });
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
                {
                    var preset = LightingPresetStore.Load(path); CheckPresetDevice(preset);
                    LightingPresetStore.SaveToLibrary(preset); Reload();
                }
            }
            catch (Exception ex) { Failure(ex); }
        };
        var export = new Button { Content = L.T("Exportieren …") };
        export.Click += async (_, _) =>
        {
            if (Program.Offline) return;
            if (list.SelectedItem is not PresetRow row) return;
            try
            {
                var file = await window.StorageProvider.SaveFilePickerAsync(new() { Title = L.T("Beleuchtungsvorlage exportieren"), SuggestedFileName = "lighting.inglorious.json", ShowOverwritePrompt = true });
                if (file?.TryGetLocalPath() is { } path) LightingPresetStore.Export(path, row.Entry.Preset, true);
            }
            catch (Exception ex) { Failure(ex); }
        };
        delete.Click += (_, _) => Safe(() => { if (Program.Offline) return; if (list.SelectedItem is PresetRow row) { LightingPresetStore.Delete(row.Entry); Reload(); } });
        Reload(); window.Content = new Border { Padding = new Thickness(20), Child = Column(showAll, list, status, Row(import, export, delete), name, Row(save, apply, Button("Schließen", () => window.Close()))) };
        return window;
    }
    private sealed record PresetRow(LightingPresetEntry Entry) { public override string ToString() => Entry.Preset.Name + " · " + Entry.DeviceName; }
}
