using Microsoft.Identity.Client;
using Entities.Configuration;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Utils.Http;
/// <summary>
/// HttpClient that can handle HTTP 429s automatically
/// </summary>
public class SecureSPThrottledHttpClient(Config config, bool ignoreRetryHeader, ILogger ILogger) : AutoThrottleHttpClient(ignoreRetryHeader, ILogger, new SecureSPHandler(config))
{
}

public class SecureSPHandler : DelegatingHandler
{
    protected Config _config;
    private AuthenticationResult? auth = null;
    public SecureSPHandler(Config config)
    {
        _config = config;
        InnerHandler = new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Security boundary (mirrors AuthUtils.ValidateSiteUrl): this handler attaches an app-only
        // SharePoint token with Sites.FullControl.All to the outgoing request. Request URLs are built
        // from stored site/web URLs that originate in caller-supplied input, so refuse to send the
        // token anywhere outside the configured tenant.
        AuthUtils.ValidateSiteUrl(
            request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? string.Empty,
            _config.BaseServerAddress);

        // Get auth for REST
        var app = await AuthUtils.GetNewClientApp(_config);

        if (auth == null || auth.ExpiresOn < DateTimeOffset.Now.AddMinutes(5))
        {
            auth = await app.AuthForSharePointOnline(_config.BaseServerAddress);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return await base.SendAsync(request, cancellationToken);
    }

}
