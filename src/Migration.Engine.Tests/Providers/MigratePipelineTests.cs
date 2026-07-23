using Microsoft.Extensions.Logging.Abstractions;
using Migration.Engine.Migration;
using Migration.Engine.Providers;
using Migration.Engine.Tests.Providers.InMemory;
using Models.ColdStorage;
using System.Text;
using Xunit;

namespace Migration.Engine.Tests.Providers;

/// <summary>
/// Exhaustive tests of the provider-neutral <see cref="MigratePipeline"/> driven entirely by
/// in-memory adaptors: the happy path, the #1 invariant (never delete the source without a verified
/// copy), throttling/retry parking + exhaustion, conflict-by-date, delete-safety, already-gone,
/// eligibility/hold skips, and resume — no SharePoint or Azure required.
/// </summary>
public class MigratePipelineTests
{
    private static readonly TransferPipelineOptions Opts = new() { MaxProcessAttempts = 3, ThrottleBackoffBaseSeconds = 1, ThrottleBackoffMaxSeconds = 4 };
    private static readonly DateTime BaseTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Path = "/sites/x/Shared Documents/a.docx";
    private static readonly ColdStorageKey Key = new("cold", "site|a.docx");

    private sealed record Harness(MigratePipeline Pipeline, InMemorySourceStore Source, InMemoryColdStore Cold, InMemoryJobStatusWriter Writer, Guid ItemId, Guid JobId);

    private static Harness Build(IArchiveEligibilityEvaluator? eligibility = null)
    {
        var source = new InMemorySourceStore();
        var cold = new InMemoryColdStore();
        var writer = new InMemoryJobStatusWriter();
        var pipeline = new MigratePipeline(Opts, NullLogger.Instance, writer, source, cold, eligibility ?? new FakeEligibility());
        var jobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        writer.Seed(itemId, jobId);
        return new Harness(pipeline, source, cold, writer, itemId, jobId);
    }

    private static MigrateRequest Req(Harness h, DateTime? lastModifiedUtc = null, bool copyMetadataColumns = false) => new()
    {
        JobId = h.JobId,
        ItemId = h.ItemId,
        Source = new SourceItemRef("https://site", "https://site/web", Path),
        Cold = Key,
        SourceLastModifiedUtc = lastModifiedUtc ?? BaseTime,
        SourceSizeHint = 5,
        RequestedByUpn = "user@contoso.com",
        CopyMetadataColumns = copyMetadataColumns,
    };

    private static byte[] Bytes(string s = "hello") => Encoding.UTF8.GetBytes(s);

    // ---- Happy path -------------------------------------------------------

