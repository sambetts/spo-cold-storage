using Migration.Engine.Lifecycle;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

public class JobEtaCalculatorTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NothingRemaining_ReturnsNull()
    {
        var eta = JobEtaCalculator.EstimateCompletion(
            completedItems: 10, remainingItems: 0, jobStartUtc: Start, nowUtc: Start.AddMinutes(5), latestNextRetryUtc: null);
        Assert.Null(eta);
    }

    [Fact]
    public void SteadyThroughput_ProjectsFutureCompletion()
    {
        // 50 done in 10 minutes = 5/min; 50 remaining => ~10 more minutes.
        var now = Start.AddMinutes(10);
        var eta = JobEtaCalculator.EstimateCompletion(
            completedItems: 50, remainingItems: 50, jobStartUtc: Start, nowUtc: now, latestNextRetryUtc: null);
        Assert.NotNull(eta);
        Assert.True(eta > now, "ETA must be in the future");
        Assert.InRange((eta!.Value - now).TotalMinutes, 9, 11);
    }

    [Fact]
    public void NoCompletionsButScheduledRetry_AnchorsToRetryTime()
    {
        var now = Start.AddMinutes(3);
        var retryDue = Start.AddMinutes(30);
        var eta = JobEtaCalculator.EstimateCompletion(
            completedItems: 0, remainingItems: 20, jobStartUtc: Start, nowUtc: now, latestNextRetryUtc: retryDue);
        Assert.Equal(retryDue, eta);
    }

    [Fact]
    public void NoCompletionsNoRetry_ReturnsNull()
    {
        var eta = JobEtaCalculator.EstimateCompletion(
            completedItems: 0, remainingItems: 20, jobStartUtc: Start, nowUtc: Start.AddMinutes(3), latestNextRetryUtc: null);
        Assert.Null(eta);
    }

    [Fact]
    public void ThrottleFloorLaterThanThroughput_PushesEtaOut()
    {
        // Throughput alone would say ~2 more minutes, but a retry isn't due for 30 minutes.
        var now = Start.AddMinutes(10);
        var retryDue = now.AddMinutes(30);
        var eta = JobEtaCalculator.EstimateCompletion(
            completedItems: 90, remainingItems: 10, jobStartUtc: Start, nowUtc: now, latestNextRetryUtc: retryDue);
        Assert.Equal(retryDue, eta);
    }
}
