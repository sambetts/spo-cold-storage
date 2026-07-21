using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Migration.Engine.Lifecycle;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Verifies the transient/permanent split that decides whether a failure is retried
/// with backoff (RetryScheduled) or fails the item terminally.
/// </summary>
public class TransientErrorClassifierTests
{
    [Theory]
    [InlineData("The remote server returned 429 Too Many Requests")]
    [InlineData("Request was throttled by SharePoint")]
    [InlineData("The operation timed out")]
    [InlineData("503 Service Unavailable")]
    [InlineData("504 Gateway Timeout")]
    [InlineData("An existing connection was forcibly closed by the remote host")]
    // Exact strings from the bulk-restore throttle incident (job 8d0a36dc): the raw CSOM 429
    // WebException, and the exception thrown once ExecuteQueryAsyncWithThrottleRetries exhausts
    // its retries. Both MUST classify transient so the restore pipeline parks them for retry.
    [InlineData("The remote server returned an error: (429) .")]
    [InlineData("Got throttled by SPO executing CSOM request. Maximum retry attempts 10 has been attempted.")]
    // "I/O error occurred" is a transient SharePoint CSOM hiccup under load (seen on the bulk
    // restore's uploads); it must retry, not fail terminally.
    [InlineData("Microsoft.SharePoint.Client.ServerException: I/O error occurred.")]
    [InlineData("I/O error occurred.")]
    public void TransientMessages_AreTransient(string message)
        => Assert.True(TransientErrorClassifier.IsTransient(message));

    [Theory]
    [InlineData("403 Forbidden: Access denied")]
    [InlineData("404 Not Found")]
    [InlineData("Blob MD5 mismatch (expected x, got y)")]
    [InlineData("something specific went wrong")]
    [InlineData("")]
    [InlineData(null)]
    public void PermanentOrUnknownMessages_AreNotTransient(string? message)
        => Assert.False(TransientErrorClassifier.IsTransient(message));

    [Fact]
    public void TimeoutException_IsTransient()
        => Assert.True(TransientErrorClassifier.IsTransient(new TimeoutException()));

    [Fact]
    public void SocketException_IsTransient()
        => Assert.True(TransientErrorClassifier.IsTransient(new SocketException()));

    [Fact]
    public void HttpRequestException_503_IsTransient()
        => Assert.True(TransientErrorClassifier.IsTransient(new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable)));

    [Fact]
    public void HttpRequestException_403_IsNotTransient()
        => Assert.False(TransientErrorClassifier.IsTransient(new HttpRequestException("nope", null, HttpStatusCode.Forbidden)));

    [Fact]
    public void InnerExceptionIsInspected()
    {
        var ex = new InvalidOperationException("wrapper", new Exception("The remote server returned 429 Too Many Requests"));
        Assert.True(TransientErrorClassifier.IsTransient(ex));
    }

    [Fact]
    public void PlainException_IsNotTransient()
        => Assert.False(TransientErrorClassifier.IsTransient(new InvalidOperationException("access is denied to the resource")));
}
