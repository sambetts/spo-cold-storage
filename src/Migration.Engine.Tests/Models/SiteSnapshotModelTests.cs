using Migration.Engine.SnapshotBuilder;
using Models;
using Xunit;

namespace Migration.Engine.Tests.Models;

public class SiteSnapshotModelTests
{
    /// <summary>
    /// Tests SiteSnapshotModel.AnalysisFinished
    /// </summary>
    [Fact]
    public void ModelAnalysisFinishedTests()
    {
        var m = new SiteSnapshotModel();
        var l = new DocLib();
        m.Lists.Add(l);

        var f1 = new DocumentSiteWithMetadata { State = SiteFileAnalysisState.AnalysisPending };
        var f2 = new DocumentSiteWithMetadata { State = SiteFileAnalysisState.AnalysisInProgress };
        l.Files.AddRange(new DocumentSiteWithMetadata[] { f1, f2 });

        m.InvalidateCaches();
        Assert.False(m.AnalysisFinished);

        f1.State = SiteFileAnalysisState.Complete;
        m.InvalidateCaches();
        Assert.False(m.AnalysisFinished);

        f2.State = SiteFileAnalysisState.Complete;
        m.InvalidateCaches();
        Assert.True(m.AnalysisFinished);
    }
}
