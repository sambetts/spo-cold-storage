using Azure.Identity;
using Azure.Storage.Blobs;
using Entities.Configuration;

namespace Migration.Engine.Utils;
/// <summary>
/// Factory for creating BlobServiceClient instances with appropriate authentication.
///
/// For production endpoints (https://*.blob.core.windows.net) uses
/// <see cref="DefaultAzureCredential"/> so the same code works for the App
/// Service Managed Identity, local CLI / VS auth, and any future workload
/// identity. The previous implementation used <see cref="ClientSecretCredential"/>
/// with the AAD app registration's clientId/secret which required granting that
/// SP Storage RBAC on top of the MSI - and the workers ended up failing with
/// AuthorizationPermissionMismatch when only the MSI had the role.
///
/// Connection strings that carry an AccountKey are still accepted: if no Config
/// is supplied, the underlying SDK uses the key. Production deployments should
/// prefer the RBAC path (config != null) over baking storage keys into the
/// process.
/// </summary>
public static class BlobServiceClientFactory
{
    /// <summary>
    /// Creates a BlobServiceClient from a connection string or a storage
    /// endpoint URI.
    /// </summary>
    /// <param name="connectionString">Either an Azure Storage connection string, an https:// blob endpoint URI, or a UseDevelopmentStorage=true Azurite string.</param>
    /// <param name="config">When supplied, forces RBAC via DefaultAzureCredential. When null, the connection string's embedded auth (e.g. AccountKey) is used directly.</param>
    public static BlobServiceClient Create(string connectionString, Config? config = null)
    {
        if (IsDevelopmentStorage(connectionString))
        {
            // Azurite / Storage Emulator: connection string carries the well-known dev key.
            return new BlobServiceClient(connectionString);
        }

        if (config == null)
        {
            // No Config -> trust the connection string (must carry its own auth).
            return connectionString.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? throw new ArgumentException("A plain https:// endpoint requires a Config so we can attach a credential. Pass `config` explicitly.", nameof(connectionString))
                : new BlobServiceClient(connectionString);
        }

        // Production path: use Managed Identity (or the dev tool that DefaultAzureCredential
        // resolves to locally). Extract the blob endpoint URI from whichever form the caller
        // supplied so we don't accidentally hand the connection string's AccountKey to the SDK.
        var endpoint = connectionString.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? connectionString
            : ExtractStorageEndpoint(connectionString);

        return new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential());
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
