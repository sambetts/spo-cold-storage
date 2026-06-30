namespace Models.ColdStorage;

/// <summary>
/// Lifecycle status values shared between SPFx, the web UI and backend workers.
/// Mirrors the list in requirements.md so that every system reflects the same
/// current state for each item or batch.
/// </summary>
public enum MigrationLifecycleStatus
{
    Queued = 0,
    Validating = 10,
    ValidationFailed = 11,
    MigrationInProgress = 20,
    CopiedToColdStorage = 21,
    CopyToColdStorageFailed = 22,
    PostCopyValidation = 23,
    DeletePending = 24,
    DeleteFailed = 25,
    PlaceholderCreating = 26,
    PlaceholderFailed = 27,
    ColdStorageMigrationCompleted = 30,
    RestoreInProgress = 40,
    RestoredToSharePoint = 41,
    RestoreFailed = 42,
    PostRestoreValidation = 43,
    PlaceholderRemoving = 44,
    PlaceholderRemoveFailed = 45,
    RestoreCompleted = 50,
    CompletedWithWarning = 60,
    RetryScheduled = 70,
    Cancelled = 80,

    /// <summary>
    /// The item was deliberately not archived because it failed an eligibility
    /// rule (too small, excluded file type, excluded scope, under legal hold,
    /// still actively read, ...). Terminal and the source is always left intact.
    /// </summary>
    Skipped = 81,
}

public static class MigrationLifecycleStatusExtensions
{
    /// <summary>
    /// Terminal statuses where the job/item will not transition further without
    /// an explicit retry or re-queue.
    /// </summary>
    public static bool IsTerminal(this MigrationLifecycleStatus status) => status switch
    {
        MigrationLifecycleStatus.ColdStorageMigrationCompleted => true,
        MigrationLifecycleStatus.RestoreCompleted => true,
        MigrationLifecycleStatus.ValidationFailed => true,
        MigrationLifecycleStatus.CopyToColdStorageFailed => true,
        MigrationLifecycleStatus.DeleteFailed => true,
        MigrationLifecycleStatus.PlaceholderFailed => true,
        MigrationLifecycleStatus.RestoreFailed => true,
        MigrationLifecycleStatus.PlaceholderRemoveFailed => true,
        MigrationLifecycleStatus.CompletedWithWarning => true,
        MigrationLifecycleStatus.Cancelled => true,
        MigrationLifecycleStatus.Skipped => true,
        _ => false,
    };

    /// <summary>
    /// True for the statuses where a restore has actively claimed an item and is
    /// working on it (past the Queued stage, before a terminal state). Used by the
    /// concurrency guard to detect a second restore of the same placeholder
    /// (issue #10).
    /// </summary>
    public static bool IsActiveRestore(this MigrationLifecycleStatus status) => status switch
    {
        MigrationLifecycleStatus.RestoreInProgress => true,
        MigrationLifecycleStatus.RestoredToSharePoint => true,
        MigrationLifecycleStatus.PostRestoreValidation => true,
        MigrationLifecycleStatus.PlaceholderRemoving => true,
        _ => false,
    };

    /// <summary>
    /// True if the source SharePoint item has been (or should be) removed by this
    /// point in the lifecycle. Used as the guard so we never delete the source
    /// before a successful copy + post-copy validation.
    /// </summary>
    public static bool SourceDeleteAllowed(this MigrationLifecycleStatus status) => status switch
    {
        MigrationLifecycleStatus.DeletePending => true,
        MigrationLifecycleStatus.PlaceholderCreating => true,
        MigrationLifecycleStatus.PlaceholderFailed => true,
        MigrationLifecycleStatus.ColdStorageMigrationCompleted => true,
        _ => false,
    };
}
