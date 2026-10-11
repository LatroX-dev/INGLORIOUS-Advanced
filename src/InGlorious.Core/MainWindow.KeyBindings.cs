using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _keyBindingTransferBusy;
    private sealed record KeyBindingContext(AppProject Project, string Fingerprint, string? Path, string ProjectName, string MacroName);

    private bool CanTransferKeyBindings => !_readingDevice && !_checkingFirmwareGuide && !_startupLoading &&
        !_startupFinishing && !_startupClosed && !_scanning && _pendingSoftwareInstallation is null && IsEnabled && EditorSurface.IsEnabled;

    private KeyBindingContext CaptureKeyBindingContext() => new(_project, Fingerprint(_project), _projectPath, ProjectName.Text, MacroName.Text);

    private bool IsKeyBindingContextCurrent(KeyBindingContext context) => CanTransferKeyBindings && !HasPendingEdits &&
        ReferenceEquals(_project, context.Project) && Fingerprint(_project) == context.Fingerprint && _projectPath == context.Path &&
        ProjectName.Text == context.ProjectName && MacroName.Text == context.MacroName;

    private void ImportKeyBindings_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshotMode || _keyBindingTransferBusy || !CanTransferKeyBindings) return;
        _keyBindingTransferBusy = true;
        try
        {
            var startingProject = _project;
            if (!ResolvePendingEdits() || !ReferenceEquals(_project, startingProject) || !CanTransferKeyBindings) return;
            CommitNames();
            var context = CaptureKeyBindingContext();
            var picker = new OpenFileDialog
            {
                Title = L.T("Tastenbelegung importieren"), Filter = L.T("Tastenbelegung") + " (*.bindings.json)|*.bindings.json",
                Multiselect = false, CheckFileExists = true
            };
            if (picker.ShowDialog(this) != true || !IsKeyBindingContextCurrent(context)) return;
            var document = KeyBindingTransfer.Load(picker.FileName);
            ImportKeyBindingsCore(context, document, () => SelectKeyBindingLayers(true,
                document.Layers.Select(layer => (layer.Index, layer.Name)).ToArray(), document.Slider is not null));
        }
        catch (InvalidDataException ex) { ShowError(ex.Message); }
        catch (Exception) { ShowError(L.T("Die Tastenbelegung konnte nicht importiert werden.")); }
        finally { _keyBindingTransferBusy = false; }
    }

    private KeyBindingSelection? SelectKeyBindingLayers(bool importing, IReadOnlyList<(int Index, string Name)> layers, bool canIncludeSlider)
    {
        var dialog = new KeyBindingSelectionWindow(importing, layers, canIncludeSlider) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    private bool ImportKeyBindingsCore(KeyBindingContext context, KeyBindingDocument document, Func<KeyBindingSelection?> select)
    {
        if (!IsKeyBindingContextCurrent(context)) return false;
        if (document.DeviceId != _project.DeviceId)
            throw new InvalidDataException(L.T("Diese Tastenbelegungen gehören zu einem anderen Gerätemodell oder Layout."));
        var selection = select();
        if (selection is null || !IsKeyBindingContextCurrent(context)) return false;
        var candidate = KeyBindingTransfer.Apply(_project, document, selection.Layers, selection.IncludeSlider);
        if (Fingerprint(candidate) != context.Fingerprint)
        {
            BeginChange(); _project = candidate; _macroAssigning = false;
            RefreshAll(); CaptureSession();
        }
        FinishChange(L.T("Tastenbelegung importiert."), false);
        return true;
    }

    private KeyBindingDocument? CaptureSelectedKeyBindings(KeyBindingContext context, Func<KeyBindingSelection?> select)
    {
        if (!IsKeyBindingContextCurrent(context)) return null;
        var selection = select();
        return selection is not null && IsKeyBindingContextCurrent(context)
            ? KeyBindingTransfer.Capture(_project, selection.Layers, selection.IncludeSlider) : null;
    }

    private bool IsKeyBindingExportProfilePath(string path) => ProfileLibrary.PathsEqual(path, _projectPath) ||
        _sessions.Values.Any(session => ProfileLibrary.PathsEqual(path, session.Path));

    private void ExportKeyBindings_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshotMode || _keyBindingTransferBusy || !CanTransferKeyBindings) return;
        _keyBindingTransferBusy = true;
        try
        {
            var startingProject = _project;
            if (!ResolvePendingEdits() || !ReferenceEquals(_project, startingProject) || !CanTransferKeyBindings) return;
            CommitNames();
            var context = CaptureKeyBindingContext();
            var document = CaptureSelectedKeyBindings(context, () => SelectKeyBindingLayers(false,
                _project.Layers.Select((layer, index) => (index, layer.Name)).ToArray(), Device.HasSlider));
            if (document is null) return;
            var picker = new SaveFileDialog
            {
                Title = L.T("Tastenbelegung exportieren"), Filter = L.T("Tastenbelegung") + " (*.bindings.json)|*.bindings.json",
                FileName = KeyBindingTransfer.SuggestFileName(_project), DefaultExt = ".bindings.json", AddExtension = true, OverwritePrompt = true
            };
            if (picker.ShowDialog(this) != true || !IsKeyBindingContextCurrent(context)) return;
            if (IsKeyBindingExportProfilePath(picker.FileName))
                throw new InvalidDataException(L.T("Wähle für die Tastenbelegung eine andere Datei als dein Profil."));
            KeyBindingTransfer.Export(picker.FileName, document, overwrite: true);
            Status(L.T("Tastenbelegung exportiert."));
        }
        catch (InvalidDataException ex) { ShowError(ex.Message); }
        catch (Exception) { ShowError(L.T("Die Tastenbelegung konnte nicht exportiert werden.")); }
        finally { _keyBindingTransferBusy = false; }
    }
}
