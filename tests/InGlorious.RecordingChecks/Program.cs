using InGlorious.Core;
using System.Windows;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string description) { ++_checks; if (!condition) throw new InvalidOperationException(description); }
    private static void Balanced(MacroRecording recording)
    {
        var held = new HashSet<string>(); var delay = 0;
        foreach (var step in recording.Steps)
        {
            Check(step.Kind is "down" or "up" or "delay", "supported step kind");
            if (step.Kind == "delay") { Check(step.DelayMs is >= 1 and <= 10000, "bounded pause"); delay += step.DelayMs; }
            else if (step.Kind == "down") Check(held.Add(step.Key), "no duplicate press");
            else Check(held.Remove(step.Key), "no unmatched release");
        }
        Check(held.Count == 0 && recording.HeldCount == 0, "all held keys released");
        Check(recording.StepCount <= 128 && delay <= 30000, "editor limits respected");
        if (recording.StepCount > 0)
        {
            var project = ProjectService.Create("numpad");
            project.Macros.Add(new() { Name = "Recording", Steps = recording.Steps.ToList() });
            Check(ProjectService.Validate(project).Count == 0, "recording accepted by current project validator");
        }
    }
    [STAThread]
    private static void Main()
    {
        var recording = new MacroRecording();
        Check(!recording.KeyDown("KC_A", 0), "no implicit start");
        recording.Start(100); Check(recording.KeyDown("KC_LCTL", 110), "modifier recorded");
        Check(recording.KeyDown("KC_A", 130), "chord recorded");
        Check(!recording.KeyDown("KC_A", 140, true) && !recording.KeyDown("KC_A", 150), "repeat suppressed");
        recording.KeyUp("KC_A", 170); recording.Stop(210, "Focus lost");
        Check(recording.Steps.Count == 7 && recording.Steps.Where(s => s.Kind == "delay").Sum(s => s.DelayMs) == 100, "timing excludes initial idle, preserves chord pauses");
        Check(recording.Steps[^1].Key == "KC_LCTL" && recording.Status == "Focus lost", "focus-stop releases modifier"); Balanced(recording);
        var snapshot = recording.Steps; snapshot[0].Key = "KC_BAD"; Check(recording.Steps[0].Key == "KC_LCTL", "snapshots detached");
        recording.Start(1000); Check(!recording.KeyUp("KC_ENT", 1001), "activation-key release ignored");
        recording.KeyDown("KC_A", 1002); recording.Stop(26003);
        Check(recording.Steps.Count == 5 && recording.Steps[1].DelayMs == 10000 && recording.Steps[3].DelayMs == 5001, "long pauses split"); Balanced(recording);
        recording.Start(0); recording.KeyDown("KC_RALT", 0); recording.Advance(40000);
        Check(!recording.IsRecording && recording.ElapsedMs == 30000 && recording.Steps.Where(s => s.Kind == "delay").Sum(s => s.DelayMs) == 30000, "duration stops at exact deadline"); Balanced(recording);
        recording.Start(0); recording.Advance(30000); Check(recording.StepCount == 0 && !recording.IsRecording, "empty time-limit safe");
        recording.Start(0); recording.Unsupported("Pause", 1); recording.KeyDown("KC_VOLU", 2);
        Check(recording.UnsupportedInputs.Count == 2 && recording.StepCount == 0, "unsupported actions visible, no invalid steps");
        recording.KeyDown("KC_A", 3); recording.KeyUp("KC_A", 4); recording.Stop(5); Balanced(recording);
        for (var spacing = 0; spacing <= 2; ++spacing)
        {
            recording.Start(0); long time = 0;
            for (var i = 0; i < 200 && recording.IsRecording; ++i) { recording.KeyDown("KC_A", time); time += spacing; recording.KeyUp("KC_A", time); time += spacing; }
            Check(!recording.IsRecording && recording.Status.Contains("Schrittgrenze"), "step limit stops explicitly"); Balanced(recording);
        }
        // Adversarial chords and pauses always retain room for every key release.
        var random = new Random(1729); var codes = new[] { "KC_A", "KC_B", "KC_LCTL", "KC_RSFT", "KC_PENT", "KC_RALT" };
        for (var run = 0; run < 250; ++run)
        {
            recording.Start(0); long time = 0;
            for (var i = 0; i < 150 && recording.IsRecording; ++i)
            {
                time += random.Next(0, 750); var code = codes[random.Next(codes.Length)];
                if (random.Next(2) == 0) recording.KeyDown(code, time); else recording.KeyUp(code, time);
            }
            recording.Stop(time); Balanced(recording);
        }
        foreach (var (scan, ext, vk, expected) in new (int, bool, int, string)[] {
            (0x15,false,0x5A,"KC_Y"), (0x2C,false,0x59,"KC_Z"), (0x1C,false,13,"KC_ENT"), (0x1C,true,13,"KC_PENT"),
            (0x2B,false,0xBF,"KC_NUHS"), (0x56,false,0xE2,"KC_NUBS"), (0x1D,false,17,"KC_LCTL"), (0x1D,true,17,"KC_RCTL"),
            (0x38,true,18,"KC_RALT"), (0x47,false,0x24,"KC_P7"), (0x47,true,0x24,"KC_HOME"), (0x45,true,0x90,"KC_NUM"),
            (0x37,true,0x2C,"KC_PSCR"), (0,false,0x87,"KC_F24"), (0x01,false,27,"KC_ESC"), (0x0F,false,9,"KC_TAB") })
            Check(MacroPhysicalKeys.TryMap(scan, ext, vk, out var code) && code == expected, "physical key mapping " + expected);
        Check(MacroPhysicalKeys.Label("KC_Y") == "Z" && MacroPhysicalKeys.Label("KC_Z") == "Y", "German physical labels");
        Check(MacroPhysicalKeys.Label("KC_P") == "P", "ordinary letter P is not labeled as numpad");
        Check(MacroPhysicalKeys.Label("KC_P7") == "Num 7" && MacroPhysicalKeys.Label("KC_PENT") == "Num Enter", "numpad digits and Enter retain numpad labels");
        Check(!MacroPhysicalKeys.Label("KC_PSCR").StartsWith("Num ") && !MacroPhysicalKeys.Label("KC_PGUP").StartsWith("Num ") && !MacroPhysicalKeys.Label("KC_PGDN").StartsWith("Num "), "P-prefixed navigation labels are not numpad labels");
        Check(!MacroPhysicalKeys.TryMap(0x45, false, 0x13, out _) && !MacroPhysicalKeys.TryMap(0, false, 0xAF, out _), "Pause/media rejected");
        // Constructing a dialog neither captures keys nor touches a project/device.
        var app = new Application(); var window = new MacroRecorderWindow();
        Check(window.RecordedSteps == null && window.FindName("StartRecordingButton") != null && window.FindName("RecordedStepsList") != null, "dialog contract and controls exist");
        window.Close(); app.Shutdown();
        Console.WriteLine($"PASS macro recording: {_checks} checks; no real input hooks, input synthesis or device access.");
    }
}
