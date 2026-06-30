using Migration.Engine.Migration;
using Xunit;

namespace Migration.Engine.Tests.Migration;

/// <summary>
/// Covers the pure retention-label decision used by the compliance-hold gate
/// (issue #15). The CSOM plumbing that reads <c>_ComplianceTag</c> is kept thin
/// and exercised against a live SharePoint context, not here.
/// </summary>
public class RetentionLabelHoldDetectorTests
{
    [Theory]
    [InlineData("Retain-7yrs", true)]
    [InlineData("Legal Hold", true)]
    [InlineData("  Record  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void ShouldTreatAsHold_NonEmptyLabelMeansHeld(string? complianceTag, bool expected)
        => Assert.Equal(expected, RetentionLabelHoldDetector.ShouldTreatAsHold(complianceTag));
}
