using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using Models.ColdStorage;
using System.Text;

namespace Migration.Engine.Migration;

/// <summary>
/// Writes the ".url" placeholder file into SharePoint and copies role
/// assignments from the source file onto it so the placeholder retains the
/// same access controls as the original.
/// </summary>
public sealed class SharePointPlaceholderWriter(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public static string BuildPlaceholderServerRelativeUrl(string originalServerRelativeUrl)
    {
        if (string.IsNullOrEmpty(originalServerRelativeUrl))
        {
            throw new ArgumentException("originalServerRelativeUrl must be provided", nameof(originalServerRelativeUrl));
        }
        return originalServerRelativeUrl + ".url";
    }

    /// <summary>
    /// Uploads the ".url" file to the same folder as the source. Returns the
    /// server-relative URL of the new placeholder.
    /// </summary>
    public async Task<string> WritePlaceholderAsync(
        ClientContext ctx,
        string originalServerRelativeUrl,
        PlaceholderFileMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(metadata);

        cancellationToken.ThrowIfCancellationRequested();

        var placeholderUrl = BuildPlaceholderServerRelativeUrl(originalServerRelativeUrl);
        var content = metadata.BuildUrlFileContent();
        var bytes = Encoding.UTF8.GetBytes(content);

        // Resolve the folder for the source file then upload the .url next to it.
        var folderServerRelativeUrl = GetParentFolder(originalServerRelativeUrl);
        var folder = ctx.Web.GetFolderByServerRelativeUrl(folderServerRelativeUrl);
        ctx.Load(folder);
        await ctx.ExecuteQueryAsync().ConfigureAwait(false);

        using var ms = new MemoryStream(bytes);
        var fileInfo = new FileCreationInformation
        {
            ContentStream = ms,
            Url = Path.GetFileName(placeholderUrl),
            Overwrite = true,
        };

        var addedFile = folder.Files.Add(fileInfo);
        ctx.Load(addedFile, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync().ConfigureAwait(false);

        _logger.LogInformation("Placeholder uploaded to '{Url}'.", addedFile.ServerRelativeUrl);
        return addedFile.ServerRelativeUrl;
    }

    /// <summary>
    /// Copies role assignments from a source list-item to a destination
    /// list-item. Skips silently when the source had inherited permissions
    /// (nothing to copy) and logs without throwing so the migration is not
    /// failed by a permissions mismatch.
    /// </summary>
    public async Task<bool> CopyRoleAssignmentsAsync(
        ClientContext ctx,
        ListItem source,
        ListItem destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            ctx.Load(source, s => s.HasUniqueRoleAssignments, s => s.RoleAssignments.Include(r => r.Member, r => r.RoleDefinitionBindings));
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);

            if (!source.HasUniqueRoleAssignments)
            {
                _logger.LogDebug("Source had inherited permissions - placeholder will inherit too.");
                return true;
            }

            destination.BreakRoleInheritance(false, false);
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);

            foreach (var assignment in source.RoleAssignments)
            {
                var bindings = new RoleDefinitionBindingCollection(ctx);
                foreach (var def in assignment.RoleDefinitionBindings)
                {
                    bindings.Add(def);
                }
                destination.RoleAssignments.Add(assignment.Member, bindings);
            }
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per requirements: "Items with broken inheritance must retain
            // equivalent permissions on the replacement .url file" - but failure
            // to copy permissions on the placeholder must not undo a successful
            // migration. Log + continue (caller will surface as
            // CompletedWithWarning if needed).
            _logger.LogWarning(ex, "Failed to copy role assignments to placeholder. Continuing with inherited permissions.");
            return false;
        }
    }

    private static string GetParentFolder(string serverRelativeUrl)
    {
        var idx = serverRelativeUrl.LastIndexOf('/');
        if (idx <= 0)
        {
            throw new ArgumentException("Cannot derive parent folder from URL: " + serverRelativeUrl, nameof(serverRelativeUrl));
        }
        return serverRelativeUrl[..idx];
    }
}
