namespace InGlorious.Core;

/// <summary>Labels for local OS input. These do not identify a physical key or device.</summary>
public static class DeviceTypingTest
{
    public static string KeyLabel(string name)
    {
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])) return name[1..];
        if (name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7 && char.IsAsciiDigit(name[6])) return name[6..];
        var code = name switch
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
        // Letters are the OS result, not QMK positions: do not swap Y/Z or infer a layout.
        return code is null ? name : BindingCatalog.FunctionLabel(code);
    }
}
