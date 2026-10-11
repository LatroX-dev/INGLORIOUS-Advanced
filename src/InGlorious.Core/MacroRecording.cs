namespace InGlorious.Core;

/// <summary>Bounded local recording state. Receives timestamps/codes; never reads or sends input.</summary>
public sealed class MacroRecording
{
    public const int MaximumSteps = 128;
    public const int MaximumDurationMs = 30000;
    private readonly List<MacroStep> _steps = [];
    private readonly List<string> _held = [];
    private readonly HashSet<string> _unsupported = [];
    private long _startedAt, _lastEventAt, _observedAt;
    public bool IsRecording { get; private set; }
    public string Status { get; private set; } = "Bereit.";
    public int HeldCount => _held.Count;
    public int StepCount => _steps.Count;
    public int ElapsedMs => (int)Math.Clamp(_observedAt - _startedAt, 0, MaximumDurationMs);
    public IReadOnlyList<string> UnsupportedInputs => _unsupported.Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<MacroStep> Steps => _steps.Select(Clone).ToArray();

    public void Start(long timestampMs)
    {
        if (timestampMs < 0) throw new ArgumentOutOfRangeException(nameof(timestampMs));
        _steps.Clear(); _held.Clear(); _unsupported.Clear();
        _startedAt = _lastEventAt = _observedAt = timestampMs;
        IsRecording = true; Status = "Aufnahme läuft.";
    }
    public void Advance(long timestampMs)
    {
        if (!IsRecording) return;
        Observe(timestampMs);
        if (timestampMs - _startedAt >= MaximumDurationMs)
            Finish(_startedAt + MaximumDurationMs, "Aufnahme nach 30 Sekunden gestoppt.", true);
    }
    public bool KeyDown(string code, long timestampMs, bool repeated = false)
    {
        if (!IsRecording) return false;
        Advance(timestampMs);
        if (!IsRecording || repeated || _held.Contains(code)) return false;
        if (!MacroPhysicalKeys.IsSupported(code)) { Unsupported(code, timestampMs); return false; }
        if (!AppendEvent("down", code, timestampMs, _held.Count + 1)) return false;
        _held.Add(code); return true;
    }
    public bool KeyUp(string code, long timestampMs)
    {
        if (!IsRecording) return false;
        Advance(timestampMs);
        if (!IsRecording || !_held.Contains(code)) return false;
        if (!AppendEvent("up", code, timestampMs, _held.Count - 1)) return false;
        _held.Remove(code); return true;
    }
    public void Unsupported(string name, long timestampMs)
    {
        if (!IsRecording) return;
        Advance(timestampMs);
        if (IsRecording) _unsupported.Add(name);
    }
    public void Stop(long timestampMs, string reason = "Aufnahme gestoppt.")
    {
        if (!IsRecording) return;
        Advance(timestampMs);
        if (IsRecording) Finish(timestampMs, reason, true);
    }
    private void Observe(long timestampMs)
    {
        if (timestampMs < _observedAt) throw new ArgumentOutOfRangeException(nameof(timestampMs), "Recording timestamps must be monotonic.");
        _observedAt = timestampMs;
    }
    private bool AppendEvent(string kind, string code, long timestampMs, int heldAfter)
    {
        var pause = _steps.Count == 0 ? 0 : (int)(timestampMs - _lastEventAt);
        // Reserve space for all releases before accepting another press or a pause.
        if (_steps.Count + DelaySteps(pause) + 1 + heldAfter > MaximumSteps)
        {
            Finish(timestampMs, "Schrittgrenze erreicht. Letzte Eingabe nicht aufgenommen; gehaltene Tasten abgeschlossen.", false);
            return false;
        }
        AddDelay(pause);
        _steps.Add(new MacroStep { Kind = kind, Key = code, DelayMs = 0 });
        _lastEventAt = timestampMs;
        return true;
    }
    private void Finish(long timestampMs, string reason, bool preserveHoldTime)
    {
        if (_held.Count > 0 && preserveHoldTime)
        {
            var pause = (int)(timestampMs - _lastEventAt);
            if (_steps.Count + DelaySteps(pause) + _held.Count <= MaximumSteps) AddDelay(pause);
            else reason += " Die letzte Haltezeit wurde wegen der Schrittgrenze gekürzt.";
        }
        for (var i = _held.Count - 1; i >= 0; --i)
            _steps.Add(new MacroStep { Kind = "up", Key = _held[i], DelayMs = 0 });
        _held.Clear(); IsRecording = false; Status = reason;
    }
    private static int DelaySteps(int duration) => (duration + 9999) / 10000;
    private void AddDelay(int duration)
    {
        while (duration > 0)
        {
            var part = Math.Min(duration, 10000);
            _steps.Add(new MacroStep { Kind = "delay", Key = "", DelayMs = part }); duration -= part;
        }
    }
    private static MacroStep Clone(MacroStep step) => new() { Kind = step.Kind, Key = step.Key, DelayMs = step.DelayMs };
}

