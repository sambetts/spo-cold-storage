using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Entities;
using Entities.Configuration;
using Migration.Engine.Migration;
using Migration.Engine.Utils;
using Models;
using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
namespace Migration.Engine;

/// <summary>
/// Listens for new service bus messages for files to migrate to az blob
/// </summary>
public class ServiceBusMigrationListener : BaseComponent
{
    private readonly ServiceBusClient _sbClient;
    private readonly ServiceBusProcessor _receiver;
    private readonly ConcurrentBag<string> _ignoreDownloads = [];     // Files that are in progress of have errored
    private readonly object _lockObj = new();
    private int _filesProcessedFromQueue = 0;
    const int REPORT_QUEUE_LENGTH_EVERY = 10;

    public ServiceBusMigrationListener(Config config, ILogger ILogger) : base(config, ILogger)
    {
        _sbClient = ServiceBusClientFactory.Create(_config.ConnectionStrings.ServiceBus, _config);
        _receiver = _sbClient.CreateProcessor(_config.ServiceBusQueueName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 10,
            PrefetchCount = 0,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            MaxAutoLockRenewalDuration = TimeSpan.FromHours(24),        // Queue should be configured for 5 minute lock timeout
            AutoCompleteMessages = false                                // Messages are completed only when the migrator has succeeded to migrate the file
        });
    }

    public async Task ListenForFilesToMigrate()
    {
        try
        {
            // Start an initial DB session to avoid threads configuring context
            using (var db = new SPOColdStorageDbContext(_config))
            {
                await db.TargetSharePointSites.CountAsync();
            }

            // Add handler to process messages
            _receiver.ProcessMessageAsync += MessageHandler;

            // Add handler to process any errors
            _receiver.ProcessErrorAsync += ErrorHandler;

            // Start processing SB messages
            _logger.LogError($"Listening on service-bus '{_sbClient.FullyQualifiedNamespace}' for new files to migrate.");
            await _receiver.StartProcessingAsync();

            // Block infinitely
            while (true)
            {
                await Task.Delay(1000);
            }
        }
        finally
        {
            // Calling DisposeAsync on client types is required to ensure that network resources and other unmanaged objects are properly cleaned up.
            await _receiver.DisposeAsync();
            await _sbClient.DisposeAsync();
        }
    }

    // Handle received SB messages
    async Task MessageHandler(ProcessMessageEventArgs args)
    {
        string body = args.Message.Body.ToString();
        var msg = System.Text.Json.JsonSerializer.Deserialize<BaseSharePointFileInfo>(body);
        if (msg != null && msg.IsValidInfo)
        {
            _logger.LogInformation($"Started migration for: {msg.ServerRelativeFilePath}");

            // Message completed on success.
            await StartFileMigrationAsync(msg, args);

            lock (_lockObj)
            {
                _filesProcessedFromQueue++;
                if (_filesProcessedFromQueue % REPORT_QUEUE_LENGTH_EVERY == 0)
                {
                    _logger.LogInformation($"{_filesProcessedFromQueue} files processed...");
                }
            }
        }
        else
        {
            _logger.LogInformation($"Received unrecognised message: '{body}'. Sending to dead-letter queue.");
            await args.DeadLetterMessageAsync(args.Message);
        }
    }

    // Handle any errors when receiving SB messages
    Task ErrorHandler(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception.Message);
        _logger.LogError(args.Exception, "Unhandled exception");
        return Task.CompletedTask;
    }

    private async Task StartFileMigrationAsync(BaseSharePointFileInfo sharePointFileToMigrate, ProcessMessageEventArgs args)
    {
        string thisFileRef = sharePointFileToMigrate.FullSharePointUrl;
        if (_ignoreDownloads.Contains(thisFileRef))
        {
            _logger.LogWarning($"Already currently importing file '{sharePointFileToMigrate.FullSharePointUrl}'. Won't do it twice this session.");
            return;
        }

        _ignoreDownloads.Add(thisFileRef);

        // Begin migration on common class
        using var sharePointFileMigrator = new SharePointFileMigrator(_config, _logger);
        // Find/create SP context
        var app = await AuthUtils.GetNewClientApp(_config);

        long migratedFileSize = 0;
        bool success = false;
        try
        {
            // Start migration
            migratedFileSize = await sharePointFileMigrator.MigrateFromSharePointToBlobStorage(sharePointFileToMigrate, app);
            success = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            _logger.LogError($"ERROR: Got fatal error '{ex.Message}' importing file '{sharePointFileToMigrate.FullSharePointUrl}'. Will try again");

            await sharePointFileMigrator.SaveErrorForFileMigrationToSql(ex, sharePointFileToMigrate);
#if DEBUG
            throw;
#endif
        }
        finally
        {
            // Import done/failed - remove from list of current imports
            if (!_ignoreDownloads.TryTake(out thisFileRef!))
            {
                _logger.LogWarning($"Error removing file '{sharePointFileToMigrate.FullSharePointUrl}' from list of concurrent operations. Not sure what to do.");
            }
        }

        if (success)
        {
            // Complete the message. messages is deleted from the queue. 
            try
            {
                await args.CompleteMessageAsync(args.Message);
                _logger.LogInformation($"'{sharePointFileToMigrate.ServerRelativeFilePath}' ({migratedFileSize:N0} bytes) migrated succesfully.");

            }
            catch (ServiceBusException ex)
            {
                base._logger.LogError(ex, "Unhandled exception");
                base._logger.LogInformation("Couldn't complete SB message: " + ex.Message);
#if DEBUG
                throw;
#endif
            }
            await sharePointFileMigrator.SaveSucessfulFileMigrationToSql(sharePointFileToMigrate);
        }
        else
        {
            // Leave for processing later
            await args.AbandonMessageAsync(args.Message);
        }
    }

}
