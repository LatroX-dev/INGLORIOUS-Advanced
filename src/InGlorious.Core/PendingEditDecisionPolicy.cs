namespace InGlorious.Core;

public enum PendingEditChoice { Apply, Discard, Cancel }

/// <summary>The navigation gate is independent of WPF and device access.</summary>
public static class PendingEditDecisionPolicy
{
    public static bool Resolve(bool hasChanges, PendingEditChoice choice, Func<bool> apply, Action discard)
    {
        if (!hasChanges) return true;
        switch (choice)
        {
            case PendingEditChoice.Apply: return apply();
            case PendingEditChoice.Discard: discard(); return true;
            default: return false;
        }
    }
    public static bool TryTiming(string? intervalText, string? debounceText, out int interval, out int debounce)
    {
        interval = debounce = 0;
        return int.TryParse(intervalText, out interval) && interval is 1 or 2 or 4 or 8
            && int.TryParse(debounceText, out debounce) && debounce is >= 0 and <= 40;
    }
    public static string TimingFingerprint(string? intervalText, string? debounceText) =>
        TryTiming(intervalText, debounceText, out var interval, out var debounce)
            ? $"{interval}/{debounce}" : $"invalid:{intervalText?.Trim()}/{debounceText?.Trim()}";
}
