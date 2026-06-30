using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Cost model for the savings KPI dashboard (issue #8).
/// </summary>
public class SavingsCalculatorTests
{
    [Fact]
    public void Compute_OneTebibyte_GivesExpectedCostsAndSavings()
    {
        // 1024 GiB at $0.02 Azure vs $0.20 SPO -> azure 20.48, spo 204.80, net 184.32
        long bytes = (long)(1024 * SavingsCalculator.BytesPerGb);
        var b = SavingsCalculator.Compute(bytes, 0.02m, 0.20m);

        Assert.Equal(1024d, b.ReclaimedGb, 3);
        Assert.Equal(20.48m, b.AzureCostPerMonth);
        Assert.Equal(204.80m, b.SpoValuePerMonth);
        Assert.Equal(184.32m, b.NetSavingsPerMonth);
    }

    [Fact]
    public void Compute_Zero_IsAllZero()
    {
        var b = SavingsCalculator.Compute(0, 0.02m, 0.20m);
        Assert.Equal(0, b.ReclaimedBytes);
        Assert.Equal(0d, b.ReclaimedGb);
        Assert.Equal(0m, b.AzureCostPerMonth);
        Assert.Equal(0m, b.NetSavingsPerMonth);
    }

    [Fact]
    public void Compute_NegativeBytes_ClampedToZero()
    {
        var b = SavingsCalculator.Compute(-5, 0.02m, 0.20m);
        Assert.Equal(0, b.ReclaimedBytes);
        Assert.Equal(0m, b.NetSavingsPerMonth);
    }

    [Fact]
    public void Compute_NetCanBeNegative_WhenAzureCostsMoreThanSpoValue()
    {
        long bytes = (long)(100 * SavingsCalculator.BytesPerGb);
        var b = SavingsCalculator.Compute(bytes, 0.50m, 0.20m);
        Assert.True(b.NetSavingsPerMonth < 0);
    }
}
