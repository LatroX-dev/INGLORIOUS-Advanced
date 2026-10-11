using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace InGlorious.Core;

public sealed class MacroLibraryWindow : Window
{
    private MacroLibrarySnapshot _snapshot;
    private readonly ListBox _profileList = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _libraryList = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 42, Margin = new(0, 14, 0, 8) };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 28, Margin = new(0, 8, 0, 0) };
    private readonly List<Button> _profileActions = [], _libraryActions = [];
    public IReadOnlyList<SharedMacro>? SelectedMacros { get; private set; }

    public MacroLibraryWindow(IReadOnlyList<MacroDefinition> profileMacros, string profileName, string? libraryPath = null)
    {
        _snapshot = SharedMacroLibrary.Load(libraryPath);
        Title = L.T("Makrobibliothek"); Width = 980; Height = 680; MinWidth = 800; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        var root = new Grid { Margin = new(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var introduction = new TextBlock
        {
            Text = L.T("Makros für alle Geräte. Ins Profil werden unabhängige Kopien übernommen; spätere Bibliotheksänderungen ändern das Profil nicht."),
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16)
        };
        root.Children.Add(introduction);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new(18) }); columns.ColumnDefinitions.Add(new());
        Grid.SetRow(columns, 1); root.Children.Add(columns);
        var left = MakeColumn(L.F($"Dieses Profil: {profileName}"), _profileList, out var leftActions);
        var right = MakeColumn(L.T("Zentrale Bibliothek"), _libraryList, out var rightActions);
        columns.Children.Add(left); Grid.SetColumn(right, 2); columns.Children.Add(right);
        foreach (var macro in profileMacros)
        {
            // A profile may still contain an unfinished draft. Validate only the explicitly selected macros.
            _profileList.Items.Add(Item(macro.Name, macro.Steps?.Count ?? 0, macro));
        }
        _profileActions.Add(AddButton(leftActions, "In Bibliothek kopieren", () =>
        {
            var selected = ProfileSelection(); var merged = SharedMacroLibrary.Merge(_snapshot.Macros, selected);
            var added = merged.Count - _snapshot.Macros.Count;
            if (added > 0) _snapshot = SharedMacroLibrary.Save(_snapshot, merged);
            RefreshLibrary(); _status.Text = L.F($"{added} Makros in der Bibliothek gespeichert. Gleiche Einträge werden nicht doppelt angelegt.");
        }));
        _profileActions.Add(AddButton(leftActions, "Exportieren …", () => Export(ProfileSelection())));
        AddButton(rightActions, "Importieren …", Import);
        _libraryActions.Add(AddButton(rightActions, "Exportieren …", () => Export(LibrarySelection())));
        _libraryActions.Add(AddButton(rightActions, "Entfernen", Remove));
        _profileList.SelectionChanged += (_, _) => RefreshSelection(); _libraryList.SelectionChanged += (_, _) => RefreshSelection();
        var footer = new StackPanel(); Grid.SetRow(footer, 2); root.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = L.T("Klicken wählt mehrere Makros aus. Import und Export enthalten keine Tastenbelegungen."), Style = (Style)FindResource("HelpText"), Margin = new(0, 12, 0, 0) });
        footer.Children.Add(_details); footer.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var accept = AddButton(buttons, "Auswahl ins Profil kopieren", () =>
        {
            var selected = LibrarySelection(); if (selected.Count == 0) return;
            SelectedMacros = selected.Select(SharedMacroLibrary.Clone).ToArray(); DialogResult = true;
        });
        accept.Style = (Style)FindResource("PrimaryButton"); _libraryActions.Add(accept);
        var close = AddButton(buttons, "Schließen", Close); close.IsCancel = true;
        footer.Children.Add(buttons); Content = root; RefreshLibrary(); RefreshSelection();
    }

    private static Grid MakeColumn(string heading, ListBox list, out WrapPanel actions)
    {
        var column = new Grid(); column.RowDefinitions.Add(new() { Height = GridLength.Auto }); column.RowDefinitions.Add(new()); column.RowDefinitions.Add(new() { Height = GridLength.Auto });
        column.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled); Grid.SetRow(list, 1); column.Children.Add(list);
        actions = new WrapPanel { Margin = new(0, 8, 0, 0) }; Grid.SetRow(actions, 2); column.Children.Add(actions); return column;
    }
    private Button AddButton(Panel panel, string source, Action action)
    {
        var button = new Button { Content = L.T(source), Margin = new(0, 0, 6, 6) };
        button.Click += (_, _) => Guard(action); panel.Children.Add(button); return button;
    }
    private static ListBoxItem Item(string name, int count, object value)
    {
        var text = new StackPanel { Margin = new(6) };
        text.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = L.F($"{count} Schritte"), FontSize = 11, Margin = new(0, 3, 0, 0) });
        return new() { Content = text, Tag = value, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
    private List<SharedMacro> ProfileSelection() => _profileList.SelectedItems.Cast<ListBoxItem>().Select(i => SharedMacroLibrary.FromProfile((MacroDefinition)i.Tag)).ToList();
    private List<SharedMacro> LibrarySelection() => _libraryList.SelectedItems.Cast<ListBoxItem>().Select(i => (SharedMacro)i.Tag).ToList();
    private void RefreshLibrary()
    {
        _libraryList.Items.Clear(); foreach (var macro in _snapshot.Macros) _libraryList.Items.Add(Item(macro.Name, macro.Steps.Count, macro));
        RefreshSelection();
    }
    private void RefreshSelection()
    {
        foreach (var action in _profileActions) action.IsEnabled = _profileList.SelectedItems.Count > 0;
        foreach (var action in _libraryActions) action.IsEnabled = _libraryList.SelectedItems.Count > 0;
        _details.Text = L.F($"Ausgewählt: {_profileList.SelectedItems.Count} im Profil · {_libraryList.SelectedItems.Count} in der Bibliothek");
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Title = L.T("Makros importieren"), Filter = L.T("INGLORIOUS-Makros (*.macros.json)|*.macros.json|JSON (*.json)|*.json"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var incoming = SharedMacroLibrary.ImportFile(dialog.FileName);
        var merged = SharedMacroLibrary.Merge(_snapshot.Macros, incoming); var added = merged.Count - _snapshot.Macros.Count;
        if (added > 0) _snapshot = SharedMacroLibrary.Save(_snapshot, merged);
        RefreshLibrary(); _status.Text = L.F($"{added} Makros in der Bibliothek gespeichert. Gleiche Einträge werden nicht doppelt angelegt.");
    }
    private void Export(List<SharedMacro> selected)
    {
        if (selected.Count == 0) return;
        var dialog = new SaveFileDialog { Title = L.T("Makros exportieren"), Filter = L.T("INGLORIOUS-Makros (*.macros.json)|*.macros.json"), FileName = "Macros.macros.json", AddExtension = true, DefaultExt = ".macros.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        SharedMacroLibrary.ExportFile(dialog.FileName, selected, overwrite: true, library: _snapshot);
        _status.Text = L.F($"{selected.Count} Makros exportiert.");
    }
    private void Remove()
    {
        var selected = LibrarySelection(); if (selected.Count == 0) return;
        if (MessageBox.Show(this, L.F($"{selected.Count} Makros aus der Bibliothek entfernen? Kopien in Profilen bleiben erhalten."), L.T("Makrobibliothek"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var remaining = _snapshot.Macros.Where(m => !selected.Contains(m)).ToList();
        _snapshot = SharedMacroLibrary.Save(_snapshot, remaining); RefreshLibrary();
        _status.Text = L.T("Aus der Bibliothek entfernt. Profile bleiben unverändert.");
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { _status.Text = ex.Message; }
    }
}
