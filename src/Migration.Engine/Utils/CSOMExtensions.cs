using Migration.Engine.Lifecycle;
using Microsoft.SharePoint.Client;
using System.Net;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Utils;

public static class CSOMExtensions
{
    public static async Task ExecuteQueryAsyncWithThrottleRetries(this ClientContext clientContext, ILogger logger)
    {
        int retryAttempts = 0;
        int backoffIntervalSeconds = 1;
        int retryAfterInterval = 0;
        int? lastRetryAfterSeconds = null;
        bool retryWithWrapper = false;
        ClientRequestWrapper? wrapper = null;

        // Do while retry attempt is less than retry count
        while (retryAttempts < Constants.MAX_SPO_API_RETRIES)
        {
            try
            {
                if (!retryWithWrapper)
                {
                    await clientContext.ExecuteQueryAsync();
                    return;
                }
                else
                {
                    retryAttempts++;

                    // retry the previous request using wrapper
                    if (wrapper != null && wrapper.Value != null)
                    {
                        await clientContext.RetryQueryAsync(wrapper.Value);
                        return;
                    }
                    // retry the previous request as normal
                    else
                    {
                        await clientContext.ExecuteQueryAsync();
                        return;
                    }
                }
            }
            catch (WebException ex)
            {
                // Check if request was throttled - http status code 429
                // Check is request failed due to server unavailable - http status code 503
                if (ex.Response is HttpWebResponse response && (response.StatusCode == (HttpStatusCode)429 || response.StatusCode == (HttpStatusCode)503))
                {
                    var clientRequestData = ex.Data["ClientRequest"];
                    if (clientRequestData != null)
                    {
                        wrapper = (ClientRequestWrapper)clientRequestData;
                        retryWithWrapper = true;
                    }

                    // Determine the retry after value - use the `Retry-After` header when available
                    string retryAfterHeader = response.GetResponseHeader("Retry-After");
                    if (!string.IsNullOrEmpty(retryAfterHeader) && Int32.TryParse(retryAfterHeader, out retryAfterInterval))
                    {
                        lastRetryAfterSeconds = retryAfterInterval;
                    }
                    else
                    {
                        retryAfterInterval = backoffIntervalSeconds;
                    }

                    // Trace standard throttling message
                    logger.LogWarning($"{Constants.THROTTLE_ERROR} executing CSOM request. Sleeping for {retryAfterInterval} seconds.");

                    // Delay for the requested seconds
                    await Task.Delay(retryAfterInterval * 1000);

                    // Increase counters
                    backoffIntervalSeconds *= 2;
                }
                else
                {
                    throw;
                }
            }
        }

        // Track error & throw exception. Carry the last server-provided Retry-After so the
        // pipeline can schedule the item's next attempt for the time SharePoint asked for
        // (see ThrottleInfo) instead of falling back to a generic backoff.
        var givingUpMsgBody = $"Maximum retry attempts {Constants.MAX_SPO_API_RETRIES} has been attempted.";
        logger.LogError($"{Constants.THROTTLE_ERROR} executing CSOM request. {givingUpMsgBody}");
        var giveUp = new Exception($"{Constants.THROTTLE_ERROR} executing CSOM request. {givingUpMsgBody}");
        if (lastRetryAfterSeconds is int retryAfter)
        {
            giveUp.Data[ThrottleInfo.RetryAfterDataKey] = retryAfter;
        }
        throw giveUp;

    }
}
