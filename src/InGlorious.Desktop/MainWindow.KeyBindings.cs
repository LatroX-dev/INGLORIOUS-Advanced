using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private static FilePickerFileType KeyBindingFileType() => new(L.T("Tastenbelegung") + " (*.bindings.json)")
    { Patterns = ["*.bindings.json"] };

    private bool IsKeyBindingContextCurrent(EditorState state, string fingerprint, string? profilePath) =>
        ReferenceEquals(_state, state) && !_busy && !_startupLoading && !_initialConnectionsPending &&
        !_startupWindowClosed && !_allowClose && _helpWindow is null && _pendingApply is null && state.Path == profilePath && EditorState.Fingerprint(state.Project) == fingerprint;

    private async Task ImportKeyBindingsAsync()
    {
        if (Program.Offline || _startupLoading || _startupWindowClosed || _allowClose || _helpWindow is not null || !await GuardNavigation()) return;
        var state = _state;
        var fingerprint = EditorState.Fingerprint(state.Project);
        var profilePath = state.Path;
        _navigating = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Tastenbelegung importieren"), AllowMultiple = false,
                FileTypeFilter = [KeyBindingFileType()]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path || !IsKeyBindingContextCurrent(state, fingerprint, profilePath)) return;
            var document = KeyBindingTransfer.Load(path);
            await ImportKeyBindingsCoreAsync(state, fingerprint, profilePath, document, () =>
                ShowKeyBindingSelectionAsync(true, document.Layers.Select(layer => (layer.Index, layer.Name)).ToArray(), document.Slider is not null));
        }
        catch (InvalidDataException ex) { Status(ex.Message); }
        catch (Exception) { Status(L.T("Die Tastenbelegung konnte nicht importiert werden.")); }
        finally { _navigating = false; }
    }

    private async Task<bool> ImportKeyBindingsCoreAsync(EditorState state, string fingerprint, string? profilePath, KeyBindingDocument document, Func<Task<KeyBindingSelection?>> choose)
    {
        if (!IsKeyBindingContextCurrent(state, fingerprint, profilePath)) return false;
        if (state.Device.Id != document.DeviceId)
            throw new InvalidDataException(L.T("Diese Tastenbelegungen gehören zu einem anderen Gerätemodell oder Layout."));
        var selection = await choose();
        if (selection is null || !IsKeyBindingContextCurrent(state, fingerprint, profilePath)) return false;
        var candidate = KeyBindingTransfer.Apply(state.Project, document, selection.Layers, selection.IncludeSlider);
        state.Change(project => { project.Layers = candidate.Layers; project.Slider = candidate.Slider; project.Macros = candidate.Macros; });
        Changed(); Build(); Status(L.T("Tastenbelegung importiert."));
        return true;
    }

    private Task<KeyBindingSelection?> ShowKeyBindingSelectionAsync(bool importing, IReadOnlyList<(int Index, string Name)> layers, bool canIncludeSlider) =>
        Program.Offline ? Task.FromResult<KeyBindingSelection?>(null) :
        CreateKeyBindingSelectionWindow(importing, layers, canIncludeSlider).ShowDialog<KeyBindingSelection?>(this);

    private static KeyBindingSelection ReadKeyBindingSelection(IEnumerable<CheckBox> layers, CheckBox? slider) =>
        new(layers.Where(layer => layer.IsChecked == true).Select(layer => (int)layer.Tag!).ToArray(), slider?.IsChecked == true);

    private Window CreateKeyBindingSelectionWindow(bool importing, IReadOnlyList<(int Index, string Name)> layers, bool canIncludeSlider)
    {
        var title = L.T(importing ? "Tastenbelegung importieren" : "Tastenbelegung exportieren");
        var dialog = new Window
        {
            Title = title, Width = 580, MaxHeight = 680, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#141618")
        };
        var heading = Text(title, size: 21); heading.FontWeight = FontWeight.SemiBold;
        var description = Text(L.T(importing ? "Nur die ausgewählten Ebenen werden ersetzt. Farben und Effekte bleiben erhalten." : "Wähle die Ebenen für den Export."), true);
        var choices = layers.OrderBy(layer => layer.Index).Select(layer => new CheckBox
        {
            Name = "KeyBindingLayer" + layer.Index, Tag = layer.Index, IsChecked = true,
            Content = Text(L.F($"Ebene {layer.Index} · {layer.Name}")), HorizontalAlignment = HorizontalAlignment.Stretch
        }).ToArray();
        var layerList = new StackPanel { Spacing = 6 };
        foreach (var checkbox in choices) layerList.Children.Add(checkbox);
        var body = Column(heading, description,
            new Border { Background = Brush("#1C1F22"), Padding = new Thickness(14, 8), Margin = new Thickness(0, 10, 0, 0), Child = layerList });
        CheckBox? slider = null;
        if (canIncludeSlider)
        {
            slider = new CheckBox { Name = "KeyBindingSlider", IsChecked = false, Content = L.T("Schieberegler einbeziehen"), Margin = new Thickness(0, 10, 0, 0) };
            body.Children.Add(slider); body.Children.Add(Text(L.T("Gilt für alle Ebenen."), true, 11));
        }
        var cancel = new Button { Content = ButtonContent("Abbrechen"), IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();
        var apply = new Button { Name = "ConfirmKeyBindingSelection", Content = ButtonContent(importing ? "Übernehmen" : "Exportieren …") };
        apply.Classes.Add("primary");
        void UpdateSelection() => apply.IsEnabled = choices.Any(choice => choice.IsChecked == true);
        foreach (var checkbox in choices) checkbox.PropertyChanged += (_, e) => { if (e.Property.Name == "IsChecked") UpdateSelection(); };
        apply.Click += (_, _) => { if (apply.IsEnabled) dialog.Close(ReadKeyBindingSelection(choices, slider)); };
        UpdateSelection();
        var actions = Row(cancel, apply); actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(actions); body.Margin = new Thickness(24);
        dialog.Content = new ScrollViewer { Content = body };
        return dialog;
    }

    private bool IsOpenProfilePath(string path) => ProfileLibrary.PathsEqual(path, _state.Path) ||
        _sessions.Values.Any(session => ProfileLibrary.PathsEqual(path, session.Path));

    private async Task ExportKeyBindingsAsync()
    {
        if (Program.Offline || _startupLoading || _startupWindowClosed || _allowClose || _helpWindow is not null || !await GuardNavigation()) return;
        var state = _state;
        var fingerprint = EditorState.Fingerprint(state.Project);
        var profilePath = state.Path;
        _navigating = true;
        try
        {
            var selection = await ShowKeyBindingSelectionAsync(false, state.Project.Layers.Select((layer, index) => (index, layer.Name)).ToArray(), state.Device.HasSlider);
            if (selection is null || !IsKeyBindingContextCurrent(state, fingerprint, profilePath)) return;
            var document = KeyBindingTransfer.Capture(state.Project, selection.Layers, selection.IncludeSlider);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Tastenbelegung exportieren"), SuggestedFileName = KeyBindingTransfer.SuggestFileName(state.Project),
                DefaultExtension = "bindings.json", ShowOverwritePrompt = true, FileTypeChoices = [KeyBindingFileType()]
            });
            if (file?.TryGetLocalPath() is not { } path || !IsKeyBindingContextCurrent(state, fingerprint, profilePath)) return;
            if (IsOpenProfilePath(path)) throw new InvalidDataException(L.T("Wähle für die Tastenbelegung eine andere Datei als dein Profil."));
            KeyBindingTransfer.Export(path, document, overwrite: true);
            Status(L.T("Tastenbelegung exportiert."));
        }
        catch (InvalidDataException ex) { Status(ex.Message); }
        catch (Exception) { Status(L.T("Die Tastenbelegung konnte nicht exportiert werden.")); }
        finally { _navigating = false; }
    }
}
