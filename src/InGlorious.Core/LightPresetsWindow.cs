using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace InGlorious.Core;

public sealed class LightPresetsWindow : Window
{
    private readonly AppProject _source;
    private readonly int _layer;
    private readonly string? _folder;
    private readonly ListBox _list = new();
    private readonly ToggleButton _showAllDevices = new() { IsChecked = false };
    private readonly TextBox _name = new() { MaxLength = 80, MinWidth = 200, Margin = new(0, 0, 8, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 44, Margin = new(0, 12, 0, 12) };
    private readonly Button _apply, _delete, _export;
    public LightingPreset? SelectedPreset { get; private set; }

    public LightPresetsWindow(AppProject project, int layer, string? folder = null)
    {
        _source = ProjectService.Clone(project); _layer = layer; _folder = folder;
        Title = L.T("Beleuchtungsvorlagen"); Width = 860; Height = 620; MinWidth = 700; MinHeight = 500;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new(20) }; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Content = root;
        var introduction = new StackPanel(); root.Children.Add(introduction);
        introduction.Children.Add(new TextBlock { Text = L.T("Eine Vorlage ersetzt nur die Beleuchtung der gewählten Ebene. Belegungen, Makros und andere Ebenen bleiben erhalten."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        _showAllDevices.Content = L.T("Alle Geräte anzeigen"); _showAllDevices.Style = (Style)FindResource("SwitchToggle");
        _showAllDevices.Width = 260; _showAllDevices.HorizontalAlignment = HorizontalAlignment.Left; _showAllDevices.Margin = new(0, 0, 0, 14);
        introduction.Children.Add(_showAllDevices);
        var save = new DockPanel { Margin = new(0, 0, 0, 14) }; Grid.SetRow(save, 1); root.Children.Add(save);
        var saveButton = Button("Aktuelle Beleuchtung speichern", () =>
        {
            var entry = LightingPresetStore.SaveToLibrary(LightingPresetStore.Capture(_source, _layer, _name.Text), _folder);
            Refresh(entry.Path); _status.Text = L.T("Beleuchtungsvorlage gespeichert.");
        }); DockPanel.SetDock(saveButton, Dock.Right); save.Children.Add(saveButton);
        _name.Text = L.T("Meine Beleuchtung"); save.Children.Add(_name);
        System.Windows.Automation.AutomationProperties.SetName(_name, L.T("Name der Beleuchtungsvorlage"));
        Grid.SetRow(_list, 2); root.Children.Add(_list);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var footer = new StackPanel(); Grid.SetRow(footer, 3); root.Children.Add(footer);
        footer.Children.Add(_status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        actions.Children.Add(Button("Importieren …", Import));
        _export = Button("Exportieren …", Export); actions.Children.Add(_export);
        _delete = Button("Löschen", Delete); actions.Children.Add(_delete);
        _apply = Button("Auf gewählte Ebene anwenden", () => { if (Current is not { } current || current.Preset.DeviceId != _source.DeviceId) return; SelectedPreset = current.Preset; DialogResult = true; });
        _apply.Style = (Style)FindResource("PrimaryButton"); actions.Children.Add(_apply);
        var close = Button("Schließen", Close); close.IsCancel = true; actions.Children.Add(close);
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("ShowAllDevicesToggle", _showAllDevices); RegisterName("PresetList", _list); RegisterName("ApplyPresetButton", _apply); RegisterName("PresetStatus", _status);
        _showAllDevices.Checked += (_, _) => Refresh(Current?.Path);
        _showAllDevices.Unchecked += (_, _) => Refresh(Current?.Path);
        _list.SelectionChanged += (_, _) => UpdateSelection(); Refresh();
    }
    private LightingPresetEntry? Current => (_list.SelectedItem as ListBoxItem)?.Tag as LightingPresetEntry;
    internal IReadOnlyList<LightingPresetEntry> VisibleEntries => _list.Items.Cast<ListBoxItem>().Select(item => (LightingPresetEntry)item.Tag).ToArray();
    private Button Button(string text, Action action)
    {
        var button = new Button { Content = L.T(text), Margin = new(0, 0, 8, 6) };
        button.Click += (_, _) => Guard(action); return button;
    }
    private void Refresh(string? selectPath = null)
    {
        var entries = LightingPresetStore.Discover(out var warning, _folder); _list.Items.Clear();
        foreach (var entry in entries.Where(entry => _showAllDevices.IsChecked == true || entry.Preset.DeviceId == _source.DeviceId))
        {
            var stack = new StackPanel { Margin = new(6) };
            stack.Children.Add(new TextBlock { Text = entry.Preset.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = entry.DeviceName + (entry.IsBuiltIn ? L.T(" · Standardvorlage") : ""), FontSize = 11, Margin = new(0, 3, 0, 0) });
            var item = new ListBoxItem { Content = stack, Tag = entry }; _list.Items.Add(item);
            if (entry.Path == selectPath) _list.SelectedItem = item;
        }
        if (_list.SelectedItem is null) _list.SelectedItem = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((LightingPresetEntry)i.Tag).Preset.DeviceId == _source.DeviceId);
        UpdateSelection(); if (warning is not null) _status.Text = warning;
    }
    private void UpdateSelection()
    {
        var current = Current; _export.IsEnabled = current is not null; _delete.IsEnabled = current is { IsBuiltIn: false };
        _apply.IsEnabled = current is not null && current.Preset.DeviceId == _source.DeviceId;
        _status.Text = current is null ? L.T("Wähle eine Beleuchtungsvorlage.") : current.Preset.DeviceId != _source.DeviceId ? L.T("Diese Beleuchtungsvorlage gehört zu einem anderen Gerätemodell.") : L.F($"Ziel: {DeviceCatalog.Get(_source.DeviceId).Name} · Ebene {_layer}");
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Title = L.T("Beleuchtungsvorlage importieren"), Filter = L.T("INGLORIOUS-Beleuchtung (*.lighting.json)|*.lighting.json|JSON (*.json)|*.json") };
        if (dialog.ShowDialog(this) != true) return;
        ImportFile(dialog.FileName);
    }
    internal void ImportFile(string path)
    {
        var preset = LightingPresetStore.Load(path);
        if (preset.DeviceId != _source.DeviceId)
            throw new InvalidDataException(L.T("Diese Beleuchtungsvorlage gehört zu einem anderen Gerätemodell."));
        var entry = LightingPresetStore.SaveToLibrary(preset, _folder); Refresh(entry.Path);
    }
    private void Export()
    {
        if (Current is not { } current) return;
        var dialog = new SaveFileDialog { Title = L.T("Beleuchtungsvorlage exportieren"), Filter = L.T("INGLORIOUS-Beleuchtung (*.lighting.json)|*.lighting.json"), FileName = "Lighting.lighting.json", DefaultExt = ".lighting.json", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        LightingPresetStore.Export(dialog.FileName, current.Preset, overwrite: true); _status.Text = L.T("Beleuchtungsvorlage exportiert.");
    }
    private void Delete()
    {
        if (Current is not { IsBuiltIn: false } current) return;
        if (MessageBox.Show(this, L.F($"Beleuchtungsvorlage „{current.Preset.Name}“ löschen? Profile bleiben unverändert."), L.T("Beleuchtungsvorlagen"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LightingPresetStore.Delete(current, _folder); Refresh(); _status.Text = L.T("Beleuchtungsvorlage gelöscht.");
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { _status.Text = ex.Message; }
    }
}
