using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private async Task<bool> AdoptSetupCoreImportAsync(CoreImportSelection selection)
    {
        if (_startupWindowClosed || _allowClose || !await GuardNavigation()) return false;
        var destination = OpenSession(selection.Preview.DeviceId);
        if (!await MayReplace(destination)) return false;
        var copy = selection.ApplyToCopy(destination.Project);
        var imported = new EditorState(destination.Project, null);
        imported.Change(project => { project.Name = copy.Name; project.Layers = copy.Layers; project.Macros = copy.Macros; project.Slider = copy.Slider; project.DeviceSettings = copy.DeviceSettings; project.Standby = copy.Standby; });
        _sessions[copy.DeviceId] = _state = imported; _macroId = null;
        Build(); Status(L.T("CORE-Profilkopie geöffnet. Noch nicht auf die Tastatur übertragen."));
        return true;
    }

    private async Task ImportCoreSettingsAsync()
    {
        if (Program.Offline || _startupLoading || _startupWindowClosed || _allowClose || _helpWindow is not null || !await GuardNavigation()) return;
        var source = _state; var fingerprint = EditorState.Fingerprint(source.Project); var path = source.Path;
        _navigating = true;
        try
        {
            var copy = await new CoreImportWindow(source.Project).ShowDialog<AppProject?>(this);
            if (copy is null || !IsKeyBindingContextCurrent(source, fingerprint, path)) return;
            var imported = new EditorState(source.Project, null);
            imported.Change(project => { project.Name = copy.Name; project.Layers = copy.Layers; });
            _sessions[copy.DeviceId] = _state = imported; _macroId = null;
            Build(); Status(L.T("CORE-Profilkopie geöffnet. Noch nicht auf die Tastatur übertragen."));
        }
        finally { _navigating = false; }
    }
}
