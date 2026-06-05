using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Entities.Configuration;

namespace Migration.Engine.Utils;
/// <summary>
/// Factory for creating ServiceBusClient instances with RBAC authentication.
///
/// Uses <see cref="DefaultAzureCredential"/> so the same code path works for:
///   - Web.Server / WebJobs running on App Service -> picks up the system-
///     assigned Managed Identity (which is granted Azure Service Bus Data
///     Sender + Receiver by bicep).
///   - Local development -> falls through to AzureCliCredential /
///     VisualStudioCredential / EnvironmentCredential automatically.
///
/// The old implementation used <see cref="ClientSecretCredential"/> with the
/// AAD app registration's clientId/secret, which required granting the AAD
/// app SP Service Bus RBAC on top of the MSI - and in this deployment the
/// SP was missing the role, producing "Send claim(s) are required" 401s.
/// </summary>
public static class ServiceBusClientFactory
{
    /// <summary>
    /// Creates a ServiceBusClient using AAD RBAC.
    /// </summary>
    /// <param name="serviceBusEndpoint">SAS connection string OR fully qualified namespace (e.g. <c>sb-X.servicebus.windows.net</c>).</param>
    /// <param name="config">Application configuration. Reserved for future use; not currently required.</param>
    public static ServiceBusClient Create(string serviceBusEndpoint, Config config)
    {
        _ = config; // kept for source compat; auth no longer needs an AAD app secret

        if (string.IsNullOrWhiteSpace(serviceBusEndpoint))
        {
            throw new ArgumentException("Service Bus endpoint / connection string is required.", nameof(serviceBusEndpoint));
        }

        var fullyQualifiedNamespace = ExtractServiceBusNamespace(serviceBusEndpoint);
        var credential = new DefaultAzureCredential();
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
