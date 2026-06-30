using Entities;
using Entities.Configuration;
using Entities.DBEntities.ColdStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Models.ColdStorage;

namespace Web.Services;

/// <summary>
/// Delivers a pre-archive notice to the user (issue #17). The default
/// implementation records the notice (which the in-product notices view surfaces)
/// and logs it; an email/Teams channel can be added by replacing this service.
/// </summary>
public interface IPreArchiveNotifier
{
    Task NotifyAsync(PreArchiveNotice notice, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-product notifier: the persisted <c>pre_archive_notices</c> row IS the
/// notification surface, so this just logs. Swap for a Graph sendMail / Teams
/// implementation to add an out-of-product channel.
/// </summary>
public sealed class LoggingPreArchiveNotifier(ILogger<LoggingPreArchiveNotifier> logger) : IPreArchiveNotifier
{
    private readonly ILogger<LoggingPreArchiveNotifier> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task NotifyAsync(PreArchiveNotice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);
        _logger.LogInformation(
            "Pre-archive notice for '{Url}' addressed to {Upn}; grace until {GraceUntil:o}.",
            notice.ServerRelativeUrl, notice.NotifiedUpn ?? "(unknown)", notice.GraceUntil);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Drives the pre-archive grace-period workflow (issue #17). A future
/// auto-archive trigger calls <see cref="EvaluateAsync"/> for each candidate: the
/// first time it sends a notice and starts the grace window; later it reports
/// whether the window has elapsed so archiving may proceed. With the grace period
/// disabled it always returns Proceed.
/// </summary>
public sealed class PreArchiveNoticeService(
    SPOColdStorageDbContext db,
    Config config,
    IPreArchiveNotifier notifier)
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly IPreArchiveNotifier _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));

    public async Task<PreArchiveDecision> EvaluateAsync(
        string siteUrl,
        string serverRelativeUrl,
        string? ownerUpn,
        CancellationToken cancellationToken = default)
    {
        var graceHours = _config.ColdStoragePreArchiveGraceHours;
        if (graceHours <= 0)
        {
            return PreArchiveDecision.Proceed;
        }

        var existing = await _db.PreArchiveNotices
            .Where(n => n.ServerRelativeUrl == serverRelativeUrl && n.Status == PreArchiveNoticeStatus.Pending)
            .OrderByDescending(n => n.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var decision = PreArchiveGate.Decide(existing?.GraceUntil, now, graceHours);

        switch (decision)
        {
            case PreArchiveDecision.SendNotice:
                var notice = new PreArchiveNotice
                {
                    SiteUrl = siteUrl,
                    ServerRelativeUrl = serverRelativeUrl,
                    NotifiedUpn = ownerUpn,
                    NotifiedAt = now,
                    GraceUntil = now.AddHours(graceHours),
                    Status = PreArchiveNoticeStatus.Pending,
                    CreatedAt = now,
                };
                _db.PreArchiveNotices.Add(notice);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await _notifier.NotifyAsync(notice, cancellationToken).ConfigureAwait(false);
                break;

            case PreArchiveDecision.Proceed when existing is not null:
                existing.Status = PreArchiveNoticeStatus.Proceeded;
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        return decision;
    }
}
