using Azure.Storage.Blobs;
using Entities.Configuration;
using Migration.Engine.Utils;
using Models;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Migration;
/// <summary>
/// Uploads files from local file-system to Azure blob
/// </summary>
public class BlobStorageUploader : BaseComponent
{
    private readonly BlobServiceClient _blobServiceClient;
    private BlobContainerClient? _containerClient;
    public BlobStorageUploader(Config config, ILogger ILogger) : base(config, ILogger)
    {
        // Create BlobServiceClient with appropriate authentication based on connection string type
        _blobServiceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
    }

    public async Task UploadFileToAzureBlob(string localTempFileName, BaseSharePointFileInfo msg)
    {
        // Create the container and return a container client object
        if (_containerClient == null)
        {
            this._containerClient = _blobServiceClient.GetBlobContainerClient(_config.BlobContainerName);
        }

        _logger.LogDebug($"Uploading '{msg.ServerRelativeFilePath}' to blob storage...");
        using var fs = File.OpenRead(localTempFileName);
        var fileRef = _containerClient.GetBlobClient(msg.ServerRelativeFilePath);
        var fileExists = await fileRef.ExistsAsync();
        if (fileExists)
        {
            // MD5 has the local file
            byte[] tempFileHash;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                using var tempFileStream = File.OpenRead(localTempFileName);
                tempFileHash = md5.ComputeHash(tempFileStream);
            }

            // Get az blob MD5 & compare
            var existingProps = await fileRef.GetPropertiesAsync();

            // For some reason, sometimes the SP hash is null
            var match = existingProps.Value.ContentHash != null && existingProps.Value.ContentHash.SequenceEqual(tempFileHash);
            if (!match)
                await fileRef.UploadAsync(fs, true);
            else
                _logger.LogDebug($"Skipping '{msg.ServerRelativeFilePath}' as destination hash is identical to local file.");
        }
        else
            await _containerClient.UploadBlobAsync(msg.ServerRelativeFilePath, fs);
    }
}
