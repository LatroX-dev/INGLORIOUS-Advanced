using System.IO;

namespace InGlorious.Core;

/// <summary>Per-dialog system-start state. Never persisted in a profile or preference file.</summary>
public sealed class AppAutostartEditor
{
    private readonly bool _offline;
    private readonly Func<AppAutostartState> _read;
    private readonly Action<bool> _write;
    private AppAutostartState _state = new(false, false, null);
    public bool Enabled => _state.Enabled;
    public bool CanEdit => !_offline && _state.Supported && _state.Error is null;
    public string Message { get; private set; } = "";
    public static string Description => OperatingSystem.IsMacOS()
        ? L.T("Startet INGLORIOUS Advanced in der Menüleiste.")
        : L.T("Startet INGLORIOUS Advanced im Infobereich.");

    public AppAutostartEditor(bool offline = false, Func<AppAutostartState>? read = null, Action<bool>? write = null)
    {
        _offline = offline; _read = read ?? AppAutostart.GetState; _write = write ?? AppAutostart.SetEnabled;
        if (!offline) Refresh();
    }

    private void Refresh()
    {
        try { _state = _read(); }
        catch { _state = _state with { Error = "unavailable" }; }
        Message = _state.Error is not null ? L.T("Der Autostart-Status konnte nicht gelesen werden.")
            : !_state.Supported ? L.T("Autostart ist auf diesem System nicht verfügbar.") : L.T(_state.Notice ?? "");
    }

    // The supplied save action only writes ordinary app preferences. A cancelled dialog never calls this method.
    public bool Save(bool requested, Action savePreferences)
    {
        if (_offline) return false;
        var previous = Enabled;
        var change = CanEdit && requested != previous;
        if (change)
        {
            try
            {
                _write(requested);
                Refresh();
                if (!CanEdit || Enabled != requested) throw new InvalidOperationException("Autostart was not confirmed.");
            }
            catch
            {
                Restore(previous);
                Message = L.T("Der Autostart konnte nicht geändert werden. Bitte versuche es erneut.");
                return false;
            }
        }
        try { savePreferences(); return true; }
        catch
        {
            if (change) Restore(previous);
            Message = L.T("Die App-Einstellungen konnten nicht gespeichert werden. Bitte versuche es erneut.");
            return false;
        }
    }

    private void Restore(bool previous)
    {
        try { _write(previous); } catch { /* The following read determines the visible system state. */ }
        Refresh();
    }

    public static void RunOfflineChecks(Action<bool, string> check)
    {
        var reads = 0; var writes = 0; var saves = 0;
        var system = new AppAutostartState(true, false, null);
        AppAutostartState Read() { reads++; return system; }
        void Write(bool value) { writes++; system = system with { Enabled = value }; }
        var preview = new AppAutostartEditor(true, Read, Write);
        check(!preview.Enabled && !preview.CanEdit && !preview.Save(true, () => saves++) && reads == 0 && writes == 0 && saves == 0,
            "Autostart: offline preview never reads or changes the operating system");
        var editor = new AppAutostartEditor(false, Read, Write);
        check(editor.CanEdit && !editor.Enabled && reads == 1 && writes == 0, "Autostart: opening settings reads the real state without enabling startup");
        check(editor.Save(false, () => saves++) && writes == 0 && saves == 1, "Autostart: unchanged switch never rewrites startup registration");
        check(editor.Save(true, () => saves++) && editor.Enabled && system.Enabled && writes == 1, "Autostart: explicit save enables startup and verifies it");
        check(editor.Save(false, () => saves++) && !editor.Enabled && !system.Enabled, "Autostart: explicit save disables startup");
        var saveBefore = saves;
        check(!editor.Save(true, () => throw new IOException("fixture")) && !system.Enabled && !editor.Enabled && saves == saveBefore,
            "Autostart: a preference-save failure restores the previous startup state");
        var failed = new AppAutostartEditor(false, Read, _ => throw new UnauthorizedAccessException("fixture"));
        check(!failed.Save(true, () => saves++) && !failed.Enabled && saves == saveBefore && failed.Message == L.T("Der Autostart konnte nicht geändert werden. Bitte versuche es erneut."),
            "Autostart: failed system write resets the switch and blocks preference commit");
        var ignored = new AppAutostartEditor(false, Read, _ => { });
        check(!ignored.Save(true, () => saves++) && !ignored.Enabled && saves == saveBefore,
            "Autostart: an unconfirmed system write is never reported as saved");
        var partial = new AppAutostartEditor(false, Read, value => { if (value) system = system with { Enabled = true }; throw new IOException("fixture"); });
        check(!partial.Save(true, () => saves++) && partial.Enabled && system.Enabled && saves == saveBefore,
            "Autostart: a failed rollback displays the actual registration rather than an invented previous state");
        system = new(true, true, null); editor = new(false, Read, Write);
        check(editor.Enabled && editor.CanEdit, "Autostart: existing enabled registration is shown rather than defaulted off");
        system = new(true, true, "fixture"); editor = new(false, Read, Write); var writesBefore = writes;
        check(!editor.CanEdit && editor.Message == L.T("Der Autostart-Status konnte nicht gelesen werden.") && editor.Save(false, () => saves++) && writes == writesBefore,
            "Autostart: unknown system state cannot be overwritten while other preferences remain usable");
        system = new(false, false, null); editor = new(false, Read, Write);
        check(!editor.CanEdit && editor.Message == L.T("Autostart ist auf diesem System nicht verfügbar."), "Autostart: unsupported system is explicitly disabled");
        system = new(true, true, Notice: AppAutostart.WindowsApprovalNotice); editor = new(false, Read, Write);
        check(editor.CanEdit && editor.Enabled && editor.Message == L.T(AppAutostart.WindowsApprovalNotice),
            "Autostart: an independent Windows startup choice is visible without being overwritten");
    }
}
