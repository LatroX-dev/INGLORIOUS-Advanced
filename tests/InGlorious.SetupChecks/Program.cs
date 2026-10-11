using InGlorious.Core;

internal static partial class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or FormatException or IOException)
        { _checks++; return; }
        throw new InvalidOperationException(message);
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or FormatException or IOException or InvalidOperationException)
        { _checks++; return; }
        throw new InvalidOperationException(message);
    }

    private static async Task Main()
    {
        CheckIdentity();
        CheckStockFirmwareInspector();
        await CheckCoordinator();
        Console.WriteLine($"PASS: {_checks} setup checks; fake discovery/adapter and isolated files only. No device access.");
    }
}
