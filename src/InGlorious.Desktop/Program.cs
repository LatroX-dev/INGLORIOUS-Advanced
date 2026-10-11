using Avalonia;

namespace InGlorious.Desktop;

internal static class Program
{
    internal static string[] Arguments { get; private set; } = [];
    internal static bool Offline => Arguments.Contains("--screenshot") || Arguments.Contains("--smoke");
    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = args;
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
