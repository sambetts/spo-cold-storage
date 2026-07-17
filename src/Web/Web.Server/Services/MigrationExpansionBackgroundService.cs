using Entities;
using Microsoft.EntityFrameworkCore;

namespace Web.Services;

/// <summary>
/// Drains the <see cref="IMigrationSubmissionQueue"/> and runs each job's expansion in
/// its own DI scope, off the HTTP request thread. On startup it re-drives any job whose
/// submission was persisted but not yet expanded (e.g. the app restarted mid-submit), so
/// a large-folder submit is both responsive and durable.
/// </summary>
public sealed class MigrationExpansionBackgroundService(
    IMigrationSubmissionQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<MigrationExpansionBackgroundService> logger) : BackgroundService
{
    private readonly IMigrationSubmissionQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ILogger<MigrationExpansionBackgroundService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReDrivePendingAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var expander = scope.ServiceProvider.GetRequiredService<IMigrationExpander>();
                    // Not tied to the request token: a client disconnect must not abort expansion.
                    await expander.ExpandAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Background expansion failed for job {JobId}.", jobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ReDrivePendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SPOColdStorageDbContext>();
            var pending = await db.MigrationJobs
                .Where(j => j.SubmissionRequestJson != null && j.ExpansionCompletedAt == null)
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.JobId)
                .Take(500)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var id in pending)
            {
                _queue.Enqueue(id);
            }
            if (pending.Count > 0)
            {
                _logger.LogInformation("Re-drove {Count} pending migration submission(s) after startup.", pending.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup re-drive of pending migration submissions failed.");
        }
    }
}
