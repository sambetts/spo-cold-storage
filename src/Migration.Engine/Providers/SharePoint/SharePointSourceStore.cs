using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using Migration.Engine.Migration;
using Migration.Engine.Utils;
using Models;
using Models.ColdStorage;

namespace Migration.Engine.Providers.SharePoint;

/// <summary>
/// SharePoint Online implementation of <see cref="ISourceStore"/>. Orchestrates the proven CSOM /
/// download / placeholder helpers behind the provider-neutral contract, and encodes the two SP
/// quirks the pipelines rely on: a delete of an already-gone file is a success (File-Not-Found),
/// and an upload whose response was lost is treated as done when the bytes actually landed.
/// Throttles surface as the raw CSOM exceptions, which <see cref="Lifecycle.TransientErrorClassifier"/>
/// already recognises, so the pipeline parks + retries them.
/// </summary>
public sealed class SharePointSourceStore(Config config, ILogger logger) : ISourceStore
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly SharePointPlaceholderWriter _placeholderWriter = new(logger);
    private readonly IArchiveHoldDetector? _holdDetector = config.ColdStorageSkipRetentionLabeled > 0 ? new RetentionLabelHoldDetector(logger) : null;

    public string ProviderId => "SharePointOnline";

    private Task<ClientContext> ContextAsync(string siteUrl) => AuthUtils.GetClientContext(_config, siteUrl, _logger, null, warmUpWeb: false);

    public async Task<SourceItemInfo> GetItemAsync(SourceItemRef item, CancellationToken cancellationToken = default)
    {
        using var ctx = await ContextAsync(item.StoreUrl).ConfigureAwait(false);
        var file = ctx.Web.GetFileByServerRelativeUrl(item.ItemPath);
        ctx.Load(file, f => f.Exists, f => f.Length, f => f.CheckOutType, f => f.ListItemAllFields);
        try
        {
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileNotFound(ex))
        {
            return SourceItemInfo.Missing;
        }
        if (!file.Exists)
        {
            return SourceItemInfo.Missing;
        }

        string? createdBy = null, modifiedBy = null;
        DateTime? created = null, modified = null;
        try
        {
            var li = file.ListItemAllFields;
            createdBy = (li["Author"] as FieldUserValue)?.LookupValue;
            modifiedBy = (li["Editor"] as FieldUserValue)?.LookupValue;
            if (li["Created"] is DateTime c) { created = c; }
            if (li["Modified"] is DateTime m) { modified = m; }
        }
        catch (Exception) { /* best-effort authorship capture */ }

        return new SourceItemInfo
        {
            Exists = true,
            Length = file.Length,
            CreatedUtc = created,
            LastModifiedUtc = modified,
            CreatedBy = createdBy,
            ModifiedBy = modifiedBy,
            IsLocked = file.CheckOutType != CheckOutType.None,
            LockReason = file.CheckOutType != CheckOutType.None ? "checked out" : null,
        };
    }

    public async Task<HoldStatus> CheckHoldAsync(SourceItemRef item, CancellationToken cancellationToken = default)
    {
        if (_holdDetector is null)
        {
            return HoldStatus.NotOnHold;
        }
        using var ctx = await ContextAsync(item.StoreUrl).ConfigureAwait(false);
        return await _holdDetector.CheckAsync(ctx, item.ItemPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ITransferContent> ReadContentAsync(SourceItemRef item, CancellationToken cancellationToken = default)
    {
        var app = await AuthUtils.GetNewClientApp(_config).ConfigureAwait(false);
        var downloader = new SharePointFileDownloader(app, _config, _logger);
        // The downloader streams to a temp file and verifies the declared Content-Length (truncation
        // guard), so a partial download can never be written back or archived.
        var (tempFile, _) = await downloader.DownloadFileToTempDir(ToFileInfo(item)).ConfigureAwait(false);
        return TempFileTransferContent.FromExistingFile(tempFile);
    }

    public async Task DeleteAsync(SourceItemRef item, CancellationToken cancellationToken = default)
    {
        using var ctx = await ContextAsync(item.StoreUrl).ConfigureAwait(false);
        try
        {
            var file = ctx.Web.GetFileByServerRelativeUrl(item.ItemPath);
            file.DeleteObject();
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileNotFound(ex))
        {
            // Already gone — the archival goal is met and nothing new is deleted. Idempotent success.
            _logger.LogWarning("Source '{Path}' already gone at delete; treating as deleted.", item.ItemPath);
        }
    }

    public async Task<string> WriteContentAsync(SourceItemRef item, ITransferContent content, ConflictBehavior conflict, CancellationToken cancellationToken = default)
    {
        using var ctx = await ContextAsync(item.StoreUrl).ConfigureAwait(false);
        var destinationUrl = item.ItemPath;

        // Conflict handling: if the destination exists and it isn't our own same-length prior upload,
        // apply the requested behaviour.
        var existingLength = await FileLengthOrNullAsync(ctx, destinationUrl).ConfigureAwait(false);
        if (existingLength is long len)
        {
            if (len == content.Length)
            {
                return destinationUrl; // response-lost-but-landed: our content is already there.
            }
            destinationUrl = conflict switch
            {
                ConflictBehavior.Overwrite => destinationUrl,
                ConflictBehavior.Rename => RenameToAvoidConflict(destinationUrl),
                _ => throw TransferProviderException.Permanent($"Conflict at '{destinationUrl}' and conflict behaviour = Fail.", ProviderId),
            };
        }

        var folderUrl = ParentFolder(destinationUrl);
        var folder = ctx.Web.GetFolderByServerRelativeUrl(folderUrl);
        ctx.Load(folder);
        await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

        await using var stream = await content.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        var addInfo = new FileCreationInformation
        {
            ContentStream = stream,
            Url = Path.GetFileName(destinationUrl),
            Overwrite = conflict == ConflictBehavior.Overwrite,
        };
        try
        {
            var uploaded = folder.Files.Add(addInfo);
            ctx.Load(uploaded, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            return uploaded.ServerRelativeUrl;
        }
        catch (Exception ex) when (Lifecycle.TransientErrorClassifier.IsTransient(ex))
        {
            // The request may have reached SharePoint even though the response was lost. If the file
            // is now present with the expected length, the upload succeeded — treat as done; else
            // rethrow so the item parks for a clean retry.
            var landed = await FileLengthOrNullAsync(ctx, destinationUrl).ConfigureAwait(false);
            if (landed != content.Length)
            {
                throw;
            }
            _logger.LogWarning(ex, "Upload response for '{Url}' lost, but the file landed with the expected size; treating as uploaded.", destinationUrl);
            return destinationUrl;
        }
    }

    public async Task<string> WritePointerAsync(SourceItemRef item, PlaceholderFileMetadata pointer, string? userFacingUrl = null, CancellationToken cancellationToken = default)
    {
        using var ctx = await ContextAsync(item.StoreUrl).ConfigureAwait(false);
        var placeholderUrl = await _placeholderWriter.WritePlaceholderAsync(ctx, item.ItemPath, pointer, cancellationToken, userFacingUrl).ConfigureAwait(false);
        await _placeholderWriter.StampOriginalMetadataAsync(ctx, placeholderUrl, pointer, cancellationToken).ConfigureAwait(false);
        return placeholderUrl;
    }

    public async Task<PlaceholderFileMetadata?> ReadPointerAsync(SourceItemRef pointer, CancellationToken cancellationToken = default)
    {
        using var ctx = await ContextAsync(pointer.StoreUrl).ConfigureAwait(false);
        var file = ctx.Web.GetFileByServerRelativeUrl(pointer.ItemPath);
        ctx.Load(file, f => f.Exists);
        try
        {
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileNotFound(ex))
        {
            return null;
        }
        if (!file.Exists)
        {
            return null;
        }

        var streamResult = file.OpenBinaryStream();
        await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await streamResult.Value.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return PlaceholderFileMetadata.TryParse(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    public async Task RemovePointerAsync(SourceItemRef pointer, CancellationToken cancellationToken = default)
    {
        using var ctx = await ContextAsync(pointer.StoreUrl).ConfigureAwait(false);
        try
        {
            var file = ctx.Web.GetFileByServerRelativeUrl(pointer.ItemPath);
            file.DeleteObject();
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileNotFound(ex))
        {
            // Idempotent: a missing pointer is a success.
        }
    }

    private async Task<long?> FileLengthOrNullAsync(ClientContext ctx, string serverRelativeUrl)
    {
        var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
        ctx.Load(file, f => f.Exists, f => f.Length);
        try
        {
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
        catch (ServerException)
        {
            return null;
        }
        return file.Exists ? file.Length : null;
    }

    private static BaseSharePointFileInfo ToFileInfo(SourceItemRef item) => new()
    {
        SiteUrl = item.StoreUrl,
        WebUrl = string.IsNullOrEmpty(item.WebUrl) ? item.StoreUrl : item.WebUrl,
        ServerRelativeFilePath = item.ItemPath,
        LastModified = DateTime.UtcNow,
    };

    private static string ParentFolder(string serverRelativeUrl)
    {
        var idx = serverRelativeUrl.LastIndexOf('/');
        return idx <= 0 ? "/" : serverRelativeUrl[..idx];
    }

    private static string RenameToAvoidConflict(string destinationUrl)
    {
        var folder = ParentFolder(destinationUrl);
        var name = Path.GetFileNameWithoutExtension(destinationUrl);
        var ext = Path.GetExtension(destinationUrl);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return $"{folder}/{name}.restored-{stamp}{ext}";
    }

    /// <summary>True when the exception chain indicates the SharePoint file is already gone.</summary>
    private static bool IsFileNotFound(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is ServerException se
                && (string.Equals(se.ServerErrorTypeName, "System.IO.FileNotFoundException", StringComparison.OrdinalIgnoreCase)
                    || (se.Message?.Contains("File Not Found", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                return true;
            }
        }
        return false;
    }
}
