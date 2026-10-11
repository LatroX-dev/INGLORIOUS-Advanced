using InGlorious.Core;

var checks = 0;
void Check(bool condition, string text) { ++checks; if (!condition) throw new InvalidOperationException(text); }
foreach (var choice in Enum.GetValues<PendingEditChoice>())
{
    var applied = 0; var discarded = 0;
    Check(PendingEditDecisionPolicy.Resolve(false, choice, () => { ++applied; return true; }, () => ++discarded), "Unchanged fields never block navigation");
    Check(applied == 0 && discarded == 0, "Unchanged state triggers no side effect");
    foreach (var valid in new[] { false, true })
    {
        applied = discarded = 0;
        var proceed = PendingEditDecisionPolicy.Resolve(true, choice, () => { ++applied; return valid; }, () => ++discarded);
        Check(proceed == (choice == PendingEditChoice.Discard || choice == PendingEditChoice.Apply && valid), "Cancel or invalid apply blocks navigation/save");
        Check(applied == (choice == PendingEditChoice.Apply ? 1 : 0) && discarded == (choice == PendingEditChoice.Discard ? 1 : 0), "Only explicit choice acts");
    }
}
for (var interval = -1; interval <= 9; ++interval)
for (var debounce = -1; debounce <= 41; ++debounce)
    Check(PendingEditDecisionPolicy.TryTiming(interval.ToString(), debounce.ToString(), out _, out _) == (interval is 1 or 2 or 4 or 8 && debounce is >= 0 and <= 40), "Timing bounds");
foreach (var invalid in new[] { "", "abc", "1.5", "999999999999999999999" }) Check(!PendingEditDecisionPolicy.TryTiming("1", invalid, out _, out _), "Invalid text rejected");
Check(PendingEditDecisionPolicy.TimingFingerprint("1", "05") == PendingEditDecisionPolicy.TimingFingerprint(" 1 ", "5"), "Equivalent numbers do not prompt");
Check(PendingEditDecisionPolicy.TimingFingerprint("1", "41") != PendingEditDecisionPolicy.TimingFingerprint("1", "5"), "Invalid draft remains pending");
Console.WriteLine($"PASS pending-edit decisions: {checks} checks; no UI or hardware calls.");
