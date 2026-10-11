using Avalonia.Controls;
using Avalonia.LogicalTree;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private async Task RunKeyBindingChecksAsync(Action<bool, string> check)
    {
        if (!Program.Offline) throw new InvalidOperationException("Key binding checks require offline mode.");
        var savedState = _state; var savedPage = _page; var savedSlider = _editingSlider;
        var savedBusy = _busy; var savedLoading = _startupLoading; var savedInitial = _initialConnectionsPending;
        var savedPending = _pendingApply; var savedNavigating = _navigating;
        try
        {
            _busy = _startupLoading = _initialConnectionsPending = _navigating = false; _pendingApply = null;
            var target = ProjectService.Create("numpad");
            var key = DeviceCatalog.Get("numpad").Keys.First(k => !k.IsEncoder).Id;
            target.Name = "Local profile"; target.Layers[0].Keys[key].Color = "#123456";
            var source = ProjectService.Create("numpad");
            source.Layers[0].Keys[key].Binding = "KC_A"; source.Layers[1].Name = "Work";
            source.Slider.Mode = 1;
            var document = KeyBindingTransfer.Capture(source);
            EditorState Fresh()
            {
                _state = new EditorState(target, Path.Combine(Path.GetTempPath(), "inglorious-bindings-check-profile.json"));
                _page = "Tastenbelegung"; Build(); return _state;
            }
            var state = Fresh(); var initial = EditorState.Fingerprint(state.Project); var profilePath = state.Path;
            foreach (var slider in new[] { false, true })
            {
                _editingSlider = slider; Build();
                var controls = this.GetLogicalDescendants().OfType<Button>().ToArray();
                check(controls.Count(b => b.Name == "ImportKeyBindings") == 1 && controls.Count(b => b.Name == "ExportKeyBindings") == 1,
                    "key binding file actions are available in the key and slider editors");
            }
            check(!await ImportKeyBindingsCoreAsync(state, initial, profilePath, document, () => Task.FromResult(false)) &&
                EditorState.Fingerprint(state.Project) == initial && !state.CanUndo,
                "canceling binding import leaves the entire editor and undo history unchanged");
            check(await ImportKeyBindingsCoreAsync(state, initial, profilePath, document, () => Task.FromResult(true)), "confirmed bindings are imported");
            check(state.Project.Layers[0].Keys[key].Binding == "KC_A" && state.Project.Layers[1].Name == "Work" && state.Project.Slider.Mode == 1 &&
                state.Project.Layers[0].Keys[key].Color == "#123456" && state.Project.Name == target.Name && state.Path == profilePath && state.Dirty,
                "binding import updates assignments, layer names and slider while preserving lighting and profile identity");
            state.Undo(); check(EditorState.Fingerprint(state.Project) == initial && !state.CanUndo && !state.Dirty,
                "one undo restores the complete pre-import profile");

            var confirmations = 0; var rejected = false;
            try { await ImportKeyBindingsCoreAsync(state, initial, profilePath, KeyBindingTransfer.Capture(ProjectService.Create("pro-iso")), () => { confirmations++; return Task.FromResult(true); }); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && confirmations == 0 && EditorState.Fingerprint(state.Project) == initial,
                "a different keyboard model is rejected before confirmation without changing the editor");

            var accepted = await ImportKeyBindingsCoreAsync(state, initial, profilePath, document, () =>
            { state.Change(p => p.Name = "Changed while choosing"); return Task.FromResult(true); });
            check(!accepted && state.Project.Name == "Changed while choosing" && state.Project.Layers[0].Keys[key].Binding == target.Layers[0].Keys[key].Binding,
                "confirmation never overwrites changes made while the import dialog was open");

            state = Fresh(); initial = EditorState.Fingerprint(state.Project);
            accepted = await ImportKeyBindingsCoreAsync(state, initial, state.Path, document, () =>
            { _state = new EditorState("pro-iso"); return Task.FromResult(true); });
            check(!accepted && EditorState.Fingerprint(state.Project) == initial && _state.Device.Id == "pro-iso",
                "switching device invalidates a pending binding import");

            state = Fresh(); initial = EditorState.Fingerprint(state.Project); var oldPath = state.Path;
            accepted = await ImportKeyBindingsCoreAsync(state, initial, oldPath, document, () =>
            { state.MarkSaved(Path.Combine(Path.GetTempPath(), "inglorious-bindings-check-renamed.json")); return Task.FromResult(true); });
            check(!accepted && EditorState.Fingerprint(state.Project) == initial, "saving under a new profile path invalidates an open binding import");
            check(IsOpenProfilePath(state.Path!), "binding export protects the current profile file");

            var pendingCalls = 0; _pendingApply = () => { pendingCalls++; return true; };
            check(!await ImportKeyBindingsCoreAsync(state, initial, state.Path, document, () => { confirmations++; return Task.FromResult(true); }) && confirmations == 0,
                "binding import refuses to overwrite pending editor input");
            await ImportKeyBindingsAsync(); await ExportKeyBindingsAsync();
            check(pendingCalls == 0 && _pendingApply is not null && EditorState.Fingerprint(state.Project) == initial,
                "offline file actions neither open user file dialogs nor apply pending editor changes");
            _pendingApply = null; _busy = true;
            check(!IsKeyBindingContextCurrent(state, initial, state.Path), "active device operations block binding import and export");
            _busy = false; _startupLoading = true;
            check(!IsKeyBindingContextCurrent(state, initial, state.Path), "device startup reads block binding import and export");
        }
        finally
        {
            _state = savedState; _page = savedPage; _editingSlider = savedSlider;
            _busy = savedBusy; _startupLoading = savedLoading; _initialConnectionsPending = savedInitial;
            _navigating = savedNavigating; Build(); _pendingApply = savedPending;
        }
    }
}
