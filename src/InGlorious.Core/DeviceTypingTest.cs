namespace InGlorious.Core;

/// <summary>Labels for local OS input. These do not identify a physical key or device.</summary>
public static class DeviceTypingTest
{
    // Only a prefix of the expected test word is retained, never unrelated typed text.
    public static string AdvanceCompletionPrefix(string current, string text, string expected)
    {
        if (expected.Length == 0) return "";
        foreach (var character in text)
        {
            current += character;
            while (current.Length > 0 && !expected.StartsWith(current, StringComparison.OrdinalIgnoreCase)) current = current[1..];
        }
        return current;
    }

    public static bool IsTestNavigationKey(string name) => name is "Return" or "Enter" or "Space" or "Tab" or "Escape" or
        "Left" or "Right" or "Up" or "Down" or "Home" or "End" or "Prior" or "PageUp" or "Next" or "PageDown" or
        "Back" or "Backspace" or "Delete";

    /// <summary>
    /// Locates an unambiguous standard legend for an OS input. This is a visual hint,
    /// never evidence that this device or this physical switch generated the input.
    /// </summary>
    public static IReadOnlyList<string> MatchingDisplayKeys(DeviceDefinition device, string name)
    {
        var keys = device.Keys.Where(key => !key.IsEncoder && key.Label != "Knob").ToArray();
        string? code = name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1]) ? "KC_" + name[1] :
            name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7 && char.IsAsciiDigit(name[6]) ? "KC_P" + name[6] :
            name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name[1..], out var function) && function is >= 1 and <= 24 ? "KC_" + name : KeyCode(name);
        // With Num Lock off, desktop systems report navigation events for a keypad.
        // Only the standalone numpad has no competing navigation cluster.
        if (device.Id == "numpad") code = name switch
        {
            "Home" => "KC_P7", "Up" => "KC_P8", "Prior" or "PageUp" => "KC_P9",
            "Left" => "KC_P4", "Clear" => "KC_P5", "Right" => "KC_P6",
            "End" => "KC_P1", "Down" => "KC_P2", "Next" or "PageDown" => "KC_P3",
            "Insert" => "KC_P0", "Delete" => "KC_PDOT", "Return" or "Enter" => "KC_PENT", _ => code
        };
        var matches = name.Length == 1 && char.IsAsciiLetter(name[0])
            ? keys.Where(key => key.DisplayLabel.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray()
            : keys.Where(key => key.DefaultBinding == code || (name is "Return" or "Enter" && key.DefaultBinding == "KC_PENT")).ToArray();
        // Main Enter and keypad Enter are indistinguishable in generic OS input.
        // Likewise, never guess a remapped Fn key, knob press or rotation.
        return matches.Length == 1 ? [matches[0].Id] : [];
    }

    public static string KeyLabel(string name)
    {
        if (name.StartsWith("Oem", StringComparison.Ordinal)) return L.T("Zeichentaste");
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])) return name[1..];
        if (name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7 && char.IsAsciiDigit(name[6])) return name[6..];
        var code = KeyCode(name);
        // Letters are the OS result, not QMK positions: do not swap Y/Z or infer a layout.
        return code is null ? name : BindingCatalog.FunctionLabel(code);
    }

    public static IReadOnlyList<string> MatchingTextDisplayKeys(DeviceDefinition device, string text)
    {
        // Use the text actually produced by the OS, never a guessed OEM scan-code map.
        // Shifted symbols, IME input, duplicates and dead keys may have no matching cap.
        if (text.Length != 1 || char.IsControl(text[0]) || char.IsWhiteSpace(text[0])) return [];
        var matches = device.Keys.Where(key => !key.IsEncoder && key.Label != "Knob" &&
            key.DisplayLabel.Equals(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? [matches[0].Id] : [];
    }

    private static string? KeyCode(string name) => name switch
        {
            "Return" or "Enter" => "KC_ENT", "Back" or "Backspace" => "KC_BSPC",
            "Delete" => "KC_DEL", "Insert" => "KC_INS", "Home" => "KC_HOME", "End" => "KC_END",
            "Prior" or "PageUp" => "KC_PGUP", "Next" or "PageDown" => "KC_PGDN",
            "Left" => "KC_LEFT", "Right" => "KC_RGHT", "Up" => "KC_UP", "Down" => "KC_DOWN",
            "NumLock" => "KC_NUM", "Tab" => "KC_TAB", "Space" => "KC_SPC", "Escape" => "KC_ESC",
            "Capital" or "CapsLock" => "KC_CAPS", "Scroll" => "KC_SCRL", "Pause" => "KC_PAUS", "Snapshot" or "PrintScreen" => "KC_PSCR",
            "LeftCtrl" => "KC_LCTL", "RightCtrl" => "KC_RCTL", "LeftShift" => "KC_LSFT", "RightShift" => "KC_RSFT",
            "LeftAlt" => "KC_LALT", "RightAlt" => "KC_RALT", "LWin" => "KC_LGUI", "RWin" => "KC_RGUI", "Apps" => "KC_APP",
            "Add" => "KC_PPLS", "Subtract" => "KC_PMNS", "Multiply" => "KC_PAST", "Divide" => "KC_PSLS", "Decimal" => "KC_PDOT",
            "VolumeUp" => "KC_VOLU", "VolumeDown" => "KC_VOLD", "VolumeMute" => "KC_MUTE",
            "MediaPlayPause" => "KC_MPLY", "MediaNextTrack" => "KC_MNXT", "MediaPreviousTrack" => "KC_MPRV", "MediaStop" => "KC_MSTP",
            _ => null
        };
}
