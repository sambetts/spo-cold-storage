using Entities.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Migration.Engine.Reconciliation;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>POST /api/admin/reconcile</c> – admin-triggered, on-demand orphan
/// reconciliation pass (issue #3). The same pass also runs on a schedule in the
/// migrator worker when ColdStorageReconcileIntervalHours &gt; 0. Runs
/// synchronously and returns the summary; bounded per run.
/// </summary>
[Authorize]
[ApiController]
[Route("api/admin")]
public class ReconcileController(
    Config config,
    IColdStorageAdminAuthorizationService admin,
    ILogger<ReconcileController> logger) : ControllerBase
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    private readonly ILogger<ReconcileController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [HttpPost("reconcile")]
    public async Task<ActionResult<ReconcileSummaryResponse>> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        var reconciler = new ColdStorageReconciler(_config, _logger);
        var summary = await reconciler.RunAsync(cancellationToken).ConfigureAwait(false);

        return new ReconcileSummaryResponse
        {
            Checked = summary.Checked,
            Orphans = summary.Orphans,
            BlobsDeleted = summary.BlobsDeleted,
            Quarantined = summary.Quarantined,
            Errors = summary.Errors,
            Policy = ColdStorageReconciler.ParsePolicy(_config.ColdStorageOrphanPolicy).ToString(),
        };
    }
}
