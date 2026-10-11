using InGlorious.FirmwareAdapter;

if (args.SequenceEqual(new[] { "--self-test" })) return SelfTests.Run();
if (args.Length == 3 && args[0] == "--self-test" && args[1] == "--firmware-directory") return SelfTests.Run(args[2]);
if (args.Length != 4 || args[0] != "--request" || args[2] != "--response")
{
    Console.Error.WriteLine("Usage: InGlorious.FirmwareAdapter --request REQUEST.json --response NEW_RESPONSE.json; --self-test runs offline checks only.");
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
    var response = new AdapterEngine(() => new NativeBackend()).Run(request);
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
