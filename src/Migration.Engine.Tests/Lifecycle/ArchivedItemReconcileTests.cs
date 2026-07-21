using Migration.Engine.Lifecycle;
using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

public class ArchivedItemReconcileTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsFullyArchivedButNotCompleted_True_WhenCopiedDeletedAndPlaceholdered_ButFailed()
    {
        // The exact shape of the 592 false-failures: source deleted + placeholder created,
        // yet marked CopyToColdStorageFailed.
        Assert.True(ArchivedItemReconcile.IsFullyArchivedButNotCompleted(
            MigrationLifecycleStatus.CopyToColdStorageFailed, sourceDeletedAt: T, placeholderCreatedAt: T));
    }

    [Fact]
    public void IsFullyArchivedButNotCompleted_True_WhenRequeuedBackToQueued()
    {
        Assert.True(ArchivedItemReconcile.IsFullyArchivedButNotCompleted(
            MigrationLifecycleStatus.Queued, sourceDeletedAt: T, placeholderCreatedAt: T));
    }

    [Fact]
    public void IsFullyArchivedButNotCompleted_False_WhenAlreadyCompleted()
    {
        // Never re-touch a correctly-completed item.
        Assert.False(ArchivedItemReconcile.IsFullyArchivedButNotCompleted(
            MigrationLifecycleStatus.ColdStorageMigrationCompleted, sourceDeletedAt: T, placeholderCreatedAt: T));
    }

    [Fact]
    public void IsFullyArchivedButNotCompleted_False_WhenNoPlaceholder()
    {
        // Source gone but no placeholder = not fully archived; must not be auto-completed.
        Assert.False(ArchivedItemReconcile.IsFullyArchivedButNotCompleted(
            MigrationLifecycleStatus.CopyToColdStorageFailed, sourceDeletedAt: T, placeholderCreatedAt: null));
    }

    [Fact]
    public void IsFullyArchivedButNotCompleted_False_WhenSourceStillPresent()
    {
        // Copied + placeholder but source NOT deleted is not a valid archived state to force-complete.
        Assert.False(ArchivedItemReconcile.IsFullyArchivedButNotCompleted(
            MigrationLifecycleStatus.CopyToColdStorageFailed, sourceDeletedAt: null, placeholderCreatedAt: T));
    }

    [Fact]
    public void ResolveFailureStatus_ReturnsNull_WhenFullyArchived()
    {
        // Fully archived => must NOT be failed at all (null = complete it instead).
        Assert.Null(ArchivedItemReconcile.ResolveFailureStatus(
            sourceDeletedAt: T, placeholderCreatedAt: T,
            defaultFailure: MigrationLifecycleStatus.CopyToColdStorageFailed));
    }

    [Fact]
    public void ResolveFailureStatus_ReturnsPlaceholderFailed_WhenSourceGoneButNoPlaceholder()
    {
        // Source gone, no placeholder => needs a placeholder, never "copy failed / source untouched".
        Assert.Equal(MigrationLifecycleStatus.PlaceholderFailed, ArchivedItemReconcile.ResolveFailureStatus(
            sourceDeletedAt: T, placeholderCreatedAt: null,
            defaultFailure: MigrationLifecycleStatus.CopyToColdStorageFailed));
    }

    [Fact]
    public void ResolveFailureStatus_ReturnsDefault_WhenSourceStillInSharePoint()
    {
        // The normal case: source intact => the caller's default failure applies (copy failed etc.).
        Assert.Equal(MigrationLifecycleStatus.CopyToColdStorageFailed, ArchivedItemReconcile.ResolveFailureStatus(
            sourceDeletedAt: null, placeholderCreatedAt: null,
            defaultFailure: MigrationLifecycleStatus.CopyToColdStorageFailed));
    }

    [Fact]
    public void ResolveFailureStatus_HonoursDefault_ForStallMapping()
    {
        // Stall path passes a pre-mapped default (e.g. PlaceholderFailed); source intact keeps it.
        Assert.Equal(MigrationLifecycleStatus.PlaceholderFailed, ArchivedItemReconcile.ResolveFailureStatus(
            sourceDeletedAt: null, placeholderCreatedAt: null,
            defaultFailure: MigrationLifecycleStatus.PlaceholderFailed));
    }

    [Fact]
    public void SourceIsGone_TracksDeletionTimestamp()
    {
        Assert.True(ArchivedItemReconcile.SourceIsGone(T));
        Assert.False(ArchivedItemReconcile.SourceIsGone(null));
    }
}
