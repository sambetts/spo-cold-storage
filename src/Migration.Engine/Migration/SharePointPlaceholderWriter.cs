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
    /// Internal name of the cold-storage status column stamped onto every
    /// placeholder so archived files are visually identifiable in the library
    /// view (issue #32). Unlike the "Original *" columns above this one is
    /// <b>not</b> opt-in: the badge is the only at-a-glance signal a user gets
    /// that a ".url" file is an archived document.
    /// </summary>
    public const string FieldColdStorageStatus = "ColdStorageStatus";

    /// <summary>
    /// Component id of the SPFx <c>ColdStorageStatusFieldCustomizer</c> (see
    /// <c>src/SPFx/spfx-cold-storage/src/extensions/coldStorageStatusField/ColdStorageStatusFieldCustomizer.manifest.json</c>).
    /// Binding it to the <i>list</i> column is what makes SharePoint render the
    /// coloured badge instead of raw text — a site column alone renders nothing
    /// because it is never added to a library or a view.
    /// </summary>
    public const string StatusFieldCustomizerId = "bcc81765-0e17-4bd7-a1a5-68a72cb5a016";

    /// <summary>
    /// Schema XML for the status column. Kept as a helper so the customizer
    /// binding is covered by a unit test.
    /// </summary>
    public static string BuildStatusFieldXml() =>
        $"<Field Type='Text' DisplayName='Cold storage' Name='{FieldColdStorageStatus}' " +
        $"StaticName='{FieldColdStorageStatus}' Group='Cold Storage' MaxLength='64' Required='FALSE' " +
        $"ClientSideComponentId='{StatusFieldCustomizerId}' />";

    /// <summary>
    /// Document libraries already known to carry the cold-storage columns, keyed by
    /// <c>{sharepoint host}|{library root folder server-relative URL}</c> (value =
    /// whether the optional "Original *" columns were provisioned too). Static because
    /// the pipeline builds a writer per message, so an instance-level cache would never hit.
    /// <para>
    /// The host is part of the key because server-relative URLs are <b>not</b> globally
    /// unique — two host-named site collections both have a <c>/Shared Documents</c> — and
    /// a collision would make one site skip provisioning and silently lose every badge
    /// (the same reasoning as <see cref="ColdStorageBlobKey"/> prefixing the host).
    /// </para>
    /// <para>
    /// This exists to keep the badge cheap: without it every single file would pay an
    /// extra CSOM round trip to re-discover columns that were created by the first
    /// file in that library, which matters on large jobs where SharePoint throttling
    /// is the bottleneck.
    /// </para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ProvisionedLibraries =
        new(StringComparer.OrdinalIgnoreCase);

    private static string HostOf(ClientContext ctx)
        => Uri.TryCreate(ctx.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    private static string LibraryCacheKey(ClientContext ctx, string libraryRootServerRelativeUrl)
        => $"{HostOf(ctx)}|{libraryRootServerRelativeUrl}";

    private static string? FindProvisionedLibrary(
        ClientContext ctx, string placeholderServerRelativeUrl, bool needOriginalMetadataColumns)
    {
        var host = HostOf(ctx);
        foreach (var entry in ProvisionedLibraries)
        {
            if (needOriginalMetadataColumns && !entry.Value)
            {
                continue;
            }
            var separator = entry.Key.IndexOf('|');
            if (separator < 0
                || !entry.Key.AsSpan(0, separator).Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (IsUnder(placeholderServerRelativeUrl, entry.Key[(separator + 1)..]))
            {
                return entry.Key;
            }
        }
        return null;
    }

    private static bool IsUnder(string path, string libraryRoot) =>
        path.Length > libraryRoot.Length
        && path[libraryRoot.Length] == '/'
        && path.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures the cold-storage columns exist on the placeholder's library and
    /// stamps their values onto the placeholder list item:
    /// <list type="bullet">
    ///   <item>the status badge column, always (issue #32); and</item>
    ///   <item>the captured author/editor/timestamps, when
    ///   <paramref name="copyOriginalMetadataColumns"/> is set (issue #1).</item>
    /// </list>
    /// Best-effort: a failure here is logged and swallowed so it never undoes an
    /// otherwise-successful migration (mirrors <see cref="CopyRoleAssignmentsAsync"/>).
    /// </summary>
    public async Task<bool> StampPlaceholderColumnsAsync(
        ClientContext ctx,
        string placeholderServerRelativeUrl,
        PlaceholderFileMetadata metadata,
        bool copyOriginalMetadataColumns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();

        var cachedLibrary = FindProvisionedLibrary(ctx, placeholderServerRelativeUrl, copyOriginalMetadataColumns);
        try
        {
            var phFile = ctx.Web.GetFileByServerRelativeUrl(placeholderServerRelativeUrl);
            var item = phFile.ListItemAllFields;

            var stampStatus = true;
            var stampOriginal = copyOriginalMetadataColumns;

            if (cachedLibrary is null)
            {
                ctx.Load(item, i => i.ParentList);
                var list = item.ParentList;
                ctx.Load(list, l => l.Fields.Include(f => f.InternalName), l => l.RootFolder.ServerRelativeUrl);
                await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

                var existing = new HashSet<string>(
                    list.Fields.Select(f => f.InternalName), StringComparer.OrdinalIgnoreCase);

                // Each group is provisioned in its own round trip and REPORTS success
                // instead of throwing: the worker scales out, so several files from a
                // fresh library race to create the same column and all but one get a
                // duplicate-name error. Losing that race must not cost us the values we
                // can still write — the column exists either way.
                stampStatus = await EnsureStatusFieldAsync(ctx, list, existing, cancellationToken).ConfigureAwait(false);
                if (copyOriginalMetadataColumns)
                {
                    stampOriginal = await EnsureOriginalMetadataFieldsAsync(ctx, list, existing, cancellationToken).ConfigureAwait(false);
                }

                var root = list.RootFolder.ServerRelativeUrl;
                if (stampStatus && !string.IsNullOrEmpty(root))
                {
                    cachedLibrary = LibraryCacheKey(ctx, root);
                    ProvisionedLibraries.AddOrUpdate(cachedLibrary, stampOriginal, (_, had) => had || stampOriginal);
                }
            }

            if (!stampStatus && !stampOriginal)
            {
                return false;
            }

            // The placeholder only ever exists once the copy is verified and the
            // source removed, so the archived state is the terminal migrate status.
            if (stampStatus)
            {
                item[FieldColdStorageStatus] = nameof(MigrationLifecycleStatus.ColdStorageMigrationCompleted);
            }

            if (stampOriginal)
            {
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
            }
            item.Update();
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Drop the cache entry so the next file re-discovers the columns rather
            // than repeating a doomed update (e.g. someone deleted the column).
            if (cachedLibrary is not null)
            {
                ProvisionedLibraries.TryRemove(cachedLibrary, out _);
            }
            _logger.LogWarning(ex, "Failed to stamp cold-storage columns onto placeholder '{Url}'. Continuing.", placeholderServerRelativeUrl);
            return false;
        }
    }

    /// <summary>
    /// Adds the status column to the library (and to its default view) bound to the
    /// SPFx field customizer, so archived rows render the badge without any manual
    /// <c>Set-PnPField</c> step. Returns true when the column is present afterwards.
    /// Never throws — a provisioning failure only costs the badge, never the migration.
    /// </summary>
    private async Task<bool> EnsureStatusFieldAsync(
        ClientContext ctx, List list, HashSet<string> existing, CancellationToken cancellationToken)
    {
        if (existing.Contains(FieldColdStorageStatus))
        {
            return true;
        }

        try
        {
            var field = list.Fields.AddFieldAsXml(BuildStatusFieldXml(), true, AddFieldOptions.AddFieldInternalNameHint);
            // Belt and braces: not every SPO build honours ClientSideComponentId in
            // the field schema, and without it the customizer never renders.
            field.ClientSideComponentId = new Guid(StatusFieldCustomizerId);
            field.Update();
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            existing.Add(FieldColdStorageStatus);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not create the '{Field}' list column; checking whether it exists already.", FieldColdStorageStatus);
        }

        // Most likely cause: another worker archiving into the same fresh library won
        // the race and SharePoint rejected ours as a duplicate name. The column exists,
        // so this is a success, not a failure.
        if (await ListHasFieldsAsync(ctx, list, existing, [FieldColdStorageStatus], cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // Otherwise fall back to the site column provisioned by the SPFx feature's
        // elements.xml (it already carries the customizer binding).
        try
        {
            var siteField = ctx.Web.AvailableFields.GetByInternalNameOrTitle(FieldColdStorageStatus);
            list.Fields.Add(siteField);
            var view = list.DefaultView;
            view.ViewFields.Add(FieldColdStorageStatus);
            view.Update();
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            existing.Add(FieldColdStorageStatus);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not provision the '{Field}' column on the library; the cold-storage badge will not show for this item.", FieldColdStorageStatus);
            return false;
        }
    }

    /// <summary>
    /// Adds the opt-in "Original *" columns. Same contract as
    /// <see cref="EnsureStatusFieldAsync"/>: returns whether they are present, never throws.
    /// </summary>
    private async Task<bool> EnsureOriginalMetadataFieldsAsync(
        ClientContext ctx, List list, HashSet<string> existing, CancellationToken cancellationToken)
    {
        string[] all = [FieldOriginalAuthor, FieldOriginalEditor, FieldOriginalModified, FieldOriginalCreated];
        if (all.All(existing.Contains))
        {
            return true;
        }

        try
        {
            EnsureTextField(list, existing, FieldOriginalAuthor, "Original Author");
            EnsureTextField(list, existing, FieldOriginalEditor, "Original Editor");
            EnsureDateField(list, existing, FieldOriginalModified, "Original Modified");
            EnsureDateField(list, existing, FieldOriginalCreated, "Original Created");
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            foreach (var name in all)
            {
                existing.Add(name);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not create the 'Original *' columns; checking whether they exist already.");
        }

        return await ListHasFieldsAsync(ctx, list, existing, all, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads the list's fields and reports whether all of <paramref name="internalNames"/>
    /// are now present, refreshing <paramref name="existing"/>. Used to tell "someone else
    /// created it" apart from a real provisioning failure.
    /// </summary>
    private async Task<bool> ListHasFieldsAsync(
        ClientContext ctx, List list, HashSet<string> existing, string[] internalNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ctx.Load(list, l => l.Fields.Include(f => f.InternalName));
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            foreach (var name in list.Fields.Select(f => f.InternalName))
            {
                existing.Add(name);
            }
            return internalNames.All(existing.Contains);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not re-read the library's columns.");
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
