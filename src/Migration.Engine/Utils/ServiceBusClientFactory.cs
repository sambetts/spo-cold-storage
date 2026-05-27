using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Entities.Configuration;

namespace Migration.Engine.Utils;
/// <summary>
/// Factory for creating ServiceBusClient instances with RBAC authentication
/// </summary>
public static class ServiceBusClientFactory
{
    /// <summary>
    /// Creates a ServiceBusClient using RBAC authentication
    /// </summary>
    /// <param name="serviceBusEndpoint">Azure Service Bus endpoint or connection string</param>
    /// <param name="config">Application configuration for credentials</param>
    /// <returns>Configured ServiceBusClient</returns>
    public static ServiceBusClient Create(string serviceBusEndpoint, Config config)
    {
        if (config == null)
        {
            throw new ArgumentException("Config is required for Service Bus RBAC authentication", nameof(config));
        }

        // Extract the fully qualified namespace from connection string or endpoint
        var fullyQualifiedNamespace = ExtractServiceBusNamespace(serviceBusEndpoint);

        // Use RBAC with ClientSecretCredential
        var credential = new ClientSecretCredential(
            config.AzureAdConfig.TenantId,
            config.AzureAdConfig.ClientID,
            config.AzureAdConfig.Secret,
            new ClientSecretCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
            });

        return new ServiceBusClient(fullyQualifiedNamespace, credential);
    }

    /// <summary>
    /// Extracts the Service Bus namespace from a connection string or endpoint
    /// </summary>
    private static string ExtractServiceBusNamespace(string serviceBusEndpoint)
    {
        // If it's already a fully qualified namespace (e.g., "myservicebus.servicebus.windows.net"), return it
        if (!serviceBusEndpoint.Contains("=") && serviceBusEndpoint.Contains(".servicebus.windows.net"))
        {
            return serviceBusEndpoint;
        }

        // If it starts with https://, extract the host
        if (serviceBusEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(serviceBusEndpoint);
            return uri.Host;
        }

        // Parse connection string to extract Endpoint
        var parts = serviceBusEndpoint.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(split => split.Length == 2)
            .ToDictionary(split => split[0].Trim(), split => split[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (parts.TryGetValue("Endpoint", out var endpoint))
        {
            // Endpoint is typically in the format: sb://myservicebus.servicebus.windows.net/
            var uri = new Uri(endpoint);
            return uri.Host;
        }

        throw new ArgumentException(
            "Invalid Service Bus connection string format - no Endpoint or valid namespace found",
            nameof(serviceBusEndpoint));
    }
}
