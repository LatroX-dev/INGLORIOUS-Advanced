using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InGlorious.Core;

public enum SoftwareUpdatePlatform { WindowsX64, MacX64, MacArm64, LinuxX64 }
public enum SoftwareUpdateStatus { Current, Available, PackageUnavailable }
public sealed record SoftwareUpdateCheckResult(SoftwareUpdateStatus Status, string? LatestVersion, SoftwareUpdateInfo? Update);

/// <summary>A package selected from the fixed public repository, not an arbitrary download URL.</summary>
public sealed class SoftwareUpdateInfo
{
    public string Version { get; }
    public Uri ReleaseUrl { get; }
    public string Notes { get; }
    public bool IsPrerelease { get; }
    public string AssetName { get; }
    public long Size { get; }
    public string Sha256 { get; }
    internal Uri DownloadUrl { get; }
    internal SoftwareUpdateInfo(string version, string notes, bool prerelease, string assetName, long size, string sha256)
    {
        Version = version; Notes = notes; IsPrerelease = prerelease; AssetName = assetName; Size = size; Sha256 = sha256;
        ReleaseUrl = new($"{SoftwareUpdateService.RepositoryUrl}/releases/tag/v{version}");
        DownloadUrl = new($"{SoftwareUpdateService.RepositoryUrl}/releases/download/v{version}/{assetName}");
    }
}

/// <summary>Technical detail for logs. The UI supplies its own short, translated error message.</summary>
public sealed class SoftwareUpdateException : IOException
{
    public SoftwareUpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Checks public GitHub releases and verifies explicitly requested downloads. Never launches a package.</summary>
public sealed class SoftwareUpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/LatroX-dev/INGLORIOUS-Advanced";
    internal const string ReleasesUrl = "https://api.github.com/repos/LatroX-dev/INGLORIOUS-Advanced/releases?per_page=30";
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private readonly string _cache;
    private readonly HttpClient _http;
    private readonly TimeSpan _checkTimeout, _downloadTimeout;

