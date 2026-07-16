using Microsoft.Identity.Client;
using Entities.Configuration;
using Migration.Engine.Utils;
using Migration.Engine.Utils.Http;
using Models;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Migration;
/// <summary>
/// Downloads files from SharePoint to local file-system
/// </summary>
public class SharePointFileDownloader : BaseComponent
{
    private readonly IConfidentialClientApplication _app;
    private readonly SecureSPThrottledHttpClient _client;
    public SharePointFileDownloader(IConfidentialClientApplication app, Config config, ILogger ILogger) : base(config, ILogger)
    {
        _app = app;
        _client = new SecureSPThrottledHttpClient(config, true, ILogger);

        var productValue = new ProductInfoHeaderValue("SPOColdStorageMigration", "1.0");
        var commentValue = new ProductInfoHeaderValue("(+https://github.com/sambetts/SPOColdStorage)");

        _client.DefaultRequestHeaders.UserAgent.Add(productValue);
        _client.DefaultRequestHeaders.UserAgent.Add(commentValue);
    }

    /// <summary>
    /// Download file & return temp file-name + size
    /// </summary>
    /// <returns>Temp file-path and size</returns>
    /// <remarks>
    /// Uses manual HTTP calls as CSOM doesn't work with files > 2gb. 
    /// This routine writes 2mb chunks at a time to a temp file from HTTP response.
    /// </remarks>
    public async Task<(string, long)> DownloadFileToTempDir(BaseSharePointFileInfo sharePointFile)
    {
        // Write to temp file
        var tempFileName = GetTempFileNameAndCreateDir(sharePointFile);

        _logger.LogDebug($"Downloading '{sharePointFile.FullSharePointUrl}'...");
        var url = $"{sharePointFile.WebUrl}/_api/web/GetFileByServerRelativeUrl('{sharePointFile.ServerRelativeFilePath}')/OpenBinaryStream";

        long fileSize = 0;

        // Get response but don't buffer full content (which will buffer overlflow for large files)
        using (var response = await _client.GetAsyncWithThrottleRetries(url, HttpCompletionOption.ResponseHeadersRead, _logger))
        {
            // FAILSAFE: the throttle client only retries HTTP 429 and returns any
            // other non-success (403/404/500 or an auth-expired HTML login page)
            // as-is. Without this, that error body would be written to the temp
            // file, later pass length/MD5 validation (which only compare to the
            // downloaded bytes), and the good SharePoint source could then be
            // deleted. Throwing here keeps the source safe (copy step fails).
            response.EnsureSuccessStatusCode();
            var declaredLength = response.Content.Headers.ContentLength;

            using var streamToReadFrom = await response.Content.ReadAsStreamAsync();
            using var streamToWriteTo = File.Open(tempFileName, FileMode.Create);
            await streamToReadFrom.CopyToAsync(streamToWriteTo);
            fileSize = streamToWriteTo.Length;

            // Detect a truncated stream: if SharePoint declared a Content-Length,
            // the bytes we wrote must match it exactly.
            if (declaredLength.HasValue && declaredLength.Value != fileSize)
            {
                throw new IOException(
                    $"Download of '{sharePointFile.FullSharePointUrl}' was truncated: wrote {fileSize:N0} bytes, expected {declaredLength.Value:N0}.");
            }
        }

        _logger.LogDebug($"Wrote {fileSize:N0} bytes to '{tempFileName}'.");

        // Return file name & size
        return (tempFileName, fileSize);
    }

    public static string GetTempFileNameAndCreateDir(BaseSharePointFileInfo sharePointFile)
    {
        // Use a short, unique, cross-platform temp path. The previous version
        // replicated the file's full SharePoint server-relative path (with
        // Windows backslashes) under the temp dir, which overflowed the OS path
        // limit for deeply-nested files (PathTooLongException) and — on Linux,
        // where '\' is not a separator — collapsed into one oversized filename
        // component. The temp file only needs somewhere to stream the bytes; the
        // original path is preserved on the item/placeholder + blob key elsewhere.
        var ext = Path.GetExtension(sharePointFile.ServerRelativeFilePath);
        var dir = Path.Combine(Path.GetTempPath(), "SpoColdStorageMigration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "download" + ext);
    }
}
