using Migration.Engine.Lifecycle;
using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Verifies common failure causes map to actionable, user-facing summaries and
/// that bare GUIDs / stack traces never leak into the "Last error" column (#5).
/// </summary>
public class FriendlyErrorMapperTests
{
    [Fact]
    public void CheckedOut_MapsToActionableMessage()
    {
        var friendly = FriendlyErrorMapper.ToFriendly("File is checked out and cannot be deleted.", MigrationLifecycleStatus.DeleteFailed);
        Assert.Contains("checked out", friendly, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("The remote server returned 429 Too Many Requests")]
    [InlineData("Request was throttled by SharePoint")]
    public void Throttling_MapsToTransientMessage(string raw)
    {
        var friendly = FriendlyErrorMapper.ToFriendly(raw, MigrationLifecycleStatus.CopyToColdStorageFailed);
        Assert.Contains("throttled", friendly, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Throttling_NonTerminal_PromisesAutomaticRetry()
    {
        var friendly = FriendlyErrorMapper.ToFriendly("429 Too Many Requests", MigrationLifecycleStatus.RetryScheduled);
        Assert.Contains("automatically", friendly, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Throttling_Terminal_DoesNotPromiseAutomaticRetry_AndSuggestsRequeue()
    {
        var friendly = FriendlyErrorMapper.ToFriendly("429 Too Many Requests", MigrationLifecycleStatus.CopyToColdStorageFailed);
        Assert.DoesNotContain("will be retried automatically", friendly, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-queue", friendly, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessDenied_MapsToPermissionMessage()
    {
        var friendly = FriendlyErrorMapper.ToFriendly("403 Forbidden: Access denied", MigrationLifecycleStatus.CopyToColdStorageFailed);
        Assert.Contains("Access was denied", friendly);
    }

    [Fact]
    public void IntegrityMismatch_MapsToIntegrityMessage()
    {
        var friendly = FriendlyErrorMapper.ToFriendly("Blob MD5 mismatch (expected x, got y)", MigrationLifecycleStatus.CopyToColdStorageFailed);
        Assert.Contains("integrity", friendly, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotFound_IsContextual_ForMigrateVsRestore()
    {
        var migrate = FriendlyErrorMapper.ToFriendly("404 not found", MigrationLifecycleStatus.CopyToColdStorageFailed);
        var restore = FriendlyErrorMapper.ToFriendly("404 not found", MigrationLifecycleStatus.RestoreFailed);
        Assert.Contains("SharePoint", migrate);
        Assert.Contains("cold storage", restore);
    }

    [Fact]
    public void UnmappedMessage_StripsGuids_AndIsShort()
    {
        const string raw = "Unexpected failure 3fa85f64-5717-4562-b3fc-2c963f66afa6 in component\n  at Foo.Bar()\n  at Baz()";
        var friendly = FriendlyErrorMapper.ToFriendly(raw, MigrationLifecycleStatus.ValidationFailed);

        Assert.DoesNotContain("3fa85f64-5717-4562-b3fc-2c963f66afa6", friendly);
        Assert.DoesNotContain("at Foo.Bar()", friendly); // first line only
        Assert.True(friendly.Length <= 220);
    }

    [Fact]
    public void Skipped_KeepsItsOwnReason()
    {
        var friendly = FriendlyErrorMapper.ToFriendly("Skipped: file type '.tmp' is excluded from archiving", MigrationLifecycleStatus.Skipped);
        Assert.Contains(".tmp", friendly);
    }

    [Fact]
    public void EmptyMessage_FailureStatus_StillProducesSummary()
    {
        var friendly = FriendlyErrorMapper.ToFriendly("", MigrationLifecycleStatus.CopyToColdStorageFailed);
        Assert.False(string.IsNullOrWhiteSpace(friendly));
    }
}
