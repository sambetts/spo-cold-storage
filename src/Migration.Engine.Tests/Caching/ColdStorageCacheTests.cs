using AwesomeAssertions;
using Migration.Engine.Caching;
using Xunit;

namespace Migration.Engine.Tests.Caching;

/// <summary>
/// The cross-process cache (issue #68). The behaviour that matters is not "does it store
/// things" but the guarantees around it: expiry, fail-open on a broken backend, and the
/// tiered cache writing through so another instance benefits.
/// </summary>
public class ColdStorageCacheTests
{
    private sealed record Payload(string Name, int Count);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- in-memory (L1) ----

    [Fact]
    public async Task InMemory_RoundTripsAValue()
    {
        var cache = new InMemoryColdStorageCache();
        await cache.SetAsync("k", new Payload("ada", 3), TimeSpan.FromMinutes(5), Ct);

        var hit = await cache.GetAsync<Payload>("k", Ct);

        hit.Should().NotBeNull();
        hit!.Name.Should().Be("ada");
        hit.Count.Should().Be(3);
    }

    [Fact]
    public async Task InMemory_TreatsAnExpiredEntryAsAMiss()
    {
        var cache = new InMemoryColdStorageCache();
        // Already expired: a stale permission decision must never be served.
        await cache.SetAsync("k", new Payload("ada", 1), TimeSpan.FromMilliseconds(-1), Ct);

        (await cache.GetAsync<Payload>("k", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task InMemory_MissingKeyIsNull_NotAnError()
    {
        var cache = new InMemoryColdStorageCache();
        (await cache.GetAsync<Payload>("nope", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task InMemory_RemoveEvicts()
    {
        var cache = new InMemoryColdStorageCache();
        await cache.SetAsync("k", new Payload("ada", 1), TimeSpan.FromMinutes(5), Ct);
        await cache.RemoveAsync("k", Ct);

        (await cache.GetAsync<Payload>("k", Ct)).Should().BeNull();
    }

    // ---- tiered (L1 + L2) ----

    [Fact]
    public async Task Tiered_PopulatesTheLocalCacheFromTheSharedOne()
    {
        var l1 = new InMemoryColdStorageCache();
        var l2 = new InMemoryColdStorageCache();
        // Simulate another instance having warmed the shared cache.
        await l2.SetAsync("k", new Payload("ada", 7), TimeSpan.FromMinutes(10), Ct);

        var tiered = new TieredColdStorageCache(l1, l2);
        (await tiered.GetAsync<Payload>("k", Ct))!.Count.Should().Be(7);

        // The point of the tier: the next read is served locally, with no shared round trip.
        (await l1.GetAsync<Payload>("k", Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Tiered_WritesThroughToTheSharedCache()
    {
        var l1 = new InMemoryColdStorageCache();
        var l2 = new InMemoryColdStorageCache();
        var tiered = new TieredColdStorageCache(l1, l2);

        await tiered.SetAsync("k", new Payload("ada", 2), TimeSpan.FromMinutes(10), Ct);

        // Without this, another instance would still pay the SharePoint call.
        (await l2.GetAsync<Payload>("k", Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Tiered_RemoveClearsBothLevels()
    {
        var l1 = new InMemoryColdStorageCache();
        var l2 = new InMemoryColdStorageCache();
        var tiered = new TieredColdStorageCache(l1, l2);
        await tiered.SetAsync("k", new Payload("ada", 2), TimeSpan.FromMinutes(10), Ct);

        await tiered.RemoveAsync("k", Ct);

        (await l1.GetAsync<Payload>("k", Ct)).Should().BeNull();
        (await l2.GetAsync<Payload>("k", Ct)).Should().BeNull();
    }

    // ---- helpers ----

    [Fact]
    public async Task GetOrCreate_OnlyInvokesTheFactoryOnce()
    {
        var cache = new InMemoryColdStorageCache();
        var calls = 0;

        for (var i = 0; i < 3; i++)
        {
            await cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(5), _ =>
            {
                calls++;
                return Task.FromResult<Payload?>(new Payload("ada", 1));
            }, Ct);
        }

        // This is the entire value proposition — three lookups, one SharePoint call.
        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateBool_CachesFalseAsWellAsTrue()
    {
        // A "false" that isn't cached would re-hit SharePoint on every request for a user
        // who legitimately has no access — the exact hot path we're protecting.
        var cache = new InMemoryColdStorageCache();
        var calls = 0;

        for (var i = 0; i < 3; i++)
        {
            var allowed = await cache.GetOrCreateBoolAsync("flag", TimeSpan.FromMinutes(5), _ =>
            {
                calls++;
                return Task.FromResult(false);
            }, Ct);
            allowed.Should().BeFalse();
        }

        calls.Should().Be(1);
    }

    // ---- keys ----

    [Fact]
    public void Keys_AreCaseAndTrailingSlashInsensitive()
    {
        // The same site written two ways must not warm two cache entries.
        ColdStorageCacheKeys.SiteContributor("https://x.sharepoint.com/sites/A/", "Ada@x.com")
            .Should().Be(ColdStorageCacheKeys.SiteContributor("https://x.sharepoint.com/sites/a", "ada@X.com"));
    }

    [Fact]
    public void Keys_ForDifferentFeatures_DoNotCollide()
    {
        ColdStorageCacheKeys.RoleDefinitions("https://x.sharepoint.com/sites/a")
            .Should().NotBe(ColdStorageCacheKeys.SiteContributor("https://x.sharepoint.com/sites/a", "ada@x.com"));
    }

    [Fact]
    public void TableRowKeys_AreLegal_ForUrlShapedKeys()
    {
        // Table Storage rejects '/', '\', '#', '?' in a row key, and our keys are built
        // from URLs — so anything awkward has to be hashed.
        var sanitised = TableColdStorageCache.Sanitize("sp|roledefs|https://x.sharepoint.com/sites/a");

        sanitised.Should().NotContainAny("/", "\\", "#", "?");
        sanitised.Should().NotBeEmpty();
    }

    [Fact]
    public void TableRowKeys_AreStable()
    {
        const string key = "sp|roledefs|https://x.sharepoint.com/sites/a";
        TableColdStorageCache.Sanitize(key).Should().Be(TableColdStorageCache.Sanitize(key));
    }
}
