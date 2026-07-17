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

    public Task<int> PublishManyAsync(IReadOnlyList<ColdStorageBusEnvelope> envelopes, CancellationToken cancellationToken = default)
        => ColdStorageBusMessageFactory.SendManyAsync(_sender, envelopes, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