    [Fact]
    public async Task HappyPath_CopiesVerifiesDeletesSourceAndWritesPlaceholder()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        var item = h.Writer.Get(h.ItemId);
        Assert.Equal(MigrationLifecycleStatus.ColdStorageMigrationCompleted, item.Status);
        Assert.True(h.Cold.Contains(Key));                 // archived
        Assert.False(h.Source.Exists(Path));               // source deleted
        Assert.True(h.Source.HasPointer(Path + ".url"));   // placeholder written
        Assert.NotNull(item.CopiedAt);
        Assert.NotNull(item.SourceDeletedAt);
        Assert.NotNull(item.PlaceholderCreatedAt);
    }

    // ---- Placeholder metadata columns are opt-in -------------------------

    [Fact]
    public async Task Migrate_ByDefault_WritesPlaceholderWithoutStampingMetadataColumns()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.True(h.Source.HasPointer(Path + ".url"));    // placeholder is written...
        Assert.False(h.Source.LastPointerStampedColumns);   // ...but the "Original *" columns are not
    }

    [Fact]
    public async Task Migrate_WhenCopyMetadataColumnsRequested_StampsMetadataColumns()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);

        var ok = await h.Pipeline.ProcessAsync(Req(h, copyMetadataColumns: true), CancellationToken.None);

        Assert.True(ok);
        Assert.True(h.Source.LastPointerStampedColumns);
    }

    // ---- #1 invariant: source is never deleted on a copy/verify failure ---

    [Fact]
    public async Task TransientCopyFailure_ParksForRetry_SourceIntact_NothingArchived()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);
        h.Cold.Faults.Throttle("Write", retryAfterSeconds: 7);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        var item = h.Writer.Get(h.ItemId);
        Assert.Equal(MigrationLifecycleStatus.RetryScheduled, item.Status);
        Assert.NotNull(item.NextRetryAt);
        Assert.Equal(7, item.LastRetryAfterSeconds);       // honoured the server Retry-After
        Assert.True(h.Source.Exists(Path));                // INVARIANT: source kept
        Assert.False(h.Cold.Contains(Key));
    }

    [Fact]
    public async Task PermanentCopyFailure_Terminal_SourceIntact()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);
        h.Cold.Faults.Permanent("Write");

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(MigrationLifecycleStatus.CopyToColdStorageFailed, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.Exists(Path));                // INVARIANT: source kept
    }

    [Fact]
    public async Task PostCopyValidationFailure_Terminal_SourceIntact()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);
        h.Cold.Faults.Permanent("Verify");

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(MigrationLifecycleStatus.CopyToColdStorageFailed, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.Exists(Path));                // INVARIANT: source kept even though the blob was written
    }

    // ---- Throttling: retry + exhaustion -----------------------------------

    [Fact]
    public async Task Throttle_ThenRetrySucceeds()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);
        h.Cold.Faults.Throttle("Write");                   // fails once

        Assert.False(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None));
        Assert.Equal(MigrationLifecycleStatus.RetryScheduled, h.Writer.Get(h.ItemId).Status);

        // Retry: the fault is consumed, so the second attempt archives + completes.
        Assert.True(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None));
        Assert.Equal(MigrationLifecycleStatus.ColdStorageMigrationCompleted, h.Writer.Get(h.ItemId).Status);
        Assert.False(h.Source.Exists(Path));
    }

    [Fact]
    public async Task Throttle_ExhaustsAttempts_ThenTerminal()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);
        h.Cold.Faults.Throttle("Write", times: 5);         // always throttles

        Assert.False(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None)); // attempt 1 -> park
        Assert.False(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None)); // attempt 2 -> park
        Assert.False(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None)); // attempt 3 -> give up

        Assert.Equal(MigrationLifecycleStatus.CopyToColdStorageFailed, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.Exists(Path));                // INVARIANT held throughout
    }

    // ---- Conflict-by-date -------------------------------------------------

    [Fact]
    public async Task ConflictByDate_SameVersion_SkipsCopy_StillPlaceholders()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);
        h.Cold.Seed(Key, Bytes(), sourceLastModifiedUtc: BaseTime);  // same version already archived
        h.Cold.Faults.Permanent("Write");                            // a copy would fail — proves it's skipped

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MigrationLifecycleStatus.ColdStorageMigrationCompleted, h.Writer.Get(h.ItemId).Status);
        Assert.False(h.Source.Exists(Path));
        Assert.True(h.Source.HasPointer(Path + ".url"));
    }

    [Fact]
    public async Task ConflictByDate_ArchiveNewer_SkipsCopy_StillPlaceholders()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);
        h.Cold.Seed(Key, Bytes(), sourceLastModifiedUtc: BaseTime.AddDays(1)); // archive newer than source
        h.Cold.Faults.Permanent("Write");                                      // proves the copy is skipped

        Assert.True(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None));
        Assert.Equal(MigrationLifecycleStatus.ColdStorageMigrationCompleted, h.Writer.Get(h.ItemId).Status);
        Assert.False(h.Source.Exists(Path));
    }

    [Fact]
    public async Task ConflictByDate_ArchiveOlder_Overwrites()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes("new-content"), BaseTime);
        h.Cold.Seed(Key, Bytes("stale"), sourceLastModifiedUtc: BaseTime.AddDays(-1)); // archive older

        Assert.True(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None));
        Assert.Equal("new-content", Encoding.UTF8.GetString(h.Cold.BytesAt(Key)!)); // overwritten with source bytes
        Assert.False(h.Source.Exists(Path));
    }

    // ---- Delete-safety ----------------------------------------------------

    [Fact]
    public async Task DeleteSafety_SourceLengthNoLongerMatchesCopy_RefusesDelete()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes("a much longer live source body"), BaseTime); // length != archived
        // Same-version archive with a DIFFERENT length -> copy is skipped and size comes from the
        // archive, so the pre-delete length check finds a mismatch and refuses to delete.
        h.Cold.Seed(Key, Bytes("short"), sourceLastModifiedUtc: BaseTime);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(MigrationLifecycleStatus.CopyToColdStorageFailed, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.Exists(Path));                // INVARIANT: not deleted on a mismatch
    }

    // ---- Already-gone (idempotent delete) ---------------------------------

    [Fact]
    public async Task SourceAlreadyGoneAtDelete_TreatedAsDeleted_Completes()
    {
        var h = Build();
        // Source item is absent (a prior attempt deleted it). Same-version archive exists, so the
        // copy is skipped; the pre-delete GetItem returns Missing -> treated as already deleted.
        h.Cold.Seed(Key, Bytes(), sourceLastModifiedUtc: BaseTime);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MigrationLifecycleStatus.ColdStorageMigrationCompleted, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.HasPointer(Path + ".url"));
    }

    // ---- Skips ------------------------------------------------------------

    [Fact]
    public async Task Ineligible_Skipped_NothingCopiedOrDeleted()
    {
        var h = Build(FakeEligibility.Ineligible("below minimum size"));
        h.Source.Seed(Path, Bytes(), BaseTime);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MigrationLifecycleStatus.Skipped, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.Exists(Path));
        Assert.False(h.Cold.Contains(Key));
    }

    [Fact]
    public async Task OnHold_Skipped_SourceIntact()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime).ComplianceTag = "Confidential";

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MigrationLifecycleStatus.Skipped, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.Exists(Path));
        Assert.False(h.Cold.Contains(Key));
    }

    // ---- Resume -----------------------------------------------------------

    [Fact]
    public async Task Resume_SourceDeletedByPriorAttempt_RecreatesPlaceholderFromArchive()
    {
        var h = Build();
        // No live source item (it was deleted). The lifecycle row proves a prior attempt copied +
        // deleted; the archive exists. Resume must NOT try to re-read the (gone) source.
        var item = h.Writer.Get(h.ItemId);
        item.Status = MigrationLifecycleStatus.PlaceholderCreating;
        item.SourceDeletedAt = BaseTime;
        item.BlobContainerName = Key.Container;
        item.BlobPath = Key.Path;
        item.FileSize = 5;
        h.Cold.Seed(Key, Bytes(), sourceLastModifiedUtc: BaseTime);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MigrationLifecycleStatus.ColdStorageMigrationCompleted, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.HasPointer(Path + ".url"));
    }

    [Fact]
    public async Task Resume_AlreadyHasPlaceholder_IsNoOpComplete()
    {
        var h = Build();
        var item = h.Writer.Get(h.ItemId);
        item.Status = MigrationLifecycleStatus.PlaceholderCreating;
        item.SourceDeletedAt = BaseTime;
        item.PlaceholderCreatedAt = BaseTime;
        item.PlaceholderServerRelativeUrl = Path + ".url";

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.False(h.Source.HasPointer(Path + ".url"));  // no new pointer written; treated as already complete
    }

    [Fact]
    public async Task PlaceholderFailure_AfterSourceDeleted_Parks_ForRetry()
    {
        var h = Build();
        h.Source.Seed(Path, Bytes(), BaseTime);
        h.Source.Faults.Throttle("WritePointer");

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        var item = h.Writer.Get(h.ItemId);
        Assert.Equal(MigrationLifecycleStatus.RetryScheduled, item.Status);
        Assert.NotNull(item.SourceDeletedAt);              // source already gone; archive is safe
        Assert.True(h.Cold.Contains(Key));
    }
}
