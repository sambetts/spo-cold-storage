using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using Migration.Engine.Utils;
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
    /// <param name="userFacingUrl">
    /// Optional value for the <c>[InternetShortcut].URL</c> field inside the
    /// generated .url file. When set (typically a SPA download route), end
    /// users who double-click the placeholder are sent there for AAD auth +
    /// ACL check + redirect to a short-lived SAS, instead of trying to hit
    /// the raw blob URL (which fails when public network access is locked
    /// down). When null/empty, the metadata's BlobUrl is used (legacy
    /// behaviour, fine for dev).
    /// </param>
    public async Task<string> WritePlaceholderAsync(
        ClientContext ctx,
        string originalServerRelativeUrl,
        PlaceholderFileMetadata metadata,
        CancellationToken cancellationToken = default,
        string? userFacingUrl = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(metadata);

        cancellationToken.ThrowIfCancellationRequested();

        var placeholderUrl = BuildPlaceholderServerRelativeUrl(originalServerRelativeUrl);
        var content = metadata.BuildUrlFileContent(userFacingUrl);
        var bytes = Encoding.UTF8.GetBytes(content);

        // Resolve the folder for the source file then upload the .url next to it.
        var folderServerRelativeUrl = GetParentFolder(originalServerRelativeUrl);
        var folder = ctx.Web.GetFolderByServerRelativeUrl(folderServerRelativeUrl);
        ctx.Load(folder);
        await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

        using var ms = new MemoryStream(bytes);
        var fileInfo = new FileCreationInformation
        {
            ContentStream = ms,
            Url = Path.GetFileName(placeholderUrl),
            Overwrite = true,
        };

        var addedFile = folder.Files.Add(fileInfo);
        ctx.Load(addedFile, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

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

    /// <summary>
    /// Internal names of the read-only-ish columns stamped onto the placeholder
    /// list item so the original authorship/edit trail stays visible in the
    /// library after archiving (issue #1).
    /// </summary>
    private const string FieldOriginalAuthor = "ColdStorageOriginalAuthor";
    private const string FieldOriginalEditor = "ColdStorageOriginalEditor";
    private const string FieldOriginalModified = "ColdStorageOriginalModified";
    private const string FieldOriginalCreated = "ColdStorageOriginalCreated";

    /// <summary>
    /// Ensures the cold-storage "original metadata" columns exist on the
    /// placeholder's library and stamps the captured author/editor/timestamps
    /// onto the placeholder list item. Best-effort: a failure here is logged and
    /// swallowed so it never undoes an otherwise-successful migration (mirrors
    /// <see cref="CopyRoleAssignmentsAsync"/>).
    /// </summary>
    public async Task<bool> StampOriginalMetadataAsync(
        ClientContext ctx,
        string placeholderServerRelativeUrl,
        PlaceholderFileMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var phFile = ctx.Web.GetFileByServerRelativeUrl(placeholderServerRelativeUrl);
            var item = phFile.ListItemAllFields;
            ctx.Load(item, i => i.ParentList);
            var list = item.ParentList;
            ctx.Load(list, l => l.Fields.Include(f => f.InternalName));
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);

            var existing = new HashSet<string>(
                list.Fields.Select(f => f.InternalName), StringComparer.OrdinalIgnoreCase);

            EnsureTextField(list, existing, FieldOriginalAuthor, "Original Author");
            EnsureTextField(list, existing, FieldOriginalEditor, "Original Editor");
            EnsureDateField(list, existing, FieldOriginalModified, "Original Modified");
            EnsureDateField(list, existing, FieldOriginalCreated, "Original Created");
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);

            SetIfPresent(item, FieldOriginalAuthor, metadata.OriginalCreatedBy);
            SetIfPresent(item, FieldOriginalEditor, metadata.OriginalModifiedBy);
            if (metadata.OriginalLastModified > DateTime.MinValue)
            {
                item[FieldOriginalModified] = metadata.OriginalLastModified;
            }
            if (metadata.OriginalCreated > DateTime.MinValue)
            {
                item[FieldOriginalCreated] = metadata.OriginalCreated;
            }
            item.Update();
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to stamp original-metadata columns onto placeholder '{Url}'. Continuing.", placeholderServerRelativeUrl);
            return false;
        }
    }

    private static void EnsureTextField(List list, HashSet<string> existing, string internalName, string displayName)
    {
        if (existing.Contains(internalName))
        {
            return;
        }
        var xml = $"<Field Type='Text' DisplayName='{displayName}' Name='{internalName}' StaticName='{internalName}' Group='Cold Storage' />";
        list.Fields.AddFieldAsXml(xml, true, AddFieldOptions.AddFieldInternalNameHint);
    }

    private static void EnsureDateField(List list, HashSet<string> existing, string internalName, string displayName)
    {
        if (existing.Contains(internalName))
        {
            return;
        }
        var xml = $"<Field Type='DateTime' Format='DateTime' DisplayName='{displayName}' Name='{internalName}' StaticName='{internalName}' Group='Cold Storage' />";
        list.Fields.AddFieldAsXml(xml, true, AddFieldOptions.AddFieldInternalNameHint);
    }

    private static void SetIfPresent(ListItem item, string internalName, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            item[internalName] = value;
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
