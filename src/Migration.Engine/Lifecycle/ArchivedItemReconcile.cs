using Models.ColdStorage;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Pure decisions for reconciling items whose own progress timestamps prove work
/// physically happened, independent of the (possibly stale) status column.
///
/// These guard the #1 invariant from the reporting side: once a source has been
/// deleted, the archival copy + post-copy validation are known to have succeeded
/// (the pipeline never deletes before that). So an item with a source-deleted
/// timestamp must NEVER be reported as <see cref="MigrationLifecycleStatus.CopyToColdStorageFailed"/>
/// ("copy failed, source untouched") — that message is both wrong and dangerous to
/// act on. It is either already complete (placeholder written) or merely missing its
/// placeholder.
/// </summary>
public static class ArchivedItemReconcile
{
    /// <summary>
    /// True when the item's timestamps show the file is already fully archived —
    /// content copied to blob, source deleted, AND the .url placeholder created —
    /// yet the row is not marked completed. This is a false failure left behind by an
    /// interrupted run (the final "-&gt; Completed" write was lost) or by a requeue
    /// that reset the status but not the progress. Such rows must be corrected to
    /// <see cref="MigrationLifecycleStatus.ColdStorageMigrationCompleted"/>, never re-failed.
    /// </summary>
    public static bool IsFullyArchivedButNotCompleted(
        MigrationLifecycleStatus status,
        DateTime? sourceDeletedAt,
        DateTime? placeholderCreatedAt)
        => sourceDeletedAt is not null
           && placeholderCreatedAt is not null
           && status != MigrationLifecycleStatus.ColdStorageMigrationCompleted;

    /// <summary>
    /// True once the source file has been deleted. Past this point the item is beyond
    /// the point of no return: the copy was validated, so it can never legitimately be
    /// labelled a copy failure.
    /// </summary>
    public static bool SourceIsGone(DateTime? sourceDeletedAt) => sourceDeletedAt is not null;

    /// <summary>
    /// The correct failure status for an item that a give-up / stall sweep is about to
    /// fail, honouring the timestamps: never "copy failed" once the source is gone.
    /// Returns <c>null</c> when the item should NOT be failed at all because it is
    /// already fully archived (it should be completed by
    /// <c>CompleteAlreadyArchivedAsync</c> instead).
    /// </summary>
    /// <param name="defaultFailure">
    /// The failure status to use when the source is still in SharePoint (the normal case),
    /// e.g. <see cref="MigrationLifecycleStatus.CopyToColdStorageFailed"/> for a give-up or the
    /// stall-mapped status.
    /// </param>
    public static MigrationLifecycleStatus? ResolveFailureStatus(
        DateTime? sourceDeletedAt,
        DateTime? placeholderCreatedAt,
        MigrationLifecycleStatus defaultFailure)
    {
        if (sourceDeletedAt is not null && placeholderCreatedAt is not null)
        {
            // Fully archived — must be completed, not failed.
            return null;
        }
        if (sourceDeletedAt is not null)
        {
            // Source gone but no placeholder: it needs a placeholder, not a copy retry.
            // PlaceholderFailed is accurate and requeueable (the resume path recreates the
            // placeholder from the existing blob).
            return MigrationLifecycleStatus.PlaceholderFailed;
        }
        return defaultFailure;
    }
}
