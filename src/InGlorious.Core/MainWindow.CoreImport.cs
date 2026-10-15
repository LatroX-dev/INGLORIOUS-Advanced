using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool AdoptSetupCoreImport(CoreImportSelection selection)
    {
        if (_startupClosed || _readingDevice || !ResolvePendingEdits()) return false;
        var previousId = Device.Id;
        SwitchDevice(selection.Preview.DeviceId);
        if (Device.Id != selection.Preview.DeviceId) return false;
        if (!ConfirmReplace()) { if (Device.Id != previousId) SwitchDevice(previousId); return false; }
        var copy = selection.ApplyToCopy(_project);
        BeginChange(); _project = ProjectService.Clone(copy); _projectPath = null; _macroAssigning = false; _layerIndex = 0;
        RefreshAll(); CaptureSession(); FinishChange(L.T("CORE-Profilkopie geöffnet. Noch nicht auf die Tastatur übertragen."), false);
        return true;
    }

    private void CoreImport_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshotMode || !CanTransferKeyBindings || !ResolvePendingEdits()) return;
        CommitNames(); var context = CaptureKeyBindingContext();
        var dialog = new CoreImportWindow(_project) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ImportedProject is not { } copy || !IsKeyBindingContextCurrent(context)) return;
        BeginChange(); _project = copy; _projectPath = null; _macroAssigning = false; _layerIndex = 0;
        RefreshAll(); CaptureSession(); FinishChange(L.T("CORE-Profilkopie geöffnet. Noch nicht auf die Tastatur übertragen."), false);
    }
}
