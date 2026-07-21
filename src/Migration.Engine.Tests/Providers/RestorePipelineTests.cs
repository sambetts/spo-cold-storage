using Microsoft.Extensions.Logging.Abstractions;
using Migration.Engine.Providers;
using Migration.Engine.Tests.Providers.InMemory;
using Models.ColdStorage;
using System.Text;
using Xunit;

namespace Migration.Engine.Tests.Providers;

/// <summary>
/// Exhaustive tests of the provider-neutral <see cref="RestorePipeline"/> with in-memory adaptors:
/// happy path, throttling/retry parking, missing archive, response-lost-but-landed idempotency,
/// verify-before-cold-delete, the in-flight guard, resume, and the placeholder-removal tail.
/// </summary>
public class RestorePipelineTests
{
    private static readonly TransferPipelineOptions Opts = new() { MaxProcessAttempts = 3, ThrottleBackoffBaseSeconds = 1, ThrottleBackoffMaxSeconds = 4 };
    private const string OriginalPath = "/sites/x/Shared Documents/a.docx";
    private const string PointerPath = OriginalPath + ".url";
    private static readonly ColdStorageKey Key = new("cold", "site|a.docx");

    private sealed record Harness(RestorePipeline Pipeline, InMemorySourceStore Source, InMemoryColdStore Cold, InMemoryJobStatusWriter Writer, Guid ItemId, Guid JobId);

