using Migration.Engine.Lifecycle;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// The exponential backoff schedule shared by the pipeline and the dispatch
/// reconciler for transient/throttle retries.
/// </summary>
public class ThrottleBackoffTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 600)]   // 960 capped to 600
    [InlineData(50, 600)]  // capped
    public void Exponential_WithCap(int attempt, int expected)
        => Assert.Equal(expected, ThrottleBackoff.SecondsFor(attempt, baseSeconds: 30, maxSeconds: 600));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveAttempt_TreatedAsFirst(int attempt)
        => Assert.Equal(30, ThrottleBackoff.SecondsFor(attempt, 30, 600));

    [Fact]
    public void NeverExceedsCap()
        => Assert.True(ThrottleBackoff.SecondsFor(1000, 30, 600) <= 600);
}
