using Azure.Messaging.ServiceBus;
using Entities.Configuration;
using Migration.Engine.Utils;
using Models.ColdStorage;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Publishes cold-storage envelopes onto the file-discovery queue. Used by the
/// worker's dispatch reconciler to re-drive items whose original message was never
/// sent. Wraps a single <see cref="ServiceBusSender"/> so callers can batch cheaply.
/// </summary>
public interface IColdStorageQueuePublisher : IAsyncDisposable
{
    Task PublishAsync(ColdStorageBusEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules an envelope to be enqueued at <paramref name="enqueueTimeUtc"/> (a Service Bus
    /// scheduled message) so a throttled item resumes exactly when its Retry-After / backoff
    /// elapses, without depending on a background timer being awake. Returns the scheduled
    /// sequence number.
    /// </summary>
    Task<long> ScheduleAsync(ColdStorageBusEnvelope envelope, DateTimeOffset enqueueTimeUtc, CancellationToken cancellationToken = default);

    /// <summary>Publishes many envelopes in as few batches as possible; returns the count sent.</summary>
    Task<int> PublishManyAsync(IReadOnlyList<ColdStorageBusEnvelope> envelopes, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ColdStorageQueuePublisher : IColdStorageQueuePublisher
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public ColdStorageQueuePublisher(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _client = ServiceBusClientFactory.Create(config.ConnectionStrings.ServiceBus, config);
        _sender = _client.CreateSender(config.ServiceBusQueueName);
    }

    public async Task PublishAsync(ColdStorageBusEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.IsValid)
        {
            throw new InvalidOperationException("Refusing to publish invalid cold-storage envelope.");
        }
        await _sender.SendMessageAsync(ColdStorageBusMessageFactory.BuildMessage(envelope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> ScheduleAsync(ColdStorageBusEnvelope envelope, DateTimeOffset enqueueTimeUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.IsValid)
        {
            throw new InvalidOperationException("Refusing to schedule invalid cold-storage envelope.");
        }
        var message = ColdStorageBusMessageFactory.BuildMessage(envelope);
        // Use a unique MessageId for the retry so that, if the queue ever has duplicate
        // detection enabled, the retry isn't silently dropped as a duplicate of the original
        // message (which shares the item id). The body still carries the item/job ids.
        message.MessageId = $"{envelope.ItemId}:retry:{DateTime.UtcNow.Ticks}";
        return await _sender.ScheduleMessageAsync(message, enqueueTimeUtc, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> PublishManyAsync(IReadOnlyList<ColdStorageBusEnvelope> envelopes, CancellationToken cancellationToken = default)
        => ColdStorageBusMessageFactory.SendManyAsync(_sender, envelopes, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
