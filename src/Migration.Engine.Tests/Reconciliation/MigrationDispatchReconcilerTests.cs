using Entities.DBEntities.ColdStorage;
using Migration.Engine.Lifecycle;
using Migration.Engine.Reconciliation;
using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Reconciliation;

public class MigrationDispatchReconcilerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // grace 120s, give-up 1440 min (24h), stall 30 min
    private static readonly DispatchThresholds T = new(EnqueueGraceSeconds: 120, MaxQueuedMinutes: 1440, StallMinutes: 30);

    [Theory]
    // Terminal statuses are never touched.
    [InlineData(MigrationLifecycleStatus.ColdStorageMigrationCompleted, 0, 0, -1, DispatchAction.None)]
    [InlineData(MigrationLifecycleStatus.CopyToColdStorageFailed, 999999, 999999, -1, DispatchAction.None)]
    [InlineData(MigrationLifecycleStatus.Cancelled, 999999, 0, -1, DispatchAction.None)]
    // Queued, never sent, still within grace of being created -> leave alone (a publish may be in flight).
    [InlineData(MigrationLifecycleStatus.Queued, 30, 0, -1, DispatchAction.None)]
    // Queued, never sent, older than grace -> re-drive (the orphaned-item case that froze the job).
    [InlineData(MigrationLifecycleStatus.Queued, 200, 0, -1, DispatchAction.ReDrive)]
    // Queued, sent recently -> leave alone.
    [InlineData(MigrationLifecycleStatus.Queued, 300, 0, 30, DispatchAction.None)]
    // Queued, last sent longer ago than grace -> re-drive (message likely lost).
    [InlineData(MigrationLifecycleStatus.Queued, 600, 0, 200, DispatchAction.ReDrive)]
    // Queued past the give-up window -> fail (takes precedence over re-drive).
    [InlineData(MigrationLifecycleStatus.Queued, 90000, 0, 200, DispatchAction.FailGaveUp)]
    // Active, recently updated -> leave alone.
    [InlineData(MigrationLifecycleStatus.MigrationInProgress, 0, 5, -1, DispatchAction.None)]
    [InlineData(MigrationLifecycleStatus.PostCopyValidation, 0, 29, -1, DispatchAction.None)]
    // Active, no progress past the stall window -> fail (crashed/stalled worker).
    [InlineData(MigrationLifecycleStatus.MigrationInProgress, 0, 40, -1, DispatchAction.FailStalled)]
    [InlineData(MigrationLifecycleStatus.RestoreInProgress, 0, 60, -1, DispatchAction.FailStalled)]
    public void Decide_ReturnsExpectedAction(
        MigrationLifecycleStatus status,
        int createdAgeSeconds,
        int updatedAgeMinutes,
        int lastEnqueuedAgeSeconds,
        DispatchAction expected)
    {
        var createdAt = Now.AddSeconds(-createdAgeSeconds);
        var updatedAt = Now.AddMinutes(-updatedAgeMinutes);
        DateTime? lastEnqueuedAt = lastEnqueuedAgeSeconds < 0 ? null : Now.AddSeconds(-lastEnqueuedAgeSeconds);

        var action = MigrationDispatchReconciler.Decide(status, createdAt, updatedAt, lastEnqueuedAt, Now, T);

        Assert.Equal(expected, action);
    }

    [Fact]
    public void BuildEnvelopeFromItem_Migrate_ProducesValidEnvelope()
    {
        var job = new MigrationJob
        {
            JobId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Migrate,
            RequestedByUpn = "user@contoso.onmicrosoft.com",
        };
        var item = new MigrationJobItem
        {
            ItemId = Guid.NewGuid(),
            JobId = job.JobId,
            SpSiteUrl = "https://contoso.sharepoint.com/sites/MigrationHost",
            SpWebUrl = "https://contoso.sharepoint.com/sites/MigrationHost",
            SpServerRelativeUrl = "/sites/MigrationHost/Shared Documents/a.docx",
            BlobContainerName = "cold",
            FileSize = 1234,
            SourceLastModified = Now,
        };

        var envelope = ColdStorageBusMessageFactory.BuildEnvelopeFromItem(item, job);

        Assert.NotNull(envelope);
        Assert.True(envelope!.IsValid);
        Assert.Equal(MigrationOperationKind.Migrate, envelope.Operation);
        Assert.Equal(item.ItemId, envelope.ItemId);
        Assert.Equal(job.JobId, envelope.JobId);
        Assert.NotNull(envelope.File);
    }

    [Fact]
    public void BuildEnvelopeFromItem_Restore_ProducesValidEnvelope()
    {
        var job = new MigrationJob
        {
            JobId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Restore,
            RequestedByUpn = "user@contoso.onmicrosoft.com",
        };
        var item = new MigrationJobItem
        {
            ItemId = Guid.NewGuid(),
            JobId = job.JobId,
            SpSiteUrl = "https://contoso.sharepoint.com/sites/MigrationHost",
            SpWebUrl = "https://contoso.sharepoint.com/sites/MigrationHost",
            SpServerRelativeUrl = "/sites/MigrationHost/Shared Documents/a.docx",
            PlaceholderServerRelativeUrl = "/sites/MigrationHost/Shared Documents/a.docx.url",
            BlobContainerName = "cold",
        };

        var envelope = ColdStorageBusMessageFactory.BuildEnvelopeFromItem(item, job);

        Assert.NotNull(envelope);
        Assert.True(envelope!.IsValid);
        Assert.Equal(MigrationOperationKind.Restore, envelope.Operation);
        Assert.NotNull(envelope.RestoreTarget);
    }

    [Fact]
    public void BuildEnvelopeFromItem_MissingCoordinates_ReturnsNull()
    {
        var job = new MigrationJob { JobId = Guid.NewGuid(), Operation = MigrationOperationKind.Migrate };
        var item = new MigrationJobItem { ItemId = Guid.NewGuid(), JobId = job.JobId, BlobContainerName = "cold" };

        var envelope = ColdStorageBusMessageFactory.BuildEnvelopeFromItem(item, job);

        Assert.Null(envelope);
    }
}
