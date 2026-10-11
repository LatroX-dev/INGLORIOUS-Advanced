using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InGlorious.Core;
using System.Diagnostics;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private MacroDefinition? CurrentMacro => _state.Project.Macros.FirstOrDefault(m => m.Id == _macroId);
    private Control BuildMacroWorkspace()
    {
        var panel = Column(Row(Button("Neues Makro", () =>
        {
            if (_state.Project.Macros.Count >= 32) throw new InvalidDataException(L.T("Maximal 32 Makros pro Profil."));
            var macro = new MacroDefinition { Name = L.T("Neues Makro"), Steps = [new() { Kind = "down", Key = "KC_A" }, new() { Kind = "up", Key = "KC_A" }] };
            CommitCandidate(p => p.Macros.Add(macro)); _macroId = macro.Id; Changed(); Build();
        }), AsyncButton("Makrobibliothek …", OpenMacroLibraryAsync)));
        foreach (var macro in _state.Project.Macros)
        {
            var button = AsyncButton(macro.Name + " · " + macro.Steps.Count, async () => { if (!await GuardNavigation()) return; _macroId = macro.Id; Build(); });
            button.HorizontalContentAlignment = HorizontalAlignment.Left; if (_macroId == macro.Id) button.BorderBrush = Brush("#EF646B"); panel.Children.Add(button);
        }
        if (CurrentMacro is { } selected)
        {
            panel.Children.Add(Heading("Schritte"));
            var list = new ListBox { Height = 360, ItemsSource = selected.Steps.Select((s, i) => new StepRow(i, s.Kind == "delay" ? L.F($"Pause · {s.DelayMs} ms") : BindingCatalog.FunctionLabel(s.Key) + " · " + L.T(s.Kind == "down" ? "Drücken" : "Loslassen"))).ToArray() };
            panel.Children.Add(list);
            void Move(int delta)
            {
                if (list.SelectedItem is not StepRow row) return; var destination = row.Index + delta; if (destination < 0 || destination >= selected.Steps.Count) return;
                _state.Change(p => { var steps = p.Macros.Single(m => m.Id == selected.Id).Steps; (steps[row.Index], steps[destination]) = (steps[destination], steps[row.Index]); }); Changed(); Build();
            }
            panel.Children.Add(Row(Button("↑", () => Move(-1)), Button("↓", () => Move(1)), Button("Schritt entfernen", () =>
            { if (list.SelectedItem is not StepRow row) return; _state.Change(p => p.Macros.Single(m => m.Id == selected.Id).Steps.RemoveAt(row.Index)); Changed(); Build(); })));
            var errors = ProjectService.Validate(_state.Project); if (errors.Count > 0) panel.Children.Add(Text(string.Join("\n", errors), true));
        }
        return panel;
    }
    private sealed record StepRow(int Index, string Label) { public override string ToString() => (Index + 1) + ". " + Label; }
    private void BuildMacroEditor()
    {
        if (CurrentMacro is not { } macro) { _editor.Children.Add(Text(L.T("Makro auswählen oder erstellen."), true)); return; }
        var name = Input(macro.Name);
        bool Rename() { var value = name.Text?.Trim(); if (string.IsNullOrEmpty(value)) throw new InvalidDataException(L.T("Bitte einen Namen eingeben.")); _state.Change(p => p.Macros.Single(m => m.Id == macro.Id).Name = value); return true; }
        Watch(Rename, name); _editor.Children.Add(name); _editor.Children.Add(Button("Umbenennen", () => ApplyEdited(Rename)));
        var keys = BindingCatalog.All.Where(b => MacroPhysicalKeys.IsSupported(b.Code)).ToArray(); var key = Choice(keys.Select(b => b.Label));
        var type = Choice(new[] { L.T("Tastendruck"), L.T("Drücken"), L.T("Loslassen"), L.T("Pause") }); var delay = Number(50, 0, 10000);
        _editor.Children.Add(Heading("Schritt hinzufügen")); _editor.Children.Add(key); _editor.Children.Add(type); _editor.Children.Add(delay);
        _editor.Children.Add(AsyncButton("Hinzufügen", async () =>
        {
            if (!await GuardNavigation()) return;
            var steps = new List<MacroStep>(); var code = keys[key.SelectedIndex].Code;
            if (type.SelectedIndex is 0 or 1) steps.Add(new() { Kind = "down", Key = code });
            if (type.SelectedIndex == 3) steps.Add(new() { Kind = "delay", DelayMs = (int)(delay.Value ?? 50) });
            if (type.SelectedIndex is 0 or 2) steps.Add(new() { Kind = "up", Key = code });
            if (macro.Steps.Count + steps.Count > 128) throw new InvalidDataException(L.T("Maximal 128 Schritte pro Makro."));
            _state.Change(p => p.Macros.Single(m => m.Id == macro.Id).Steps.AddRange(steps)); Changed(); Build();
        }));
        _editor.Children.Add(AsyncButton("Aufnehmen …", RecordMacroAsync));
        _editor.Children.Add(AsyncButton("Duplizieren", async () =>
        {
            if (!await GuardNavigation()) return;
            var import = SharedMacroLibrary.CopyIntoProfile(_state.Project, [SharedMacroLibrary.FromProfile(CurrentMacro!)]); OnboardSerializer.ValidateEditorProject(import.Project);
            _state.Change(p => p.Macros = import.Project.Macros); _macroId = import.AddedIds.Single(); Changed(); Build();
        }));
        _editor.Children.Add(AsyncButton("Makro entfernen", async () =>
        {
            if (!await GuardNavigation() || await Ask("Makro entfernen", L.T("Zugewiesene Tasten werden deaktiviert."), "Abbrechen", "Entfernen") != 1) return;
            _state.Change(p => { p.Macros.RemoveAll(m => m.Id == macro.Id); foreach (var layer in p.Layers) foreach (var assignment in layer.Keys.Values) if (assignment.Binding == "MACRO:" + macro.Id) assignment.Binding = "KC_NO"; }); _macroId = null; Changed(); Build();
        }));
        var targetKeys = _state.Device.Keys.ToArray(); var assignment = Choice(targetKeys.Select(k => k.AccessibleLabel));
        _editor.Children.Add(Heading("Taste zuweisen")); _editor.Children.Add(assignment);
        _editor.Children.Add(Button("Makro zuweisen", () =>
        {
            CommitCandidate(p => p.Layers[_state.LayerIndex].Keys[targetKeys[assignment.SelectedIndex].Id].Binding = "MACRO:" + macro.Id); Changed();
        }, true));
    }
    private async Task OpenMacroLibraryAsync()
    {
        if (!await GuardNavigation() || Program.Offline) return;
        var snapshot = SharedMacroLibrary.Load(); var dialog = new Window { Title = L.T("Makrobibliothek"), Width = 740, Height = 610, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { Height = 350, SelectionMode = SelectionMode.Multiple }; var feedback = Text("", true);
        Button LocalButton(string label, Action action, bool primary = false)
        {
            var button = new Button { Content = L.T(label) }; if (primary) button.Classes.Add("primary");
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { feedback.Text = ex.Message; } }; return button;
        }
        Button LocalAsyncButton(string label, Func<Task> action)
        {
            var button = new Button { Content = L.T(label) };
            button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { feedback.Text = ex.Message; } }; return button;
        }
        void Refresh() => list.ItemsSource = snapshot.Macros.Select(m => new MacroRow(m)).ToArray(); Refresh();
        var add = LocalButton("Ins Profil kopieren", () =>
        {
            var selected = list.SelectedItems?.Cast<MacroRow>().Select(r => r.Value).ToArray() ?? [];
            if (selected.Length == 0) return;
            var import = SharedMacroLibrary.CopyIntoProfile(_state.Project, selected); OnboardSerializer.ValidateEditorProject(import.Project);
            _state.Change(p => p.Macros = import.Project.Macros); _macroId = import.AddedIds.FirstOrDefault(); Changed(); dialog.Close(); Build();
        }, true);
        var save = LocalButton("Aktuelles Makro speichern", () =>
        { if (CurrentMacro is not { } current) return; snapshot = SharedMacroLibrary.Save(snapshot, SharedMacroLibrary.Merge(snapshot.Macros, [SharedMacroLibrary.FromProfile(current)])); Refresh(); });
        var importFile = LocalAsyncButton("Importieren …", async () =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = L.T("Makros importieren"), AllowMultiple = false });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            snapshot = SharedMacroLibrary.Save(snapshot, SharedMacroLibrary.Merge(snapshot.Macros, SharedMacroLibrary.ImportFile(path))); Refresh();
        });
        var export = LocalAsyncButton("Exportieren …", async () =>
        {
            var selected = list.SelectedItems?.Cast<MacroRow>().Select(r => r.Value).ToArray() ?? []; if (selected.Length == 0) return;
            var file = await dialog.StorageProvider.SaveFilePickerAsync(new() { Title = L.T("Makros exportieren"), SuggestedFileName = "macros.inglorious.json", ShowOverwritePrompt = true });
            if (file?.TryGetLocalPath() is { } path) SharedMacroLibrary.ExportFile(path, selected, true);
        });
        var delete = LocalButton("Entfernen", () => { var selected = list.SelectedItems?.Cast<MacroRow>().Select(r => r.Value).ToHashSet() ?? []; snapshot = SharedMacroLibrary.Save(snapshot, snapshot.Macros.Where(m => !selected.Contains(m))); Refresh(); });
        dialog.Content = new Border { Padding = new Thickness(22), Child = Column(list, feedback, Row(importFile, export, delete), Row(save, add, Button("Schließen", () => dialog.Close()))) };
        await dialog.ShowDialog(this);
    }
    private sealed record MacroRow(SharedMacro Value) { public override string ToString() => Value.Name + " · " + Value.Steps.Count; }
    private async Task RecordMacroAsync()
    {
        if (!await GuardNavigation() || Program.Offline || CurrentMacro is not { } macro) return;
        var dialog = new Window { Title = L.T("Makro aufnehmen"), Width = 660, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var recording = new MacroRecording(); var clock = Stopwatch.StartNew(); var status = Text(L.T("Nur Eingaben in diesem Fenster werden aufgenommen."));
        var capture = new Border { Background = Brush("#262B30"), Height = 100, Focusable = true, Child = Text(L.T("Aufnahme starten, dann Tasten drücken.")) };
        var use = Button("Übernehmen", () =>
        {
            try
            {
                recording.Stop(clock.ElapsedMilliseconds); if (recording.Steps.Count == 0) return;
                CommitCandidate(p => p.Macros.Single(m => m.Id == macro.Id).Steps = recording.Steps.Select(s => new MacroStep { Kind = s.Kind, Key = s.Key, DelayMs = s.DelayMs }).ToList()); Changed(); dialog.Close(); Build();
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }, true); use.IsEnabled = false;
        void Refresh() { status.Text = L.T(recording.Status) + " · " + recording.StepCount; use.IsEnabled = !recording.IsRecording && recording.StepCount > 0; }
        void Stop() { recording.Stop(clock.ElapsedMilliseconds); Refresh(); }
        var start = Button("Aufnahme starten", () => { recording.Start(clock.ElapsedMilliseconds); capture.Focus(); Refresh(); });
        capture.KeyDown += (_, e) => { if (!recording.IsRecording) return; var code = PhysicalCode(e.PhysicalKey); if (code is null) recording.Unsupported(e.PhysicalKey.ToString(), clock.ElapsedMilliseconds); else recording.KeyDown(code, clock.ElapsedMilliseconds); e.Handled = true; Refresh(); };
        capture.KeyUp += (_, e) => { if (!recording.IsRecording) return; var code = PhysicalCode(e.PhysicalKey); if (code is not null) recording.KeyUp(code, clock.ElapsedMilliseconds); e.Handled = true; Refresh(); };
        capture.LostFocus += (_, _) => Stop(); dialog.Deactivated += (_, _) => Stop(); dialog.Closing += (_, _) => Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; timer.Tick += (_, _) => { recording.Advance(clock.ElapsedMilliseconds); Refresh(); }; timer.Start();
        dialog.Content = new Border { Padding = new Thickness(22), Child = Column(status, capture, Row(start, Button("Stoppen", Stop), use, Button("Abbrechen", () => dialog.Close()))) };
        try { await dialog.ShowDialog(this); } finally { timer.Stop(); }
    }
    internal static string? PhysicalCode(PhysicalKey key)
    {
        var name = key.ToString();
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z') return "KC_" + name;
        if (name.StartsWith("Digit") && name.Length == 6) return "KC_" + name[^1];
        if (name.StartsWith('F') && int.TryParse(name[1..], out var number) && number is >= 1 and <= 24) return "KC_" + name;
        return name switch
        {
            "Escape" => "KC_ESC", "Enter" => "KC_ENT", "Space" => "KC_SPC", "Tab" => "KC_TAB", "Backspace" => "KC_BSPC", "Delete" => "KC_DEL", "Insert" => "KC_INS",
            "ArrowLeft" => "KC_LEFT", "ArrowRight" => "KC_RGHT", "ArrowUp" => "KC_UP", "ArrowDown" => "KC_DOWN", "Home" => "KC_HOME", "End" => "KC_END", "PageUp" => "KC_PGUP", "PageDown" => "KC_PGDN",
            "ControlLeft" => "KC_LCTL", "ControlRight" => "KC_RCTL", "ShiftLeft" => "KC_LSFT", "ShiftRight" => "KC_RSFT", "AltLeft" => "KC_LALT", "AltRight" => "KC_RALT", "MetaLeft" => "KC_LGUI", "MetaRight" => "KC_RGUI",
            "Minus" => "KC_MINS", "Equal" => "KC_EQL", "BracketLeft" => "KC_LBRC", "BracketRight" => "KC_RBRC", "Semicolon" => "KC_SCLN", "Quote" => "KC_QUOT", "Backquote" => "KC_GRV", "Backslash" => "KC_BSLS", "IntlBackslash" => "KC_NUBS", "Comma" => "KC_COMM", "Period" => "KC_DOT", "Slash" => "KC_SLSH", "CapsLock" => "KC_CAPS",
            "NumLock" => "KC_NUM", "NumPadDivide" => "KC_PSLS", "NumPadMultiply" => "KC_PAST", "NumPadSubtract" => "KC_PMNS", "NumPadAdd" => "KC_PPLS", "NumPadEnter" => "KC_PENT", "NumPadDecimal" => "KC_PDOT",
            _ when name.StartsWith("NumPad") && name.Length == 7 && char.IsDigit(name[^1]) => "KC_P" + name[^1], _ => null
        };
    }
}
