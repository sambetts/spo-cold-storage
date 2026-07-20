using Migration.Engine.Migration;
using Xunit;

namespace Migration.Engine.Tests.Migration;

public class MigrateConflictResolverTests
{
    private static readonly DateTime Archived = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoArchivedTimestamp_Overwrites()
    {
        var decision = MigrateConflictResolver.Decide(Archived, archivedSourceLastModifiedUtc: null);
        Assert.Equal(BlobConflictDecision.Overwrite, decision);
    }

    [Fact]
    public void SourceNewerThanArchive_Overwrites()
    {
        var decision = MigrateConflictResolver.Decide(Archived.AddMinutes(5), Archived);
        Assert.Equal(BlobConflictDecision.Overwrite, decision);
    }

    [Fact]
    public void SourceSameAsArchive_SkipsSameVersion()
    {
        var decision = MigrateConflictResolver.Decide(Archived, Archived);
        Assert.Equal(BlobConflictDecision.SkipSameVersion, decision);
    }

    [Fact]
    public void WithinTolerance_CountsAsSameVersion()
    {
        var decision = MigrateConflictResolver.Decide(Archived.AddSeconds(1), Archived);
        Assert.Equal(BlobConflictDecision.SkipSameVersion, decision);
    }

    [Fact]
    public void ArchiveNewerThanSource_ReportsDestinationNewer()
    {
        var decision = MigrateConflictResolver.Decide(Archived.AddMinutes(-5), Archived);
        Assert.Equal(BlobConflictDecision.DestinationNewer, decision);
    }

    [Fact]
    public void MixedKinds_ComparedInUtc()
    {
        // The same instant expressed with a local kind must still count as the same version.
        var localSame = Archived.ToLocalTime();
        Assert.Equal(BlobConflictDecision.SkipSameVersion,
            MigrateConflictResolver.Decide(localSame, Archived));
    }
}
