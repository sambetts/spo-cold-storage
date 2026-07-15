using Azure.Messaging.ServiceBus;
using Entities;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Utils;

namespace Migration.Engine;

/// <summary>
/// Cold-storage aware service-bus listener for the continuous-WebJob worker.
/// Owns the push <see cref="ServiceBusProcessor"/> plumbing and delegates the
/// actual parse/route/settle decision to <see cref="ColdStorageMessageProcessor"/>,
/// which is shared with the queue-triggered Azure Function so both hosts behave
/// identically.
/// </summary>
public sealed class ColdStorageBusListener : BaseComponent
{
    private readonly ServiceBusClient _sbClient;
    private readonly ServiceBusProcessor _processor;
    private readonly ColdStorageMessageProcessor _messageProcessor;

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
        _messageProcessor = new ColdStorageMessageProcessor(config, logger);
    }

    /// <summary>Fully-qualified Service Bus namespace the listener is attached to (diagnostics / heartbeat).</summary>
    public string ServiceBusNamespace => _sbClient.FullyQualifiedNamespace;

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
        var outcome = await _messageProcessor.ProcessMessageAsync(body, args.CancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case MessageOutcome.Complete:
                await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);
                break;
            case MessageOutcome.DeadLetter:
                await args.DeadLetterMessageAsync(args.Message).ConfigureAwait(false);
                break;
            default:
                await args.AbandonMessageAsync(args.Message).ConfigureAwait(false);
                break;
        }
    }

    private Task ErrorHandlerAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Service-bus processor error: {Message}", args.Exception.Message);
        return Task.CompletedTask;
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
