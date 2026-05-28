using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Locks the lifecycle contract from requirements.md. These rules underpin the
/// "source must never be deleted on failure" guarantee.
/// </summary>
public class MigrationLifecycleStatusTests
{
    [Theory]
    [InlineData(MigrationLifecycleStatus.ColdStorageMigrationCompleted, true)]
    [InlineData(MigrationLifecycleStatus.RestoreCompleted, true)]
    [InlineData(MigrationLifecycleStatus.CopyToColdStorageFailed, true)]
    [InlineData(MigrationLifecycleStatus.ValidationFailed, true)]
    [InlineData(MigrationLifecycleStatus.DeleteFailed, true)]
    [InlineData(MigrationLifecycleStatus.PlaceholderFailed, true)]
    [InlineData(MigrationLifecycleStatus.RestoreFailed, true)]
    [InlineData(MigrationLifecycleStatus.PlaceholderRemoveFailed, true)]
    [InlineData(MigrationLifecycleStatus.Cancelled, true)]
    [InlineData(MigrationLifecycleStatus.CompletedWithWarning, true)]
    [InlineData(MigrationLifecycleStatus.Queued, false)]
    [InlineData(MigrationLifecycleStatus.Validating, false)]
    [InlineData(MigrationLifecycleStatus.MigrationInProgress, false)]
    [InlineData(MigrationLifecycleStatus.CopiedToColdStorage, false)]
    [InlineData(MigrationLifecycleStatus.PostCopyValidation, false)]
    [InlineData(MigrationLifecycleStatus.DeletePending, false)]
    [InlineData(MigrationLifecycleStatus.PlaceholderCreating, false)]
    [InlineData(MigrationLifecycleStatus.RestoreInProgress, false)]
    [InlineData(MigrationLifecycleStatus.PostRestoreValidation, false)]
    [InlineData(MigrationLifecycleStatus.PlaceholderRemoving, false)]
    [InlineData(MigrationLifecycleStatus.RestoredToSharePoint, false)]
    [InlineData(MigrationLifecycleStatus.RetryScheduled, false)]
    public void IsTerminal_ReturnsExpected(MigrationLifecycleStatus status, bool expected)
        => Assert.Equal(expected, status.IsTerminal());

    [Theory]
    [InlineData(MigrationLifecycleStatus.DeletePending, true)]
    [InlineData(MigrationLifecycleStatus.PlaceholderCreating, true)]
    [InlineData(MigrationLifecycleStatus.PlaceholderFailed, true)]
    [InlineData(MigrationLifecycleStatus.ColdStorageMigrationCompleted, true)]
    [InlineData(MigrationLifecycleStatus.Queued, false)]
    [InlineData(MigrationLifecycleStatus.Validating, false)]
    [InlineData(MigrationLifecycleStatus.MigrationInProgress, false)]
    [InlineData(MigrationLifecycleStatus.CopiedToColdStorage, false)]
    [InlineData(MigrationLifecycleStatus.PostCopyValidation, false)]
    [InlineData(MigrationLifecycleStatus.CopyToColdStorageFailed, false)]
    [InlineData(MigrationLifecycleStatus.ValidationFailed, false)]
    public void SourceDeleteAllowed_OnlyAfterCopySuccessVerified(MigrationLifecycleStatus status, bool expected)
        => Assert.Equal(expected, status.SourceDeleteAllowed());

    /// <summary>
    /// Hard guarantee from requirements.md: if any step before delete failed
    /// (copy/upload/validation), SourceDeleteAllowed must be false.
    /// </summary>
    [Fact]
    public void SourceDelete_NeverAllowed_FromAnyFailureState()
    {
        var failureStates = new[]
        {
            MigrationLifecycleStatus.ValidationFailed,
            MigrationLifecycleStatus.CopyToColdStorageFailed,
        };
        foreach (var status in failureStates)
        {
            Assert.False(status.SourceDeleteAllowed(),
                $"Source delete must never be allowed from '{status}'.");
        }
    }
}

