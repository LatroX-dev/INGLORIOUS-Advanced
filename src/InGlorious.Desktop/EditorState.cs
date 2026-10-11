using System.Text.Json;
using InGlorious.Core;

namespace InGlorious.Desktop;

/// <summary>One detached editing session per device. Selection never changes persisted configuration.</summary>
public sealed class EditorState
{
    private readonly Stack<AppProject> _undo = new(), _redo = new();
    public AppProject Project { get; private set; }
    public string? Path { get; private set; }
    private string _saved;
    public int LayerIndex { get; set; }
    public LayerProfile Layer => Project.Layers[LayerIndex];
    public DeviceDefinition Device => DeviceCatalog.Get(Project.DeviceId);
    public HashSet<string> SelectedKeys { get; } = [];
    public HashSet<int> SelectedSideLeds { get; } = [];
    public string Surface { get; set; } = "keys";
    public bool Dirty => Fingerprint(Project) != _saved;
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public EditorState(string id) : this(ProjectService.Create(id), null) { }
    public EditorState(AppProject project, string? path)
    { Project = ProjectService.Clone(project); Path = path; _saved = path is null ? "" : Fingerprint(Project); }
    public static EditorState FromDevice(AppProject project)
    {
        var state = new EditorState(project, null);
        state.MarkInitialClean();
        return state;
    }
    public static string Fingerprint(AppProject project) => JsonSerializer.Serialize(project);
    public void MarkInitialClean() => _saved = Fingerprint(Project);
    public void MarkSaved(string path) { Path = path; _saved = Fingerprint(Project); }
    public void Change(Action<AppProject> edit)
    {
        var next = ProjectService.Clone(Project); edit(next);
        if (Fingerprint(next) == Fingerprint(Project)) return;
        _undo.Push(Project); if (_undo.Count > 100) { var keep = _undo.Take(100).Reverse().ToArray(); _undo.Clear(); foreach (var p in keep) _undo.Push(p); }
        _redo.Clear(); Project = next;
    }
    public void Undo() { if (_undo.Count != 0) { _redo.Push(Project); Project = _undo.Pop(); } }
    public void Redo() { if (_redo.Count != 0) { _undo.Push(Project); Project = _redo.Pop(); } }
    public int[] SelectedLeds()
    {
        var result = Surface == "keys" ? Device.Keys.Where(k => SelectedKeys.Contains(k.Id) && k.LedIndex.HasValue).Select(k => k.LedIndex!.Value).ToHashSet()
            : Surface == "left" ? Device.LeftSideLedIndices.ToHashSet() : Surface == "right" ? Device.RightSideLedIndices.ToHashSet()
            : Surface == "badge" ? Device.BadgeLedIndices.ToHashSet() : Surface == "both" ? Device.SideLedIndices.ToHashSet() : SelectedSideLeds.ToHashSet();
        if (Layer.SynchronizeSides && Surface != "keys")
            for (var i = 0; i < Math.Min(Device.LeftSideLedIndices.Count, Device.RightSideLedIndices.Count); i++)
                if (result.Contains(Device.LeftSideLedIndices[i]) || result.Contains(Device.RightSideLedIndices[i]))
                { result.Add(Device.LeftSideLedIndices[i]); result.Add(Device.RightSideLedIndices[i]); }
        return result.Order().ToArray();
    }
    public void SelectKey(string id, bool multiple)
    {
        if (!Device.Keys.Any(k => k.Id == id)) return;
        Surface = "keys"; SelectedSideLeds.Clear();
        if (!multiple) SelectedKeys.Clear();
        if (!SelectedKeys.Add(id)) SelectedKeys.Remove(id);
    }
    public void SelectBadge()
    {
        if (Device.BadgeLedIndices.Count == 0) return;
        SelectedKeys.Clear(); SelectedSideLeds.Clear(); Surface = "badge";
    }
    public void SelectSide(int led, bool multiple)
    {
        if (!Device.SideLedIndices.Contains(led)) return;
        var previous = SelectedLeds(); Surface = "individual"; SelectedSideLeds.Clear();
        if (multiple) SelectedSideLeds.UnionWith(previous);
        if (!SelectedSideLeds.Add(led)) SelectedSideLeds.Remove(led);
    }
    public string ColorAt(int led) => Device.Keys.FirstOrDefault(k => k.LedIndex == led) is { } key
        ? Layer.Keys[key.Id].Color : Layer.ExtraLedColors.GetValueOrDefault(led, Layer.SideLedColor);
    public void SetColor(string color)
    {
        if (!ColorSequenceRules.IsValid([color])) throw new InvalidDataException(L.T("Bitte eine Farbe als #RRGGBB eingeben, zum Beispiel #A38BFA."));
        var leds = SelectedLeds(); if (leds.Length == 0) throw new InvalidDataException(L.T("Bitte mindestens eine Taste oder Seiten-LED auswählen."));
        Change(p => { var layer = p.Layers[LayerIndex]; foreach (var led in leds)
            if (Device.Keys.FirstOrDefault(k => k.LedIndex == led) is { } key) layer.Keys[key.Id].Color = color.ToUpperInvariant();
            else layer.ExtraLedColors[led] = color.ToUpperInvariant(); });
    }
    public void SetEffect(LightingEffectSettings effect, bool global)
    {
        if (!LightingEffects.All.Any(e => e.Id == effect.Mode) || effect.Speed is < 0 or > 255 || effect.Brightness is < 0 or > 255 || effect.Target is < 1 or > 3 || !ColorSequenceRules.IsValid(effect.Colors))
            throw new InvalidDataException(L.T("Bitte einen Effekt auswählen."));
        var leds = SelectedLeds(); if (!global && leds.Length == 0) throw new InvalidDataException(L.T("Bitte mindestens eine Taste oder Seiten-LED auswählen."));
        Change(p =>
        {
            var layer = p.Layers[LayerIndex];
            if (global) layer.LightingEffect = new() { Mode = effect.Mode, Speed = effect.Speed, Brightness = effect.Brightness, Target = effect.Target, Reverse = effect.Reverse, Colors = [.. effect.Colors] };
            else foreach (var led in leds) layer.LedEffects[led] = new() { Mode = effect.Mode, Speed = effect.Speed, Brightness = effect.Brightness, Reverse = effect.Reverse, Colors = [.. effect.Colors] };
        });
    }
    public void SetBinding(string binding)
    {
        if (SelectedKeys.Count == 0) throw new InvalidDataException(L.T("Wähle zuerst mindestens eine Taste in der Geräteansicht."));
        if (binding == "KC_TRNS" && LayerIndex == 0) throw new InvalidDataException(L.T("Die Basisebene benötigt eine eigene Belegung. Durchreichen ist auf den Ebenen 1–3 möglich."));
        if (binding.StartsWith("MO(") && Device.Keys.Any(k => k.IsEncoder && SelectedKeys.Contains(k.Id))) throw new InvalidDataException(L.T("Drehbewegungen lassen sich nicht gedrückt halten. Wähle für den Drehgeber eine Taste oder „Umschalten“."));
        Change(p => { foreach (var key in SelectedKeys) p.Layers[LayerIndex].Keys[key].Binding = binding; });
    }
    public void AssignOsAction(OsAction action)
    {
        if (SelectedKeys.Count == 0) throw new InvalidDataException(L.T("Wähle zuerst mindestens eine Taste in der Geräteansicht."));
        var candidate = ProjectService.Clone(Project); var binding = action.DirectBinding;
        if (binding is null)
        {
            var created = OsActionCatalog.CreateMacro(action);
            var same = candidate.Macros.FirstOrDefault(m => JsonSerializer.Serialize(m.Steps) == JsonSerializer.Serialize(created.Steps));
            if (same is null) { candidate.Macros.Add(created); same = created; }
            binding = "MACRO:" + same.Id;
        }
        foreach (var key in SelectedKeys) candidate.Layers[LayerIndex].Keys[key].Binding = binding;
        OnboardSerializer.ValidateEditorProject(candidate);
        Change(p => { p.Macros = candidate.Macros; p.Layers = candidate.Layers; });
    }
    public void SetSideSync(bool enabled)
    {
        Change(p =>
        {
            var layer = p.Layers[LayerIndex]; layer.SynchronizeSides = enabled;
            if (!enabled) return;
            for (var i = 0; i < Math.Min(Device.LeftSideLedIndices.Count, Device.RightSideLedIndices.Count); i++)
            {
                var left = Device.LeftSideLedIndices[i]; var right = Device.RightSideLedIndices[i];
                layer.ExtraLedColors[right] = layer.ExtraLedColors.GetValueOrDefault(left, layer.SideLedColor);
                if (layer.LedEffects.TryGetValue(left, out var effect)) layer.LedEffects[right] = new() { Mode = effect.Mode, Speed = effect.Speed, Brightness = effect.Brightness, Reverse = effect.Reverse, Colors = [..effect.Colors] };
                else layer.LedEffects.Remove(right);
            }
        });
    }
    public void CopyLayer(int destination)
    {
        if (destination is < 0 or > 3 || destination == LayerIndex) return;
        if (destination == 0 && Layer.Keys.Values.Any(k => k.Binding == "KC_TRNS")) throw new InvalidDataException(L.T("Die Basisebene benötigt eigene Belegungen. Kopiere eine Ebene ohne durchgereichte Tasten oder wähle eine andere Zielebene."));
        Change(p => { var name = p.Layers[destination].Name; p.Layers[destination] = ProjectService.Clone(Project).Layers[LayerIndex]; p.Layers[destination].Name = name; });
    }
}
