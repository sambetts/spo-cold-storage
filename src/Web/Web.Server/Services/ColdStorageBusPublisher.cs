using Azure.Messaging.ServiceBus;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Utils;
using Models.ColdStorage;
using System.Text.Json;

namespace Web.Services;

/// <summary>
/// Enqueues cold-storage envelope messages onto the file-discovery service-bus
/// queue. Wraps <see cref="ServiceBusSender"/> in a singleton so controllers
/// don't pay per-request connection cost.
/// </summary>
public interface IColdStorageBusPublisher : IAsyncDisposable
{
    Task PublishAsync(ColdStorageBusEnvelope envelope, CancellationToken cancellationToken = default);
}

public sealed class ColdStorageBusPublisher : IColdStorageBusPublisher
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ColdStorageBusPublisher> _logger;

    public ColdStorageBusPublisher(Config config, ILogger<ColdStorageBusPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        var json = JsonSerializer.Serialize(envelope);
        var message = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            CorrelationId = envelope.JobId.ToString(),
            MessageId = envelope.ItemId.ToString(),
            Subject = envelope.Operation.ToString(),
        };
        await _sender.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Published {Operation} envelope job={JobId} item={ItemId} container={Container}.",
            envelope.Operation, envelope.JobId, envelope.ItemId, envelope.ContainerName);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
