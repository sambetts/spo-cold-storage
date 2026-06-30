using Azure.Messaging.ServiceBus;
using Entities;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Migration;
using Migration.Engine.Restore;
using Migration.Engine.Utils;
using Models;
using Models.ColdStorage;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Migration.Engine;

/// <summary>
/// Cold-storage aware service-bus listener. Routes messages to the migrate or
/// restore pipeline based on the <see cref="ColdStorageBusEnvelope.Operation"/>
/// discriminator. Falls back to the legacy <see cref="BaseSharePointFileInfo"/>
/// payload so existing indexer-driven migrations continue to work.
/// </summary>
public sealed class ColdStorageBusListener : BaseComponent
{
    private readonly ServiceBusClient _sbClient;
    private readonly ServiceBusProcessor _processor;
    private readonly ConcurrentDictionary<Guid, byte> _inFlightItems = new();
    private readonly ConcurrentDictionary<string, byte> _legacyInFlight = new(StringComparer.OrdinalIgnoreCase);
    // Serialises concurrent restores of the SAME placeholder on this worker so a
    // second in-flight restore can't double-upload (issue #10). Cross-process
    // restores are additionally coalesced by the pipeline's DB status guard.
    private readonly ConcurrentDictionary<string, byte> _inFlightRestorePlaceholders = new(StringComparer.OrdinalIgnoreCase);

    public ColdStorageBusListener(Config config, ILogger logger) : base(config, logger)
    {
        _sbClient = ServiceBusClientFactory.Create(_config.ConnectionStrings.ServiceBus, _config);
        _processor = _sbClient.CreateProcessor(_config.ServiceBusQueueName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 10,
            PrefetchCount = 0,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            MaxAutoLockRenewalDuration = TimeSpan.FromHours(24),
            AutoCompleteMessages = false,
        });
    }

    public async Task ListenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using (var db = new SPOColdStorageDbContext(_config))
            {
                await db.TargetSharePointSites.CountAsync(cancellationToken).ConfigureAwait(false);
            }

            _processor.ProcessMessageAsync += MessageHandlerAsync;
            _processor.ProcessErrorAsync += ErrorHandlerAsync;

            _logger.LogInformation("Listening for cold-storage messages on '{Namespace}'.", _sbClient.FullyQualifiedNamespace);
            await _processor.StartProcessingAsync(cancellationToken).ConfigureAwait(false);

            await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            await _sbClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task MessageHandlerAsync(ProcessMessageEventArgs args)
    {
        var body = args.Message.Body.ToString();
        ColdStorageBusEnvelope? envelope = TryDeserialiseEnvelope(body);

        if (envelope is not null && envelope.IsValid)
        {
            await ProcessEnvelopeAsync(envelope, args).ConfigureAwait(false);
            return;
        }

        // Backward compatibility: indexer continues to publish raw file-info.
        var legacy = TryDeserialiseLegacy(body);
        if (legacy is not null && legacy.IsValidInfo)
        {
            await ProcessLegacyAsync(legacy, args).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning("Unrecognised cold-storage bus message; sending to dead-letter queue. Body length={Length}", body.Length);
        await args.DeadLetterMessageAsync(args.Message).ConfigureAwait(false);
    }

    private async Task ProcessEnvelopeAsync(ColdStorageBusEnvelope envelope, ProcessMessageEventArgs args)
    {
        // Per-worker placeholder lock for restores: defer a second concurrent
        // restore of the same placeholder so it can't run alongside the first.
        var placeholderKey = envelope.Operation == MigrationOperationKind.Restore
            ? envelope.RestoreTarget?.PlaceholderServerRelativeUrl
            : null;
        if (placeholderKey is not null && !_inFlightRestorePlaceholders.TryAdd(placeholderKey, 0))
        {
            _logger.LogWarning("Restore for placeholder '{Key}' already in flight on this worker; deferring message.", placeholderKey);
            await args.AbandonMessageAsync(args.Message).ConfigureAwait(false);
            return;
        }

        if (!_inFlightItems.TryAdd(envelope.ItemId, 0))
        {
            _logger.LogWarning("Item {ItemId} already in flight on this worker; deferring message.", envelope.ItemId);
            if (placeholderKey is not null)
            {
                _inFlightRestorePlaceholders.TryRemove(placeholderKey, out _);
            }
            await args.AbandonMessageAsync(args.Message).ConfigureAwait(false);
            return;
        }

        bool success;
        try
        {
            using var db = new SPOColdStorageDbContext(_config);
            var writer = new JobStatusWriter(db, _logger);

            // Honour admin queue control (issue #16): if the item was cancelled or
            // already finished after the message was enqueued, do no work and let
            // the message complete.
            var current = await writer.FindItemAsync(envelope.ItemId, args.CancellationToken).ConfigureAwait(false);
            if (current is not null && current.Status.IsTerminal())
            {
                _logger.LogInformation("Item {ItemId} is already {Status} (e.g. admin-cancelled); skipping.", envelope.ItemId, current.Status);
                success = true;
            }
            else if (envelope.Operation == MigrationOperationKind.Migrate)
            {
                var pipeline = new ColdStorageMigratorPipeline(_config, _logger, writer);
                var app = await AuthUtils.GetNewClientApp(_config).ConfigureAwait(false);
                success = await pipeline.ProcessAsync(envelope, app, args.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                var pipeline = new SharePointRestorePipeline(_config, _logger, writer);
                success = await pipeline.ProcessAsync(envelope, args.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled cold-storage pipeline error for item {ItemId}.", envelope.ItemId);
            success = false;
        }
        finally
        {
            _inFlightItems.TryRemove(envelope.ItemId, out _);
            if (placeholderKey is not null)
            {
                _inFlightRestorePlaceholders.TryRemove(placeholderKey, out _);
            }
        }

        if (success)
        {
            await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);
        }
        else
        {
            await args.AbandonMessageAsync(args.Message).ConfigureAwait(false);
        }
    }

    private async Task ProcessLegacyAsync(BaseSharePointFileInfo legacy, ProcessMessageEventArgs args)
    {
        var key = legacy.FullSharePointUrl;
        if (!_legacyInFlight.TryAdd(key, 0))
        {
            _logger.LogWarning("Legacy file '{Url}' already in flight; deferring message.", key);
            await args.AbandonMessageAsync(args.Message).ConfigureAwait(false);
            return;
        }
        try
        {
            using var sharePointFileMigrator = new SharePointFileMigrator(_config, _logger);
            var app = await AuthUtils.GetNewClientApp(_config).ConfigureAwait(false);
            try
            {
                _ = await sharePointFileMigrator.MigrateFromSharePointToBlobStorage(legacy, app).ConfigureAwait(false);
                await sharePointFileMigrator.SaveSucessfulFileMigrationToSql(legacy).ConfigureAwait(false);
                await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy migration failed for '{Url}'.", key);
                await sharePointFileMigrator.SaveErrorForFileMigrationToSql(ex, legacy).ConfigureAwait(false);
                await args.AbandonMessageAsync(args.Message).ConfigureAwait(false);
            }
        }
        finally
        {
            _legacyInFlight.TryRemove(key, out _);
        }
    }

    private Task ErrorHandlerAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Service-bus processor error: {Message}", args.Exception.Message);
        return Task.CompletedTask;
    }

    private static ColdStorageBusEnvelope? TryDeserialiseEnvelope(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ColdStorageBusEnvelope>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BaseSharePointFileInfo? TryDeserialiseLegacy(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<BaseSharePointFileInfo>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Expected on cancellation.
        }
    }
}