/// <summary>Windows set-1 scan positions to this project's ISO USB/QMK key vocabulary.</summary>
public static class MacroPhysicalKeys
{
    private static readonly IReadOnlyDictionary<int, string> ScanCodes = CreateScanCodes();
    private static readonly HashSet<string> Supported = ScanCodes.Values
        .Concat(Enumerable.Range(1, 24).Select(n => "KC_F" + n)).Append("KC_NUM").Append("KC_PSCR")
        .Where(BindingCatalog.IsBasic).ToHashSet(StringComparer.Ordinal);
    public static bool IsSupported(string? code) => code != null && Supported.Contains(code);
    public static bool TryMap(int scanCode, bool extended, int virtualKey, out string code)
    {
        code = "";
        // These Windows virtual keys resolve ambiguities in NumLock/PrintScreen scan messages.
        if (virtualKey == 0x90) code = "KC_NUM";
        else if (virtualKey == 0x2C) code = "KC_PSCR";
        else if (virtualKey is >= 0x7C and <= 0x87) code = "KC_F" + (virtualKey - 0x70 + 1);
        else if (virtualKey is 0x13 or 0x91) return false; // Pause/ScrollLock absent from current catalog.
        else if (scanCode is < 1 or > 0x7F || !ScanCodes.TryGetValue(scanCode | (extended ? 0x100 : 0), out code!)) { code = ""; return false; }
        if (IsSupported(code)) return true;
        code = ""; return false;
    }
    public static string Label(string code)
    {
        var label = BindingCatalog.KeyLabel(code);
        if (code.StartsWith("KC_P", StringComparison.Ordinal) && code is not "KC_P" && code is not "KC_PSCR" && code is not "KC_PGUP" && code is not "KC_PGDN") label = "Num " + label;
        if (code is "KC_LCTL" or "KC_LSFT" or "KC_LALT" or "KC_LGUI") label += " links";
        if (code is "KC_RCTL" or "KC_RSFT") label += " rechts";
        return label;
    }
    private static IReadOnlyDictionary<int, string> CreateScanCodes()
    {
        var result = new Dictionary<int, string>();
        void Row(int start, string keys)
        {
            foreach (var key in keys.Split(' ')) result[start++] = "KC_" + key;
        }
        Row(0x01, "ESC 1 2 3 4 5 6 7 8 9 0 MINS EQL BSPC TAB Q W E R T Y U I O P LBRC RBRC ENT LCTL A S D F G H J K L SCLN QUOT GRV LSFT NUHS Z X C V B N M COMM DOT SLSH RSFT PAST LALT SPC CAPS F1 F2 F3 F4 F5 F6 F7 F8 F9 F10");
        Row(0x47, "P7 P8 P9 PMNS P4 P5 P6 PPLS P1 P2 P3 P0 PDOT");
        result[0x56] = "KC_NUBS"; result[0x57] = "KC_F11"; result[0x58] = "KC_F12";
        foreach (var (scan, code) in new (int, string)[] {
            (0x1C, "PENT"), (0x1D, "RCTL"), (0x35, "PSLS"), (0x37, "PSCR"), (0x38, "RALT"),
            (0x47, "HOME"), (0x48, "UP"), (0x49, "PGUP"), (0x4B, "LEFT"), (0x4D, "RGHT"),
            (0x4F, "END"), (0x50, "DOWN"), (0x51, "PGDN"), (0x52, "INS"), (0x53, "DEL"),
            (0x5B, "LGUI"), (0x5C, "RGUI") }) result[scan | 0x100] = "KC_" + code;
        return result;
    }
}
