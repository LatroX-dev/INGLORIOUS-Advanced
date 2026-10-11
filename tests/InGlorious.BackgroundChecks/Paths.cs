namespace InGlorious.Core;
internal static class Paths
{
    // These tests use explicit random scopes and must never enter the production pipe.
    public static string UserDataRoot => throw new InvalidOperationException("Production instance discovery is forbidden in offline tests.");
}
