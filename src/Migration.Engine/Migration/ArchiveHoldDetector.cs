using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;

namespace Migration.Engine.Migration;

/// <summary>
/// Result of a compliance-hold check for a single item.
/// </summary>
public sealed record HoldStatus(bool IsOnHold, string? Reason)
{
    public static readonly HoldStatus NotOnHold = new(false, null);
}

/// <summary>
/// Detects whether a SharePoint item is under a compliance hold so it can be
/// kept out of cold storage (issue #15). Takes a live <see cref="ClientContext"/>
/// because the only reliable per-item signal lives in SharePoint.
/// </summary>
public interface IArchiveHoldDetector
{
    Task<HoldStatus> CheckAsync(ClientContext ctx, string serverRelativeUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hold detector based on the item's Purview retention label, read from the
/// built-in <c>_ComplianceTag</c> field. Any non-empty label is treated as a
/// hold.
///
/// Honest scope note: this reliably detects retention/record LABELS. eDiscovery
/// or org-wide retention policies that place content on hold WITHOUT stamping a
/// per-item label are not exposed by this API and therefore are not detected
/// here — admins should additionally use the exclusion list (issue #7) to ring-
/// fence such areas.
///
/// Stance: fail CLOSED. If enabled and the label can't be read, the item is
/// treated as on-hold (skipped) — for a compliance feature it's safer to skip an
/// item we're unsure about than to risk archiving held content.
/// </summary>
public sealed class RetentionLabelHoldDetector(ILogger logger) : IArchiveHoldDetector
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<HoldStatus> CheckAsync(ClientContext ctx, string serverRelativeUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
            var item = file.ListItemAllFields;
            ctx.Load(item, i => i["_ComplianceTag"]);
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);

            var tag = item["_ComplianceTag"] as string;
            return ShouldTreatAsHold(tag)
                ? new HoldStatus(true, $"under retention label '{tag}'")
                : HoldStatus.NotOnHold;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not determine hold status for '{Url}'; treating as on-hold (fail-closed).", serverRelativeUrl);
            return new HoldStatus(true, "compliance hold status could not be determined (fail-closed)");
        }
    }

    /// <summary>Pure decision: a non-empty retention label means the item is held.</summary>
    public static bool ShouldTreatAsHold(string? complianceTag) => !string.IsNullOrWhiteSpace(complianceTag);
}
