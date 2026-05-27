using Azure.Core;
using Azure.Identity;
using Entities.Configuration;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Utils.Http;
/// <summary>
/// HttpClient that can handle HTTP 429s automatically
/// </summary>
public class GraphThrottledHttpClient(Config config, bool ignoreRetryHeader, ILogger ILogger) : AutoThrottleHttpClient(ignoreRetryHeader, ILogger, new SecureGraphHandler(config))
{
}

public class SecureGraphHandler : DelegatingHandler
{
    protected Config _config;
    protected AccessToken _token;
    public SecureGraphHandler(Config config)
    {
        _config = config;
        InnerHandler = new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var confidentialClientApplication = new ClientSecretCredential(_config.AzureAdConfig.TenantId, _config.AzureAdConfig.ClientID, _config.AzureAdConfig.Secret);
        if (_token.ExpiresOn < DateTime.Now.AddMinutes(5))
        {
            _token = await confidentialClientApplication.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]));
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);

        return await base.SendAsync(request, cancellationToken);
    }

}
