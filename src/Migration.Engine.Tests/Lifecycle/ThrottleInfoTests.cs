using Migration.Engine.Lifecycle;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

public class ThrottleInfoTests
{
    [Fact]
    public void NullException_ReturnsNull()
    {
        Assert.Null(ThrottleInfo.TryGetRetryAfterSeconds(null));
    }

    [Fact]
    public void PlainException_ReturnsNull()
    {
        Assert.Null(ThrottleInfo.TryGetRetryAfterSeconds(new InvalidOperationException("boom")));
    }

    [Fact]
    public void ExceptionWithRetryAfterData_ReturnsValue()
    {
        var ex = new Exception("throttled");
        ex.Data[ThrottleInfo.RetryAfterDataKey] = 137;
        Assert.Equal(137, ThrottleInfo.TryGetRetryAfterSeconds(ex));
    }

    [Fact]
    public void InnerExceptionCarriesHint_IsFound()
    {
        var inner = new Exception("inner");
        inner.Data[ThrottleInfo.RetryAfterDataKey] = 42;
        var outer = new Exception("outer", inner);
        Assert.Equal(42, ThrottleInfo.TryGetRetryAfterSeconds(outer));
    }

    [Theory]
    [InlineData("120", true, 120)]
    [InlineData("0", true, 0)]
    [InlineData("", false, 0)]
    [InlineData("not-a-number", false, 0)]
    public void TryParseSeconds_HandlesDeltaSeconds(string header, bool expectedOk, int expectedSeconds)
    {
        var ok = ThrottleInfo.TryParseSeconds(header, out var seconds);
        Assert.Equal(expectedOk, ok);
        if (expectedOk)
        {
            Assert.Equal(expectedSeconds, seconds);
        }
    }

    [Fact]
    public void TryParseSeconds_HttpDate_ReturnsNonNegativeDelta()
    {
        var future = DateTimeOffset.UtcNow.AddSeconds(90).ToString("R");
        var ok = ThrottleInfo.TryParseSeconds(future, out var seconds);
        Assert.True(ok);
        Assert.InRange(seconds, 60, 120);
    }
}
