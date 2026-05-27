using Microsoft.Graph;
using Azure.Identity;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Migration.Engine.Connectors;

/// <summary>
/// Manages Microsoft Graph API client and token refresh
/// Similar to SPOTokenManager but for Graph API instead of SharePoint CSOM
/// </summary>
public class GraphClientManager
{
    private readonly Config _config;
    private readonly ILogger _logger;
    private GraphServiceClient? _graphClient;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public GraphClientManager(Config config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets or refreshes the Graph client if the token is expired or about to expire
    /// </summary>
    public async Task<GraphServiceClient> GetOrRefreshClient()
    {
        // Refresh if client doesn't exist or token expires in less than 5 minutes
        if (_graphClient == null || _tokenExpiry < DateTime.UtcNow.AddMinutes(5))
        {
            _logger.LogInformation("Creating/refreshing Microsoft Graph client...");
            
            // Create credential based on authentication mode
            ClientSecretCredential credential;
            
            if (_config.AzureAdConfig.UseClientSecretAuth)
            {
                // Client secret authentication (no Key Vault needed)
                credential = new ClientSecretCredential(
                    _config.AzureAdConfig.TenantId,
                    _config.AzureAdConfig.ClientID,
                    _config.AzureAdConfig.Secret
                );
            }
            else
            {
                // Certificate authentication (original behavior)
                // Note: This uses the same secret to access Key Vault
                credential = new ClientSecretCredential(
                    _config.AzureAdConfig.TenantId,
                    _config.AzureAdConfig.ClientID,
                    _config.AzureAdConfig.Secret
                );
                
                // TODO: If we want true certificate auth here, we'd need to:
                // 1. Get certificate from Key Vault
                // 2. Use ClientCertificateCredential instead
                // For now, both modes use ClientSecretCredential for Graph
            }

            // Set token expiry (Azure AD tokens typically last 1 hour)
            _tokenExpiry = DateTime.UtcNow.AddMinutes(55);

            // Create Graph client with the credential
            _graphClient = new GraphServiceClient(credential);
            
            _logger.LogInformation("Microsoft Graph client ready");
        }

        return _graphClient;
    }

    /// <summary>
    /// Test the Graph client by fetching the root site
    /// </summary>
    public async Task<bool> TestConnection()
    {
        try
        {
            var client = await GetOrRefreshClient();
            var rootSite = await client.Sites["root"].GetAsync();
            
            if (rootSite != null)
            {
                _logger.LogInformation($"Successfully connected to Graph API. Root site: {rootSite.DisplayName}");
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Microsoft Graph API");
            return false;
        }
    }
}
