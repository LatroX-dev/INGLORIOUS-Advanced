using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InGlorious.Core;

int checks = 0;
void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); checks++; }
async Task Reject(Func<Task> action, string reason)
{
    try { await action(); }
    catch (SoftwareUpdateException) { checks++; return; }
    throw new InvalidOperationException(reason);
}
async Task Canceled(Func<Task> action, string reason)
{
    try { await action(); }
    catch (OperationCanceledException) { checks++; return; }
    throw new InvalidOperationException(reason);
}
const string TestVersion = "0.6.46";
byte[] payload = Encoding.UTF8.GetBytes("Synthetic software update test package. No executable content.");
string digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
string root = Path.Combine(Path.GetTempPath(), "INGLORIOUS-SoftwareUpdateChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

Dictionary<string, object?> Asset(string version = TestVersion, SoftwareUpdatePlatform platform = SoftwareUpdatePlatform.WindowsX64) => new()
{
    ["name"] = SoftwareUpdateService.PackageName(version, platform), ["state"] = "uploaded", ["size"] = payload.Length,
    ["digest"] = "sha256:" + digest,
    ["browser_download_url"] = $"{SoftwareUpdateService.RepositoryUrl}/releases/download/v{version}/{SoftwareUpdateService.PackageName(version, platform)}"
};
Dictionary<string, object?> Release(string version = TestVersion, SoftwareUpdatePlatform platform = SoftwareUpdatePlatform.WindowsX64) => new()
{
    ["tag_name"] = "v" + version, ["draft"] = false, ["prerelease"] = true,
    ["published_at"] = "2026-10-11T12:00:00Z", ["body"] = "- Software updates.\n- Fixed a menu.", ["assets"] = new[] { Asset(version, platform) }
};
HttpResponseMessage Json(params object[] releases) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json") };
SoftwareUpdateService Service(FakeHandler handler, string? directory = null, TimeSpan? checkTimeout = null, TimeSpan? downloadTimeout = null) =>
    new(directory ?? root, handler, checkTimeout, downloadTimeout);
async Task<SoftwareUpdateCheckResult> Inspect(object[] releases, SoftwareUpdatePlatform platform = SoftwareUpdatePlatform.WindowsX64, string current = "0.6.45")
{
    using var handler = new FakeHandler((_, _) => Task.FromResult(Json(releases)));
    using var service = Service(handler);
    var result = await service.CheckAsync(current, platform);
    Check(handler.Requests.Count == 1 && handler.Requests[0].Uri == SoftwareUpdateService.ReleasesUrl, "metadata is requested only from the fixed public repository");
    Check(!handler.Requests[0].HasAuthorization, "public release discovery has no credentials");
    return result;
}
async Task Unavailable(Dictionary<string, object?> asset, string reason)
{
    var release = Release(); release["assets"] = new[] { asset };
    var result = await Inspect([release]);
    Check(result.Status == SoftwareUpdateStatus.PackageUnavailable && result.Update is null && result.LatestVersion == TestVersion, reason);
}

try
{
    var ready = await Inspect([Release()]);
    Check(ready.Status == SoftwareUpdateStatus.Available && ready.Update?.Version == TestVersion, "new prerelease is offered to early-alpha users");
    Check(ready.Update!.ReleaseUrl.AbsoluteUri == SoftwareUpdateService.RepositoryUrl + "/releases/tag/v" + TestVersion, "release page is computed from the trusted repository and version");
    Check(ready.Update.IsPrerelease && ready.Update.Sha256 == digest && ready.Update.Size == payload.Length, "release package carries the published digest and size");
    Check(!Directory.Exists(Path.Combine(root, "Updates")), "checking never creates download files");
    var order = await Inspect([Release("0.6.9"), Release("0.6.10"), Release("0.6.2")], current: "0.6.8");
    Check(order.Update?.Version == "0.6.10", "versions compare numerically, independently of release order");
    foreach (string current in new[] { TestVersion, "0.6.99", "1.0.0" })
        Check((await Inspect([Release()], current: current)).Status == SoftwareUpdateStatus.Current, "equal or newer installations are never downgraded");
    Check((await Inspect([])).Status == SoftwareUpdateStatus.Current, "an empty release list offers no update");
    foreach (var platform in Enum.GetValues<SoftwareUpdatePlatform>())
    {
        var result = await Inspect([Release(platform: platform)], platform);
        Check(result.Update?.AssetName == SoftwareUpdateService.PackageName(TestVersion, platform), "platform selects exactly its installer or native archive");
    }
    Check((await Inspect([Release()], SoftwareUpdatePlatform.MacArm64)).Status == SoftwareUpdateStatus.PackageUnavailable, "missing architecture never offers a different package");
    Check((await Inspect([Release(), Release()])).Status == SoftwareUpdateStatus.PackageUnavailable, "duplicate newest releases are ambiguous");
    var draft = Release("9.0.0"); draft["draft"] = true;
    Check((await Inspect([draft, Release()])).Update?.Version == TestVersion, "draft releases never supersede published releases");
    foreach (var change in new (string Key, object? Value)[] { ("tag_name", "v0.6.46-beta"), ("tag_name", "v00.6.46"), ("tag_name", "0.6.46"), ("tag_name", "v99999999999.0.0"), ("draft", "false"), ("published_at", null), ("prerelease", null) })
    {
        var invalid = Release(); invalid[change.Key] = change.Value;
        Check((await Inspect([invalid])).Update is null, "malformed release metadata cannot create an offer");
    }
    foreach (var change in new (string Key, object? Value)[]
    {
        ("digest", null), ("digest", "sha1:" + digest), ("digest", "sha256:12"), ("digest", "sha256:" + new string('z',64)),
        ("size", 0), ("size", -1), ("size", SoftwareUpdateService.MaximumPackageBytes + 1), ("size", "1024"),
        ("state", "new"), ("name", "../INGLORIOUS-Advanced-" + TestVersion + "-Setup.exe"),
        ("browser_download_url", "https://evil.example/package.exe"),
        ("browser_download_url", SoftwareUpdateService.RepositoryUrl.Replace("https:", "http:") + "/releases/download/v" + TestVersion + "/" + SoftwareUpdateService.PackageName(TestVersion, SoftwareUpdatePlatform.WindowsX64)),
        ("browser_download_url", SoftwareUpdateService.RepositoryUrl + "/releases/download/v" + TestVersion + "/" + SoftwareUpdateService.PackageName(TestVersion, SoftwareUpdatePlatform.WindowsX64) + "?redirect=evil")
    })
    {
        var asset = Asset(); asset[change.Key] = change.Value;
        await Unavailable(asset, "invalid, incomplete, oversized or foreign package metadata is unavailable");
    }
    var duplicateAsset = Release(); duplicateAsset["assets"] = new[] { Asset(), Asset() };
    Check((await Inspect([duplicateAsset])).Status == SoftwareUpdateStatus.PackageUnavailable, "duplicate package names are rejected");
    var notes = Release(); notes["body"] = "<b>Update</b>\u001b\u202e\n[Release](https://evil.example)\n" + new string('x', 10000);
    string safeNotes = (await Inspect([notes])).Update!.Notes;
    Check(safeNotes.Length <= 4000 && !safeNotes.Contains('<') && !safeNotes.Contains("evil.example") && !safeNotes.Contains('\u001b') && !safeNotes.Contains('\u202e'), "notes are capped plain text without links, HTML or display controls");
    var pathologicalNotes = Release(); pathologicalNotes["body"] = new string('<', 16000);
    try
    {
        var boundedNotes = await Inspect([pathologicalNotes]);
        Check(boundedNotes.Update?.Notes.Length <= 4000, "unmatched markup in release notes is bounded");
    }
    catch (SoftwareUpdateException) { checks++; } // A sanitizer timeout becomes the normal, handled update error.

    foreach (string malformed in new[] { "{", "{}", "null", JsonSerializer.Serialize(Enumerable.Repeat(Release(), 31)) })
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(malformed) }));
        using var service = Service(handler);
        await Reject(() => service.CheckAsync("0.6.45", SoftwareUpdatePlatform.WindowsX64), "malformed metadata must be rejected");
    }
    using (var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } })))
    using (var service = Service(handler))
    {
        await Reject(() => service.CheckAsync("0.6.45", SoftwareUpdatePlatform.WindowsX64), "API redirects must not be followed");
        Check(handler.Requests.Count == 1, "API redirect does not make a second request");
    }
    using (var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[2 * 1024 * 1024 + 1]) })))
    using (var service = Service(handler))
        await Reject(() => service.CheckAsync("0.6.45", SoftwareUpdatePlatform.WindowsX64), "chunked metadata is bounded too");
    using (var handler = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json(); }))
    using (var service = Service(handler, checkTimeout: TimeSpan.FromMilliseconds(50)))
        await Reject(() => service.CheckAsync("0.6.45", SoftwareUpdatePlatform.WindowsX64), "check has a bounded timeout");
    using (var handler = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json(); }))
    using (var service = Service(handler))
    using (var cancel = new CancellationTokenSource(50))
        await Canceled(() => service.CheckAsync("0.6.45", SoftwareUpdatePlatform.WindowsX64, cancel.Token), "user cancellation remains cancellation");

    async Task DownloadCase(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> download, bool success, string reason, TimeSpan? timeout = null)
    {
        string attemptRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        using var handler = new FakeHandler((request, token) => request.RequestUri!.AbsoluteUri == SoftwareUpdateService.ReleasesUrl ? Task.FromResult(Json(Release())) : download(request, token));
        using var service = Service(handler, attemptRoot, downloadTimeout: timeout);
        var update = (await service.CheckAsync("0.6.45", SoftwareUpdatePlatform.WindowsX64)).Update!;
        Check(handler.Requests.Count == 1, "release discovery does not prefetch a package");
        if (success)
        {
            var progress = new List<double>();
            string path = await service.DownloadAsync(update, new InlineProgress(progress.Add));
            service.VerifyDownloadedFile(update, path);
            Check(File.ReadAllBytes(path).SequenceEqual(payload) && !Directory.EnumerateFiles(attemptRoot, "*.partial", SearchOption.AllDirectories).Any(), reason);
            Check(progress.Count >= 2 && progress[0] == 0 && progress[^1] == 1 && progress.All(p => p is >= 0 and <= 1), "download progress is bounded and finishes only on verification");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('x', payload.Length)));
            await Reject(() => { service.VerifyDownloadedFile(update, path); return Task.CompletedTask; }, "package tampering before launch is rejected");
            string outside = Path.Combine(root, update.AssetName); File.WriteAllBytes(outside, payload);
            await Reject(() => { service.VerifyDownloadedFile(update, outside); return Task.CompletedTask; }, "valid bytes outside the owned cache cannot be launched");
        }
        else
        {
            await Reject(() => service.DownloadAsync(update), reason);
            Check(!Directory.EnumerateFiles(attemptRoot, "*", SearchOption.AllDirectories).Any(), "failed downloads leave no executable or partial file");
        }
    }
    await DownloadCase((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }), true, "verified bytes are atomically promoted into the owned cache");
    await DownloadCase((request, _) => Task.FromResult(request.RequestUri!.Host == "github.com"
        ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?signature=test") } }
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(payload) }), true, "a controlled GitHub CDN redirect can download chunked content");
    foreach (string location in new[] { "https://evil.example/package", "https://release-assets.githubusercontent.com.evil.example/github-production-release-asset/1/2", "http://release-assets.githubusercontent.com/github-production-release-asset/1/2", "https://release-assets.githubusercontent.com:444/github-production-release-asset/1/2", "https://release-assets.githubusercontent.com/other/1/2", "https://evil@release-assets.githubusercontent.com/github-production-release-asset/1/2", "https://github.com/another/repo/releases/download/v1/file.exe" })
        await DownloadCase((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location) } }), false, "foreign, downgrade, misleading or unexpected redirects are rejected");
    await DownloadCase((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2") } }), false, "redirect loops are bounded");
    await DownloadCase((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[payload.Length]) }), false, "matching size with wrong hash is rejected");
    await DownloadCase((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[payload.Length + 1]) }), false, "wrong content length is rejected before writing");
    await DownloadCase((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[payload.Length + 1]) }), false, "chunked oversize response is bounded during streaming");
    await DownloadCase((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(payload[..^1]) }), false, "truncated chunked package is rejected");
    await DownloadCase(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }, false, "download timeout prevents an indefinite wait", TimeSpan.FromMilliseconds(50));
    using (var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri == SoftwareUpdateService.ReleasesUrl ? Json(Release()) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new WaitingContent() })))
    using (var service = Service(handler, Path.Combine(root, "cancel")))
    using (var cancel = new CancellationTokenSource())
    {
        var update = (await service.CheckAsync("0.6.45", SoftwareUpdatePlatform.WindowsX64)).Update!;
        cancel.CancelAfter(50);
        await Canceled(() => service.DownloadAsync(update, cancellationToken: cancel.Token), "cancellation interrupts a blocked response stream");
        Check(!Directory.EnumerateFiles(Path.Combine(root, "cancel"), "*", SearchOption.AllDirectories).Any(), "canceled download deletes its own incomplete file");
    }
    using (var cancel = new CancellationTokenSource())
    {
        SoftwareUpdateService? closingService = null;
        using var handler = new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == SoftwareUpdateService.ReleasesUrl) return Task.FromResult(Json(Release()));
            cancel.Cancel(); closingService!.Dispose();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2") } });
        });
        using var service = Service(handler, Path.Combine(root, "close-race")); closingService = service;
        var update = (await service.CheckAsync("0.6.45", SoftwareUpdatePlatform.WindowsX64)).Update!;
        await Canceled(() => service.DownloadAsync(update, cancellationToken: cancel.Token), "closing while following a redirect cancels without a disposed-client exception");
        Check(handler.Requests.Count == 2, "a canceled redirect never starts its next request");
    }
    Console.WriteLine($"PASS: {checks} software update checks. Fake HTTP only; no network, installation, app settings or hardware access.");
}
finally
{
    // This exact freshly created test root never contains production data.
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    public List<(string Uri, bool HasAuthorization)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization is not null));
        return response(request, cancellationToken);
    }
}
sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
}
sealed class WaitingContent : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new WaitingStream());
}
sealed class WaitingStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
