using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace InGlorious.Core;

public static class ManufacturerFirmwareDownload
{
    private const string DownloadUrl = "https://nyc3.digitaloceanspaces.com/gloriouscore/Glorious_Core/1.0.4_v0003_20240726_GMMK3_PRO_ISO_wired_100.zip";
    private const string ApplicationEntry = "1.0.4_v0003_20240726_GMMK3_PRO_ISO_wired_100/USB/Gmmk3_Pro_Onix_UK_Wired_104_V0003_20240726.bin";
    private const int PackageBytes = 1948369;
    private const string PackageSha256 = "C1B398041DDC36872BE37933C93CDDE0E56C5E4C9D8DCFD4A189CA476875A2ED";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    public static async Task<byte[]> GetApplicationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await Client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("MANUFACTURER_DOWNLOAD_FAILED", null, response.StatusCode);
        return await ReadPackageAsync(response.Content, timeout.Token).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadPackageAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared != PackageBytes)
            throw new InvalidDataException("MANUFACTURER_PACKAGE_SIZE_INVALID");
        using var body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[PackageBytes];
        await body.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (await body.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(PackageSha256)))
            throw new InvalidDataException("MANUFACTURER_PACKAGE_MISMATCH");

        using var package = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var entry = package.GetEntry(ApplicationEntry);
        if (entry is null || entry.Length != ManufacturerFirmware.ApplicationBytes)
            throw new InvalidDataException("MANUFACTURER_APPLICATION_MISSING");
        using var stream = entry.Open();
        var application = new byte[ManufacturerFirmware.ApplicationBytes];
        stream.ReadExactly(application);
        ManufacturerFirmware.ValidateApplication(application);
        return application;
    }
}
