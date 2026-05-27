using Azure.Identity;
using Azure.Storage.Blobs;
using Entities.Configuration;

namespace Migration.Engine.Utils;
/// <summary>
/// Factory for creating BlobServiceClient instances with appropriate authentication
/// </summary>
public static class BlobServiceClientFactory
{
    /// <summary>
    /// Creates a BlobServiceClient from a connection string, handling both production and development scenarios
    /// </summary>
    /// <param name="connectionString">Azure Storage connection string or endpoint URI</param>
    /// <param name="config">Application configuration (optional, used for production credentials)</param>
    /// <returns>Configured BlobServiceClient</returns>
    public static BlobServiceClient Create(string connectionString, Config? config = null)
    {
        // Check if this is a development storage connection string
        if (IsDevelopmentStorage(connectionString))
        {
            // For development storage (Azurite), use the connection string directly without credentials
            return new BlobServiceClient(connectionString);
        }

        // For production scenarios
        if (connectionString.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Connection string is already a URI endpoint
            if (config == null)
            {
                throw new ArgumentException("Config is required for production storage endpoints", nameof(config));
            }

            var credential = new ClientSecretCredential(
                config.AzureAdConfig.TenantId,
                config.AzureAdConfig.ClientID,
                config.AzureAdConfig.Secret,
                new ClientSecretCredentialOptions
                {
                    AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
                });

            return new BlobServiceClient(new Uri(connectionString), credential);
        }

        // Parse connection string to extract endpoint and determine if it needs credentials
        var endpoint = ExtractStorageEndpoint(connectionString);

        if (config != null)
        {
            // Use credentials for production storage
            var credential = new ClientSecretCredential(
                config.AzureAdConfig.TenantId,
                config.AzureAdConfig.ClientID,
                config.AzureAdConfig.Secret,
                new ClientSecretCredentialOptions
                {
                    AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
                });

            return new BlobServiceClient(new Uri(endpoint), credential);
        }
        else
        {
            // If no config provided, assume connection string has all auth info
            return new BlobServiceClient(connectionString);
        }
    }

    /// <summary>
    /// Checks if the connection string is for development storage (Azurite or Azure Storage Emulator)
    /// </summary>
    private static bool IsDevelopmentStorage(string connectionString)
    {
        return connectionString.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase) ||
               connectionString.Contains("AccountName=devstoreaccount1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the storage endpoint from a connection string
    /// </summary>
    private static string ExtractStorageEndpoint(string connectionString)
    {
        // If it's already a URI, return it
        if (connectionString.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        // Parse connection string to extract BlobEndpoint or construct from AccountName
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(split => split.Length == 2)
            .ToDictionary(split => split[0].Trim(), split => split[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (parts.TryGetValue("BlobEndpoint", out var blobEndpoint))
        {
            return blobEndpoint;
        }

        if (parts.TryGetValue("AccountName", out var accountName))
        {
            return $"https://{accountName}.blob.core.windows.net";
        }

        throw new ArgumentException("Invalid storage connection string format - no BlobEndpoint or AccountName found", nameof(connectionString));
    }
}
