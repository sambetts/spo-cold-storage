using Migration.Engine.Reconciliation;
using Xunit;

namespace Migration.Engine.Tests.Reconciliation;

public class ColdStorageReconcilerTests
{
    [Theory]
    [InlineData("delete", OrphanPolicy.Delete)]
    [InlineData("Delete", OrphanPolicy.Delete)]
    [InlineData("  DELETE ", OrphanPolicy.Delete)]
    [InlineData("quarantine", OrphanPolicy.Quarantine)]
    [InlineData("report", OrphanPolicy.Report)]
    [InlineData("", OrphanPolicy.Report)]
    [InlineData(null, OrphanPolicy.Report)]
    [InlineData("nonsense", OrphanPolicy.Report)]
    public void ParsePolicy_DefaultsToReport_AndIsCaseInsensitive(string? raw, OrphanPolicy expected)
        => Assert.Equal(expected, ColdStorageReconciler.ParsePolicy(raw));
}