    private static Harness Build(bool seedPointer = true, bool seedArchive = true, string archiveBody = "archived-body")
    {
        var source = new InMemorySourceStore();
        var cold = new InMemoryColdStore();
        var writer = new InMemoryJobStatusWriter();
        var pipeline = new RestorePipeline(Opts, NullLogger.Instance, writer, source, cold);
        var jobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        writer.Seed(itemId, jobId);

        if (seedArchive)
        {
            cold.Seed(Key, Encoding.UTF8.GetBytes(archiveBody), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        if (seedPointer)
        {
            source.SeedPointer(PointerPath, new PlaceholderFileMetadata
            {
                OriginalSiteUrl = "https://site",
                OriginalWebUrl = "https://site/web",
                OriginalServerRelativeUrl = OriginalPath,
                OriginalFileName = "a.docx",
                ContainerName = Key.Container,
                BlobPath = Key.Path,
                BlobUrl = "inmemory://cold/site|a.docx",
                ContentMd5Base64 = string.Empty,
            });
        }
        return new Harness(pipeline, source, cold, writer, itemId, jobId);
    }

    private static RestoreRequest Req(Harness h, ConflictBehavior conflict = ConflictBehavior.Fail, bool deleteCold = false) => new()
    {
        JobId = h.JobId,
        ItemId = h.ItemId,
        Pointer = new SourceItemRef("https://site", "https://site/web", PointerPath),
        ConflictBehavior = conflict,
        DeleteColdAfterRestore = deleteCold,
    };

    [Fact]
    public async Task HappyPath_RestoresContentAndRemovesPlaceholder()
    {
        var h = Build();

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MigrationLifecycleStatus.RestoreCompleted, h.Writer.Get(h.ItemId).Status);
        Assert.Equal("archived-body", Encoding.UTF8.GetString(h.Source.BytesAt(OriginalPath)!)); // content restored
        Assert.False(h.Source.HasPointer(PointerPath));    // placeholder removed
        Assert.True(h.Cold.Contains(Key));                 // archive kept (no delete requested)
    }

    [Fact]
    public async Task DeleteColdAfterRestore_RemovesArchive_OnlyAfterVerifiedRestore()
    {
        var h = Build();

        Assert.True(await h.Pipeline.ProcessAsync(Req(h, deleteCold: true), CancellationToken.None));
        Assert.False(h.Cold.Contains(Key));                // archive deleted after verified restore
        Assert.True(h.Source.Exists(OriginalPath));
    }

    [Fact]
    public async Task MissingArchive_FailsWithoutTouchingSource()
    {
        var h = Build(seedArchive: false);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(MigrationLifecycleStatus.RestoreFailed, h.Writer.Get(h.ItemId).Status);
        Assert.False(h.Source.Exists(OriginalPath));
    }

    [Fact]
    public async Task MissingPointer_ValidationFails()
    {
        var h = Build(seedPointer: false);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(MigrationLifecycleStatus.ValidationFailed, h.Writer.Get(h.ItemId).Status);
    }

    [Fact]
    public async Task TransientDownloadFailure_ParksForRetry()
    {
        var h = Build();
        h.Cold.Faults.Throttle("OpenRead", retryAfterSeconds: 3);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        var item = h.Writer.Get(h.ItemId);
        Assert.Equal(MigrationLifecycleStatus.RetryScheduled, item.Status);
        Assert.Equal(3, item.LastRetryAfterSeconds);
        Assert.False(h.Source.Exists(OriginalPath));       // nothing written
    }

    [Fact]
    public async Task Throttle_ThenRetrySucceeds()
    {
        var h = Build();
        h.Cold.Faults.Throttle("OpenRead");

        Assert.False(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None));
        Assert.True(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None));
        Assert.Equal(MigrationLifecycleStatus.RestoreCompleted, h.Writer.Get(h.ItemId).Status);
    }

    [Fact]
    public async Task ResponseLostButLanded_IsIdempotentOnRetry()
    {
        var h = Build();
        // First attempt: the upload lands but its response is lost (transient) -> park. The retry
        // finds the destination already holds the same-length content and short-circuits to success.
        h.Source.Faults.Transient("WriteContentAfter");

        Assert.False(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None));
        Assert.Equal(MigrationLifecycleStatus.RetryScheduled, h.Writer.Get(h.ItemId).Status);
        Assert.Equal("archived-body", Encoding.UTF8.GetString(h.Source.BytesAt(OriginalPath)!)); // it did land

        Assert.True(await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None));
        Assert.Equal(MigrationLifecycleStatus.RestoreCompleted, h.Writer.Get(h.ItemId).Status);
        Assert.False(h.Source.HasPointer(PointerPath));
    }

    [Fact]
    public async Task VerifyFailure_ParksForRetry()
    {
        var h = Build();
        h.Source.Faults.Transient("GetItem");              // the post-restore verify read fails transiently

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(MigrationLifecycleStatus.RetryScheduled, h.Writer.Get(h.ItemId).Status);
    }

    [Fact]
    public async Task PlaceholderRemovalFailure_Transient_Parks_ContentAlreadyRestored()
    {
        var h = Build();
        h.Source.Faults.Throttle("RemovePointer");

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.False(ok);
        var item = h.Writer.Get(h.ItemId);
        Assert.Equal(MigrationLifecycleStatus.RetryScheduled, item.Status);
        Assert.NotNull(item.RestoredAt);                   // content is restored; only the tail failed
        Assert.True(h.Source.Exists(OriginalPath));
    }

    [Fact]
    public async Task Resume_ContentAlreadyRestored_RemovesPlaceholderOnly()
    {
        var h = Build();
        var item = h.Writer.Get(h.ItemId);
        item.Status = MigrationLifecycleStatus.RestoredToSharePoint;
        item.RestoredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MigrationLifecycleStatus.RestoreCompleted, h.Writer.Get(h.ItemId).Status);
        Assert.False(h.Source.HasPointer(PointerPath));    // pointer removed
        Assert.False(h.Source.Exists(OriginalPath));       // no re-download/upload happened
    }

    [Fact]
    public async Task ConcurrentRestoreOfSamePointer_IsCoalesced()
    {
        var h = Build();
        h.Writer.SimulateRestoreInFlight = true;

        var ok = await h.Pipeline.ProcessAsync(Req(h), CancellationToken.None);

        Assert.True(ok);                                   // coalesced (no-op), not a failure
        Assert.Equal(MigrationLifecycleStatus.Cancelled, h.Writer.Get(h.ItemId).Status);
        Assert.True(h.Source.HasPointer(PointerPath));     // left intact for the other in-flight restore
    }

    [Fact]
    public async Task ConflictFail_WhenDestinationHasDifferentContent()
    {
        var h = Build();
        h.Source.Seed(OriginalPath, Encoding.UTF8.GetBytes("a different pre-existing file"));

        var ok = await h.Pipeline.ProcessAsync(Req(h, ConflictBehavior.Fail), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(MigrationLifecycleStatus.RestoreFailed, h.Writer.Get(h.ItemId).Status);
    }

    [Fact]
    public async Task ConflictOverwrite_ReplacesDifferentDestination()
    {
        var h = Build();
        h.Source.Seed(OriginalPath, Encoding.UTF8.GetBytes("stale existing"));

        Assert.True(await h.Pipeline.ProcessAsync(Req(h, ConflictBehavior.Overwrite), CancellationToken.None));
        Assert.Equal("archived-body", Encoding.UTF8.GetString(h.Source.BytesAt(OriginalPath)!));
    }
}