    public SoftwareUpdateService(string userDataRoot) : this(userDataRoot, new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { }

    // Only offline checks inject a handler. Production always checks each redirect itself.
    internal SoftwareUpdateService(string userDataRoot, HttpMessageHandler handler,
        TimeSpan? checkTimeout = null, TimeSpan? downloadTimeout = null)
    {
        if (!Path.IsPathFullyQualified(userDataRoot)) throw new ArgumentException("An absolute application data directory is required.", nameof(userDataRoot));
        _cache = Path.Combine(Path.GetFullPath(userDataRoot), "Updates");
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(25);
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(10);
    }

    public static string CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(SoftwareUpdateService).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
            if (TryVersion(informational, out _)) return informational!;
            var version = assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public static SoftwareUpdatePlatform? CurrentPlatform =>
        OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64 && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? SoftwareUpdatePlatform.WindowsX64
        : OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? SoftwareUpdatePlatform.MacArm64
        : OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? SoftwareUpdatePlatform.MacX64
        : OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? SoftwareUpdatePlatform.LinuxX64 : null;

    public async Task<SoftwareUpdateCheckResult> CheckAsync(string currentVersion, SoftwareUpdatePlatform platform, CancellationToken cancellationToken = default)
    {
        if (!TryVersion(currentVersion, out var current)) throw new ArgumentException("A numeric application version is required.", nameof(currentVersion));
        _ = PackageName(currentVersion, platform);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_checkTimeout);
        try
        {
            using var request = Request(new Uri(ReleasesUrl), metadata: true);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // No API redirects: the owner/repository must remain the configured public source.
            if (response.StatusCode != HttpStatusCode.OK) throw new SoftwareUpdateException($"Release check returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaximumMetadataBytes) throw new SoftwareUpdateException("Release metadata is too large.");
            byte[] bytes = await ReadBoundedAsync(response.Content, MaximumMetadataBytes, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 30)
                throw new SoftwareUpdateException("Unexpected release metadata.");
            Version? latest = null; JsonElement selected = default; bool duplicate = false;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object || !Boolean(release, "draft", out bool draft) || draft ||
                    !Boolean(release, "prerelease", out _) || !Text(release, "tag_name", out string tag) || !tag.StartsWith('v') ||
                    !TryVersion(tag[1..], out var version) || !Text(release, "published_at", out string published) ||
                    !DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) continue;
                if (latest is null || version > latest) { latest = version; selected = release; duplicate = false; }
                else if (version == latest) duplicate = true;
            }
            if (latest is null || latest <= current) return new(SoftwareUpdateStatus.Current, latest?.ToString(), null);
            string latestText = latest.ToString();
            var info = duplicate ? null : ParsePackage(selected, latestText, platform);
            return new(info is null ? SoftwareUpdateStatus.PackageUnavailable : SoftwareUpdateStatus.Available, latestText, info);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new SoftwareUpdateException("The software update check timed out.", ex); }
        catch (ObjectDisposedException ex) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("The software update check was canceled.", ex, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or RegexMatchTimeoutException)
        { throw new SoftwareUpdateException("The software update check could not be completed.", ex); }
    }

    public async Task<string> DownloadAsync(SoftwareUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateInfo(update);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_downloadTimeout);
        string? partial = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The directory is app-owned; each attempt has a new name and never overwrites files.
            AssertNoLinks(_cache);
            Directory.CreateDirectory(_cache);
            AssertNoLinks(_cache);
            string directory = Path.Combine(_cache, update.Version, Guid.NewGuid().ToString("N"));
            AssertNoLinks(directory);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            AssertNoLinks(directory);
            partial = Path.Combine(directory, update.AssetName + ".partial");
            string complete = Path.Combine(directory, update.AssetName);
            using var response = await OpenPackageAsync(update.DownloadUrl, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new SoftwareUpdateException($"Package download returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is long length && length != update.Size)
                throw new SoftwareUpdateException("The package size differs from the release metadata.");
            if (response.Content.Headers.ContentEncoding.Count != 0) throw new SoftwareUpdateException("Encoded package responses are not accepted.");
            progress?.Report(0);
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920]; long received = 0;
                while (true)
                {
                    int count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > update.Size || received > MaximumPackageBytes) throw new SoftwareUpdateException("The downloaded package is larger than expected.");
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    // Reaching the last byte is not success until its checksum is verified.
                    progress?.Report(0.99 * received / update.Size);
                }
                if (received != update.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                    throw new SoftwareUpdateException("The downloaded package did not match its published checksum and size.");
                await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            timeout.Token.ThrowIfCancellationRequested();
            AssertNoLinks(directory);
            File.Move(partial, complete, overwrite: false);
            partial = null;
            progress?.Report(1);
            return complete;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new SoftwareUpdateException("The software update download timed out.", ex); }
        catch (ObjectDisposedException ex) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("The software update download was canceled.", ex, cancellationToken); }
        catch (HttpRequestException ex) { throw new SoftwareUpdateException("The software update download could not be completed.", ex); }
        finally
        {
            if (partial is not null)
            {
                try { AssertNoLinks(partial); File.Delete(partial); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Rechecks a downloaded package immediately before the UI offers it to the operating system.</summary>
    public void VerifyDownloadedFile(SoftwareUpdateInfo update, string path)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateInfo(update);
        string full = Path.GetFullPath(path);
        string[] parts = Path.GetRelativePath(_cache, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path) || parts.Length != 3 || parts[0] != update.Version ||
            !Guid.TryParseExact(parts[1], "N", out _) || parts[2] != update.AssetName)
            throw new SoftwareUpdateException("The package is outside its update cache directory.");
        AssertNoLinks(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != update.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(update.Sha256)))
            throw new SoftwareUpdateException("The downloaded package has changed since verification.");
    }

    private static void ValidateInfo(SoftwareUpdateInfo update)
    {
        if (!TryVersion(update.Version, out _) || !Enum.GetValues<SoftwareUpdatePlatform>().Any(platform => PackageName(update.Version, platform) == update.AssetName) ||
            update.Size <= 0 || update.Size > MaximumPackageBytes || !Regex.IsMatch(update.Sha256, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant) ||
            update.DownloadUrl.AbsoluteUri != $"{RepositoryUrl}/releases/download/v{update.Version}/{update.AssetName}")
            throw new SoftwareUpdateException("The software update package metadata is invalid.");
    }

    private async Task<HttpResponseMessage> OpenPackageAsync(Uri original, CancellationToken cancellationToken)
    {
        Uri current = original;
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = Request(current, metadata: false);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var next = response.Headers.Location;
            response.Dispose();
            if (next is null || redirects == 3) throw new SoftwareUpdateException("The package redirect could not be verified.");
            if (!next.IsAbsoluteUri) next = new Uri(current, next);
            if (!SafeAssetRedirect(next, original)) throw new SoftwareUpdateException("The package redirect left the allowed download hosts.");
            current = next;
        }
        throw new SoftwareUpdateException("Too many package redirects.");
    }

    private static bool SafeAssetRedirect(Uri uri, Uri original) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsoluteUri.Length <= 8192 &&
        (uri.AbsoluteUri == original.AbsoluteUri ||
         (uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com") &&
         uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal));

    private static HttpRequestMessage Request(Uri uri, bool metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("INGLORIOUS-Advanced-Updates/1.0");
        request.Headers.Accept.ParseAdd(metadata ? "application/vnd.github+json" : "application/octet-stream");
        if (metadata) request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    private static SoftwareUpdateInfo? ParsePackage(JsonElement release, string version, SoftwareUpdatePlatform platform)
    {
        string name = PackageName(version, platform);
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 256) return null;
        var matches = assets.EnumerateArray().Where(asset => Text(asset, "name", out string assetName) && assetName == name).ToArray();
        if (matches.Length != 1) return null;
        var selected = matches[0];
        string expectedUrl = $"{RepositoryUrl}/releases/download/v{version}/{name}";
        if (!Text(selected, "state", out string state) || state != "uploaded" ||
            !Text(selected, "browser_download_url", out string url) || url != expectedUrl ||
            !Text(selected, "digest", out string digest) || !Regex.IsMatch(digest, "\\Asha256:[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant) ||
            !selected.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes <= 0 || bytes > MaximumPackageBytes)
            return null;
        Text(release, "body", out string body);
        Boolean(release, "prerelease", out bool prerelease);
        return new(version, PlainNotes(body), prerelease, name, bytes, digest[7..].ToLowerInvariant());
    }

    internal static string PackageName(string version, SoftwareUpdatePlatform platform) => platform switch
    {
        SoftwareUpdatePlatform.WindowsX64 => $"INGLORIOUS-Advanced-{version}-Setup.exe",
        SoftwareUpdatePlatform.MacX64 => $"INGLORIOUS-Advanced-{version}-osx-x64.tar.gz",
        SoftwareUpdatePlatform.MacArm64 => $"INGLORIOUS-Advanced-{version}-osx-arm64.tar.gz",
        SoftwareUpdatePlatform.LinuxX64 => $"INGLORIOUS-Advanced-{version}-linux-x64.tar.gz",
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    private static string PlainNotes(string body)
    {
        // The UI renders this as plain text, never HTML/Markdown or a web view.
        string input = body.Length > 16000 ? body[..16000] : body;
        input = Regex.Replace(input, "<[^>]*>", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        input = Regex.Replace(input, @"!?\[([^\]\r\n]*)\]\([^\)\r\n]*\)", "$1", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        var safe = new StringBuilder();
        foreach (char c in input.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format) safe.Append(c);
            if (safe.Length == 4000) break;
        }
        return string.Join('\n', safe.ToString().Split('\n').Take(50).Select(line => line.Trim().TrimStart('#').TrimStart())).Trim();
    }

    private static bool TryVersion(string? text, out Version version)
    {
        version = new(0, 0, 0);
        return text is { Length: <= 32 } && Regex.IsMatch(text, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant) &&
            Version.TryParse(text, out version!);
    }
    private static bool Text(JsonElement element, string key, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!; return true;
    }
    private static bool Boolean(JsonElement element, string key, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean(); return true;
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); byte[] buffer = new byte[81920];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new SoftwareUpdateException("Release metadata is too large.");
            output.Write(buffer, 0, count);
        }
    }
    private static void AssertNoLinks(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new SoftwareUpdateException("Linked update cache paths are not allowed.");
        }
    }
    public void Dispose() => _http.Dispose();
}
