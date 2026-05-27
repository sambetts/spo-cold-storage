using Microsoft.Identity.Client;
using Microsoft.SharePoint.Client;
using Entities.Configuration;
using Migration.Engine.Utils;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Connectors;

public class SPOTokenManager(Config config, string siteUrl, ILogger logger)
{
    private readonly Config _config = config;
    private readonly string _siteUrl = siteUrl;
    private readonly ILogger _logger = logger;
    private AuthenticationResult? _contextAuthResult = null;
    protected ClientContext? _context = null;

    public async Task<ClientContext> GetOrRefreshContext()
    {
        return await GetOrRefreshContext(null)!;
    }
    public async Task<ClientContext> GetOrRefreshContext(Action? newTokenCallback)
    {
        if (_contextAuthResult == null || _contextAuthResult.ExpiresOn < DateTime.Now.AddMinutes(-5))
        {
            _logger.LogInformation($"Refreshing SPO access token...");
            _context = await AuthUtils.GetClientContext(_config, _siteUrl, _logger, (AuthenticationResult auth) => _contextAuthResult = auth);
            await EnsureContextWebIsLoaded(_context);

            if (newTokenCallback != null)
            {
                newTokenCallback();
            }
        }
        return _context!;
    }
    public async Task EnsureContextWebIsLoaded(ClientContext spClient)
    {
        var loaded = false;
        try
        {
            // Test if this will blow up
            var url = spClient.Web.Url;
            url = spClient.Site.Url;
            loaded = true;
        }
        catch (PropertyOrFieldNotInitializedException)
        {
            loaded = false;
        }

        if (!loaded)
        {
            spClient.Load(spClient.Web);
            spClient.Load(spClient.Site, s => s.Url);
            await spClient.ExecuteQueryAsyncWithThrottleRetries(_logger);
        }
    }
}
