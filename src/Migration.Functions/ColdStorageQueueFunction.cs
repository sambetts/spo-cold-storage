using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Migration.Engine;

namespace Migration.Functions;

/// <summary>
/// Queue-triggered cold-storage worker. The Functions runtime wakes this on each
/// message on the <c>filediscovery</c> Service Bus queue (no Always On needed),
/// then hands the raw body to the shared <see cref="ColdStorageMessageProcessor"/>
/// — the same code the continuous-WebJob listener runs — and settles the message
/// according to the returned <see cref="MessageOutcome"/>.
///
/// Manual settlement (<c>autoCompleteMessages: false</c> in host.json) is required
/// so we can dead-letter unparseable messages exactly like the WebJob does.
/// </summary>
public class ColdStorageQueueFunction(ColdStorageMessageProcessor processor, ILogger<ColdStorageQueueFunction> logger)
{
    private readonly ColdStorageMessageProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly ILogger<ColdStorageQueueFunction> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [Function("ColdStorageQueue")]
    public async Task RunAsync(
        [ServiceBusTrigger("%ServiceBusQueueName%", Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(messageActions);

        var body = message.Body.ToString();
        _logger.LogInformation(
            "Received cold-storage message id={MessageId} subject={Subject} deliveryCount={DeliveryCount}.",
            message.MessageId, message.Subject, message.DeliveryCount);

        var outcome = await _processor.ProcessMessageAsync(body, cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case MessageOutcome.Complete:
                await messageActions.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case MessageOutcome.DeadLetter:
                await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "UnrecognisedColdStorageMessage", cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            default:
                // Abandon → Service Bus redelivers (up to maxDeliveryCount, then it
                // dead-letters automatically), matching the WebJob's retry behaviour.
                await messageActions.AbandonMessageAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
