using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private void MacroLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_readingDevice || !ResolvePendingEdits()) return;
        CommitNames();
        try
        {
            var dialog = new MacroLibraryWindow(ProjectService.Clone(_project).Macros, _project.Name) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedMacros is not { Count: > 0 } selected) return;
            ImportLibraryMacros(selected);
        }
        catch (Exception ex) { ShowError(L.T("Makrobibliothek konnte nicht geöffnet oder übernommen werden."), ex); }
    }

    private void ImportLibraryMacros(IReadOnlyList<SharedMacro> selected)
    {
        var import = SharedMacroLibrary.CopyIntoProfile(_project, selected);
        // Verify the actual device's current schema limits (including color-sequence storage) before changing the editor.
        OnboardSerializer.ValidateEditorProject(import.Project);
        BeginChange(); _project = import.Project; _macroId = import.AddedIds[0]; _macroAssigning = false;
        RefreshAll(); FinishChange(L.F($"{import.AddedIds.Count} Makros ins Profil kopiert. Wähle anschließend die Auslösetasten."), false);
    }
}
