using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using Migration.Engine;
using Migration.Engine.Utils;

namespace Web.Services;

/// <summary>
/// Expands a selected SharePoint <b>folder</b> into the individual files beneath
/// it so the migrator — which only ever archives one file at a time — receives a
/// flat list of files. Without this, a folder selection was handed to the worker
/// as if it were a single file: it downloaded garbage, then failed at the delete
/// step with "file not found" (<c>DeleteFailed</c>). The eligibility comment in
/// <c>ArchiveEligibility.cs</c> ("folders are expanded to their constituent files
/// by the worker") describes intended behaviour that lives here.
/// </summary>
public interface ISharePointFolderExpansionService
{
    Task<FolderExpansionResult> ExpandAsync(
        string siteUrl,
        string folderServerRelativeUrl,
        bool recursive,
        int maxFiles,
        CancellationToken cancellationToken = default);
}

/// <summary>One file discovered while expanding a folder.</summary>
public sealed record ExpandedFile(string ServerRelativeUrl, long FileSize, DateTime? LastModified);

public sealed class FolderExpansionResult
{
    public List<ExpandedFile> Files { get; } = [];

    /// <summary>Non-null when expansion partially failed or the folder was empty/capped.</summary>
    public string? Warning { get; set; }
}

public sealed class SharePointFolderExpansionService(Config config, ILogger<SharePointFolderExpansionService> logger)
    : ISharePointFolderExpansionService
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<SharePointFolderExpansionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<FolderExpansionResult> ExpandAsync(
        string siteUrl,
        string folderServerRelativeUrl,
        bool recursive,
        int maxFiles,
        CancellationToken cancellationToken = default)
    {
        var result = new FolderExpansionResult();
        if (maxFiles <= 0)
        {
            result.Warning = $"'{folderServerRelativeUrl}' was not expanded: the per-request file limit was already reached. Migrate it in a separate request.";
            return result;
        }
        if (string.IsNullOrWhiteSpace(siteUrl) || string.IsNullOrWhiteSpace(folderServerRelativeUrl))
        {
            result.Warning = "Folder could not be expanded: missing site or folder URL.";
            return result;
        }

        var rootUrl = folderServerRelativeUrl.TrimEnd('/');
        try
        {
            using var ctx = await AuthUtils.GetClientContext(_config, siteUrl, _logger, null).ConfigureAwait(false);

            var pending = new Queue<string>();
            pending.Enqueue(rootUrl);
            var capped = false;

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentUrl = pending.Dequeue();
                var isRoot = string.Equals(currentUrl, rootUrl, StringComparison.OrdinalIgnoreCase);

                var folder = ctx.Web.GetFolderByServerRelativeUrl(currentUrl);
                ctx.Load(folder,
                    f => f.Exists,
                    f => f.ServerRelativeUrl,
                    f => f.Files.Include(fi => fi.ServerRelativeUrl, fi => fi.Length, fi => fi.TimeLastModified),
                    f => f.Folders.Include(sf => sf.ServerRelativeUrl, sf => sf.Name));
                await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

                if (!folder.Exists)
                {
                    continue;
                }

                foreach (var file in folder.Files)
                {
                    if (result.Files.Count >= maxFiles)
                    {
                        capped = true;
                        break;
                    }
                    result.Files.Add(new ExpandedFile(file.ServerRelativeUrl, file.Length, file.TimeLastModified));
                }

                if (capped)
                {
                    break;
                }

                if (recursive)
                {
                    foreach (var sub in folder.Folders)
                    {
                        // Only the document library's ROOT "Forms" folder is a system
                        // folder (aspx form templates). Skip it there, but never omit a
                        // user folder deeper in the tree that merely happens to be named
                        // "Forms".
                        if (isRoot && IsSystemFolder(sub.Name))
                        {
                            continue;
                        }
                        pending.Enqueue(sub.ServerRelativeUrl);
                    }
                }
            }

            if (capped)
            {
                result.Warning = $"'{folderServerRelativeUrl}' hit the file limit ({maxFiles}); only the first {maxFiles} files were queued. Migrate sub-folders separately to archive the rest.";
            }
            else if (result.Files.Count == 0)
            {
                result.Warning = $"'{folderServerRelativeUrl}' contained no files to migrate.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to expand folder '{Folder}' on '{Site}'.", folderServerRelativeUrl, siteUrl);
            result.Warning = $"'{folderServerRelativeUrl}' could not be read from SharePoint ({ex.Message}); skipping.";
        }

        return result;
    }

    /// <summary>
    /// Skip the document library's system "Forms" folder (aspx form templates) so
    /// a library-root selection doesn't sweep in system pages.
    /// </summary>
    private static bool IsSystemFolder(string? name)
        => string.Equals(name, "Forms", StringComparison.OrdinalIgnoreCase);
}
