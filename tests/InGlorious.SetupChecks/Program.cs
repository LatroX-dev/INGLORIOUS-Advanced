using InGlorious.Core;

internal static partial class Program
{
    private static int _checks;
    private static readonly Lazy<byte[]?> RetainedOriginal = new(() =>
    {
        var first = Environment.GetEnvironmentVariable("INGLORIOUS_TEST_ORIGINAL_READ1");
        var second = Environment.GetEnvironmentVariable("INGLORIOUS_TEST_ORIGINAL_READ2");
        if (first is null && second is null) return null;
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) ||
            string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Supply two distinct original read paths for the retained-loader fixture.");
        using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (a.Length != RetainedLoaderLayout.FlashBytes || b.Length != RetainedLoaderLayout.FlashBytes)
            throw new InvalidDataException("Retained-loader fixtures require complete original reads.");
        var original = new byte[RetainedLoaderLayout.FlashBytes]; var repeat = new byte[original.Length];
        a.ReadExactly(original); b.ReadExactly(repeat);
        if (!original.AsSpan().SequenceEqual(repeat) || InstallationIdentity.Read(original) is not null)
            throw new InvalidDataException("Retained-loader original pair does not match.");
        RetainedLoaderLayout.ValidatePrefix(original);
        StockFirmwareInspector.Validate(original, 0x342D, 0xE3CE, RetainedLoaderLayout.DeviceId);
        return original;
    });
    private static byte[]? RetainedOriginalPair() => RetainedOriginal.Value?.ToArray();
    private static IEnumerable<BoardDescriptor> RoundTripBoards => BoardDescriptorRegistry.ProtocolBoards
        .Where(board => board.BoardId != RetainedLoaderLayout.BoardId || RetainedOriginal.Value is not null);
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
        if (RetainedOriginal.Value is null) Console.WriteLine("Retained-loader positive round trips need explicit INGLORIOUS_TEST_ORIGINAL_READ1/READ2 fixtures; public synthetic rejection checks remain active.");
        CheckIdentity();
        CheckStockFirmwareInspector();
        await CheckLegacyMigration();
        await CheckCoordinator();
        await CheckFirmwareSequence();
        CheckPortableUsb();
        await CheckPortableRoundTrips();
        Console.WriteLine($"PASS: {_checks} setup checks; fake discovery/adapter and isolated files only. No device access.");
    }
}
