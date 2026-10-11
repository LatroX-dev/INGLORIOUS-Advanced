namespace InGlorious.Core;

public sealed record OsAction(string Id, string System, string SourceName, string Shortcut, string? DirectBinding, IReadOnlyList<string> Keys)
{
    public string Name => L.T(SourceName);
    public string Label => Name + " · " + Shortcut;
}

/// <summary>Explicit target OS shortcuts compiled to ordinary on-device key events. No shell commands.</summary>
public static class OsActionCatalog
{
    public static string CurrentSystem => OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "windows";
    public static IReadOnlyList<OsAction> For(string system, string layout = "de")
    {
        if (system is not ("windows" or "macos" or "linux") || layout is not ("de" or "en" or "fr")) throw new ArgumentException("Unsupported target system or keyboard layout.");
        var actions = new List<OsAction>();
        string Physical(string key) => layout switch
        {
            "de" when key == "KC_Z" => "KC_Y", "de" when key == "KC_Y" => "KC_Z",
            "fr" when key == "KC_A" => "KC_Q", "fr" when key == "KC_Q" => "KC_A",
            "fr" when key == "KC_W" => "KC_Z", "fr" when key == "KC_Z" => "KC_W",
            _ => key
        };
        string Caption(string key) => key switch
        {
            "KC_LGUI" => system == "macos" ? "⌘" : system == "windows" ? "Win" : "Super",
            "KC_LCTL" => "Ctrl", "KC_LSFT" => "Shift", "KC_LALT" => system == "macos" ? "⌥" : "Alt",
            "KC_SPC" => "Space", "KC_PSCR" => "Print", _ => key[3..]
        };
        void Add(string id, string name, params string[] keys) => actions.Add(new(id, system, name, string.Join(" + ", keys.Select(Caption)), null, keys.Select(Physical).ToArray()));
        void Direct(string id, string name, string code) => actions.Add(new(id, system, name, L.T("Systemtaste"), code, []));
        var command = system == "macos" ? "KC_LGUI" : "KC_LCTL";
        Add("copy", "Kopieren", command, "KC_C"); Add("paste", "Einfügen", command, "KC_V");
        Add("cut", "Ausschneiden", command, "KC_X"); Add("select-all", "Alles markieren", command, "KC_A");
        Add("undo", "Rückgängig", command, "KC_Z");
        if (system == "windows") Add("redo", "Wiederholen", command, "KC_Y"); else Add("redo", "Wiederholen", command, "KC_LSFT", "KC_Z");
        Add("save", "Speichern", command, "KC_S"); Add("find", "Suchen", command, "KC_F");
        Add("new", "Neues Fenster", command, "KC_N"); Add("close", "Fenster oder Tab schließen", command, "KC_W");
        Add("print", "Drucken", command, "KC_P");
        Add("switch", "Anwendung wechseln", system == "macos" ? "KC_LGUI" : "KC_LALT", "KC_TAB");
        if (system == "windows")
        {
            Direct("calculator", "Rechner öffnen", "KC_CALC"); Add("files", "Explorer öffnen", "KC_LGUI", "KC_E");
            Add("settings", "Systemeinstellungen öffnen", "KC_LGUI", "KC_I"); Add("desktop", "Desktop anzeigen", "KC_LGUI", "KC_D");
            Add("lock", "Bildschirm sperren", "KC_LGUI", "KC_L"); Add("screenshot", "Bildschirmausschnitt", "KC_LGUI", "KC_LSFT", "KC_S");
            Add("tasks", "Task-Manager öffnen", "KC_LCTL", "KC_LSFT", "KC_ESC");
        }
        else if (system == "macos")
        {
            Add("files", "Finder-Suche öffnen", "KC_LGUI", "KC_LALT", "KC_SPC"); Add("search", "Spotlight öffnen", "KC_LGUI", "KC_SPC");
            Add("lock", "Bildschirm sperren", "KC_LCTL", "KC_LGUI", "KC_Q");
            Add("screenshot", "Bildschirmausschnitt", "KC_LGUI", "KC_LSFT", "KC_4");
            Add("force-quit", "Dialog zum Beenden von Apps", "KC_LGUI", "KC_LALT", "KC_ESC");
        }
        else
        {
            Direct("calculator", "Rechner öffnen", "KC_CALC"); Direct("files", "Dateimanager öffnen", "KC_MYCM");
            Add("lock", "Bildschirm sperren", "KC_LGUI", "KC_L"); Add("screenshot", "Bildschirmaufnahme", "KC_PSCR");
            Add("overview", "Aktivitätenübersicht", "KC_LGUI");
        }
        return actions;
    }
    public static MacroDefinition CreateMacro(OsAction action)
    {
        if (action.DirectBinding is not null || action.Keys.Count == 0) throw new ArgumentException("This action is a direct system key.");
        var steps = action.Keys.Select(key => new MacroStep { Kind = "down", Key = key }).ToList();
        steps.Add(new() { Kind = "delay", DelayMs = 20 });
        steps.AddRange(action.Keys.Reverse().Select(key => new MacroStep { Kind = "up", Key = key }));
        return new() { Name = action.Name + " · " + action.System, Steps = steps };
    }
}
