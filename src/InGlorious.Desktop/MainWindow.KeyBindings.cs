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
            await ImportKeyBindingsCoreAsync(state, fingerprint, profilePath, document, async () =>
                await Ask("Tastenbelegung importieren", L.T("Alle vier Ebenen und Reglerbelegungen importieren? Farben und Effekte bleiben erhalten. Zugewiesene Makros werden ergänzt."),
                    "Abbrechen", "Übernehmen") == 1);
        }
        catch (InvalidDataException ex) { Status(ex.Message); }
        catch (Exception) { Status(L.T("Die Tastenbelegung konnte nicht importiert werden.")); }
        finally { _navigating = false; }
    }

    private async Task<bool> ImportKeyBindingsCoreAsync(EditorState state, string fingerprint, string? profilePath, KeyBindingDocument document, Func<Task<bool>> confirm)
    {
        if (!IsKeyBindingContextCurrent(state, fingerprint, profilePath)) return false;
        var candidate = KeyBindingTransfer.Apply(state.Project, document);
        if (!await confirm() || !IsKeyBindingContextCurrent(state, fingerprint, profilePath)) return false;
        state.Change(project => { project.Layers = candidate.Layers; project.Slider = candidate.Slider; project.Macros = candidate.Macros; });
        Changed(); Build(); Status(L.T("Tastenbelegung importiert."));
        return true;
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
            var document = KeyBindingTransfer.Capture(state.Project);
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
