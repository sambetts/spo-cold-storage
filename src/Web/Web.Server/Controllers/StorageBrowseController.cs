using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Entities.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Migration.Engine.Utils;
using Web.Models.Api;

namespace Web.Controllers;

/// <summary>
/// Server-side proxy for browsing and downloading cold-storage blobs.
///
/// The SPA used to talk to Azure Blob Storage directly from the browser using the
/// signed-in user's storage token. That no longer works because the storage
/// account's <c>publicNetworkAccess</c> is disabled by governance policy — the
/// account is only reachable over the private endpoint the Web App is integrated
/// with. (Shared-key/account SAS is disabled too.) These endpoints therefore list
/// and stream blobs using the Web App's managed identity over that private
/// endpoint, so the browser only ever talks to our own API.
///
/// Authorization: the endpoints require an authenticated caller (the same bearer
/// token the SPA already uses for the rest of the API). This mirrors the original
/// browse model, where any authenticated app user could enumerate the default
/// container.
/// </summary>
[Authorize]
[ApiController]
[Route("api/storage")]
public class StorageBrowseController(Config config, ILogger<StorageBrowseController> logger) : ControllerBase
{
    private readonly Config _config = config;
    private readonly ILogger<StorageBrowseController> _logger = logger;

    private BlobContainerClient GetContainerClient()
    {
        // RBAC path (config != null) => DefaultAzureCredential (App Service managed
        // identity), which reaches the account over the private endpoint.
        var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
        return serviceClient.GetBlobContainerClient(_config.BlobContainerName);
    }

    /// <summary>
    /// <c>GET /api/storage/blobs?prefix=</c> — hierarchical listing (folders + files)
    /// of the default cold-storage container under the supplied prefix.
    /// </summary>
    [HttpGet("blobs")]
    public async Task<ActionResult<StorageListingResponse>> ListAsync([FromQuery] string? prefix, CancellationToken cancellationToken)
    {
        var container = GetContainerClient();
        var result = new StorageListingResponse
        {
            Container = _config.BlobContainerName,
            Prefix = prefix ?? string.Empty,
        };

        try
        {
            await foreach (var item in container.GetBlobsByHierarchyAsync(
                BlobTraits.None, BlobStates.None, "/", prefix, cancellationToken).ConfigureAwait(false))
            {
                if (item.IsPrefix)
                {
                    result.Folders.Add(item.Prefix);
                }
                else if (item.IsBlob)
                {
                    result.Files.Add(new StorageBlobEntry
                    {
                        Name = item.Blob.Name,
                        Size = item.Blob.Properties.ContentLength ?? 0,
                        LastModified = item.Blob.Properties.LastModified,
                    });
                }
            }
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to list blobs in container {Container} (prefix '{Prefix}').",
                _config.BlobContainerName, prefix);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Storage listing failed ({ex.ErrorCode ?? ex.Status.ToString()})." });
        }

        return result;
    }

    /// <summary>
    /// <c>GET /api/storage/download?blob=</c> — streams a single blob back to the
    /// caller as a file attachment. The bytes flow through the Web App (private
    /// endpoint) because the browser cannot reach the storage account directly.
    /// </summary>
    [HttpGet("download")]
    public async Task<IActionResult> DownloadAsync([FromQuery] string blob, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blob))
        {
            return BadRequest(new { error = "The 'blob' query parameter is required." });
        }

        var blobClient = GetContainerClient().GetBlobClient(blob);
        try
        {
            var props = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var stream = await blobClient.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), cancellationToken).ConfigureAwait(false);

            var fileName = System.IO.Path.GetFileName(blob);
            var contentType = string.IsNullOrEmpty(props.Value.ContentType)
                ? "application/octet-stream"
                : props.Value.ContentType;

            _logger.LogInformation("Streaming cold-storage blob '{Blob}' ({Size} bytes) to {Upn}.",
                blob, props.Value.ContentLength, User.Identity?.Name ?? "(unknown)");

            return File(stream, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return NotFound(new { error = "Blob not found." });
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to download blob '{Blob}'.", blob);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Storage download failed ({ex.ErrorCode ?? ex.Status.ToString()})." });
        }
    }
}
