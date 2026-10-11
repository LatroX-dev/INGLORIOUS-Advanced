using System.Text.Json;
using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private void AssignOsAction_Click(object sender, RoutedEventArgs e) => SelectOsAction(assign: true);
    private void AddOsMacro_Click(object sender, RoutedEventArgs e) => SelectOsAction(assign: false);
    private void SelectOsAction(bool assign)
    {
        if (_readingDevice || !ResolvePendingEdits() || assign && !RequireSelection()) return;
        var dialog = new OsActionsWindow(macrosOnly: !assign) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedAction is not { } action) return;
        try
        {
            var candidate = ProjectService.Clone(_project);
            string binding;
            if (action.DirectBinding is { } direct) binding = direct;
            else
            {
                var macro = OsActionCatalog.CreateMacro(action);
                var existing = candidate.Macros.FirstOrDefault(m => JsonSerializer.Serialize(m.Steps) == JsonSerializer.Serialize(macro.Steps));
                if (existing is not null) macro = existing;
                else candidate.Macros.Add(macro);
                binding = "MACRO:" + macro.Id;
            }
            if (assign) foreach (var id in _selection) candidate.Layers[_layerIndex].Keys[id].Binding = binding;
            OnboardSerializer.ValidateEditorProject(candidate);
            BeginChange(); _project = candidate;
            if (binding.StartsWith("MACRO:")) _macroId = binding[6..];
            RefreshMacros(); RefreshBindingChoices(); FinishChange(L.F($"{action.Name} hinzugefügt."));
        }
        catch (Exception ex) { ShowError(L.T("Die Aktion konnte nicht hinzugefügt werden."), ex); }
    }
}
