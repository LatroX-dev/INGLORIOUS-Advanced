using InGlorious.FirmwareAdapter;

if (args.SequenceEqual(new[] { "--self-test" })) return SelfTests.Run();
if (args.Length == 3 && args[0] == "--self-test" && args[1] == "--firmware-directory") return SelfTests.Run(args[2]);
if (args.Length == 7 && args[0] == "--self-test-engineering" && args[1] == "--engineering-candidate" && args[3] == "--engineering-original" && args[5] == "--engineering-repeat")
    return SelfTests.RunEngineering(args[2], args[4], args[6]);
if (args.Length == 7 && args[0] == "--self-test-retained-loader" && args[1] == "--candidate" && args[3] == "--original" && args[5] == "--repeat")
    return SelfTests.RunRetainedLoader(args[2], args[4], args[6]);
if (args.SequenceEqual(new[] { "--discover" }))
{
    try
    {
        using var usb = new NativeUsb(inventoryOnly: true);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(Json.Bytes(new { schema = 1, operation = "discover", status = "ok", devices = usb.Devices })));
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
        return 1;
    }
}
bool engineering = args.Length is 5 or 7 && args[4] == "--engineering";
string? authorizationPath = engineering && args.Length == 7 && args[5] == "--engineering-write-authorization" ? args[6] : null;
if ((args.Length != 4 && !(engineering && (args.Length == 5 || authorizationPath != null))) || args[0] != "--request" || args[2] != "--response")
{
    Console.Error.WriteLine("Usage: InGlorious.FirmwareAdapter --request REQUEST.json --response NEW_RESPONSE.json [--engineering [--engineering-write-authorization FILE]]; engineering writes additionally require a compiled reviewed candidate and scoped authorization. --self-test runs offline checks only.");
    Console.Error.WriteLine("Offline real-fixture checks: --self-test-engineering --engineering-candidate BIN --engineering-original READ1 --engineering-repeat READ2");
    Console.Error.WriteLine("Offline normal retained-loader checks: --self-test-retained-loader --candidate BIN --original READ1 --repeat READ2");
    return 2;
}
try
{
    string responsePath = Path.GetFullPath(args[3]);
    Files.NoLinks(responsePath);
    if (File.Exists(responsePath) || Directory.Exists(responsePath)) throw new IOException("RESPONSE_ALREADY_EXISTS");
    AdapterRequest request = Json.Read<AdapterRequest>(args[1]);
    // Reserve the response before any hardware access; it cannot overwrite previous evidence.
    using var responseStream = new FileStream(responsePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    var response = new AdapterEngine(() => new NativeBackend()).Run(request, engineering, authorizationPath);
    byte[] bytes = Json.Bytes(response); responseStream.Write(bytes); responseStream.Flush(true);
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
    return response.Status is "ok" or "verified" ? 0 : 1;
}
catch (Exception ex)
{
    // No unstructured tool output is exposed on stdout. A missing/failed response is never success.
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    return 1;
}
