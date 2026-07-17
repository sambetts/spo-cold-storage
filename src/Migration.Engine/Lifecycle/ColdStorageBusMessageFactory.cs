using Azure.Messaging.ServiceBus;
using Entities.DBEntities.ColdStorage;
using Models;
using Models.ColdStorage;
using System.Text.Json;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Single place that turns a <see cref="ColdStorageBusEnvelope"/> into a
/// <see cref="ServiceBusMessage"/> and (for the reconciler) rebuilds an envelope
/// from a persisted <see cref="MigrationJobItem"/>. Shared by the web publisher and
/// the worker's dispatch reconciler so message shape and batching stay identical.
/// </summary>
public static class ColdStorageBusMessageFactory
{
    /// <summary>
    /// Serialises an envelope into a queue message. <see cref="ServiceBusMessage.MessageId"/>
    /// is the item id and <see cref="ServiceBusMessage.CorrelationId"/> the job id so the
    /// message is traceable back to its lifecycle row.
    /// </summary>
    public static ServiceBusMessage BuildMessage(ColdStorageBusEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var json = JsonSerializer.Serialize(envelope);
        return new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            CorrelationId = envelope.JobId.ToString(),
            MessageId = envelope.ItemId.ToString(),
            Subject = envelope.Operation.ToString(),
        };
    }

    /// <summary>
    /// Publishes many envelopes, packing them into as few Service Bus batches as
    /// possible. Invalid envelopes are skipped. Returns the number of messages sent.
    /// </summary>
    public static async Task<int> SendManyAsync(
        ServiceBusSender sender,
        IReadOnlyList<ColdStorageBusEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(envelopes);

        var valid = new List<ColdStorageBusEnvelope>(envelopes.Count);
        foreach (var e in envelopes)
        {
            if (e is not null && e.IsValid)
            {
                valid.Add(e);
            }
        }
        if (valid.Count == 0)
        {
            return 0;
        }

        var sent = 0;
        var i = 0;
        while (i < valid.Count)
        {
            using var batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
            do
            {
                var message = BuildMessage(valid[i]);
                if (!batch.TryAddMessage(message))
                {
                    if (batch.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"Cold-storage envelope for item {valid[i].ItemId} is too large for a Service Bus batch.");
                    }
                    break; // batch full — send what we have, continue with a fresh batch
                }
                i++;
            }
            while (i < valid.Count);

            await sender.SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
            sent += batch.Count;
        }
        return sent;
    }

    /// <summary>
    /// Rebuilds the bus envelope for a persisted item so the reconciler can
    /// re-publish an item whose original message was never sent. Returns null if the
    /// item does not carry enough information to form a valid envelope.
    /// </summary>
    public static ColdStorageBusEnvelope? BuildEnvelopeFromItem(MigrationJobItem item, MigrationJob job)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(job);

        var webUrl = string.IsNullOrEmpty(item.SpWebUrl) ? item.SpSiteUrl : item.SpWebUrl;
        var envelope = new ColdStorageBusEnvelope
        {
            JobId = item.JobId,
            ItemId = item.ItemId,
            Operation = job.Operation,
            ContainerName = item.BlobContainerName ?? string.Empty,
            RequestedByUpn = job.RequestedByUpn,
            Recursive = item.Recursive,
        };

        if (job.Operation == MigrationOperationKind.Migrate)
        {
            envelope.File = new BaseSharePointFileInfo
            {
                SiteUrl = item.SpSiteUrl,
                WebUrl = webUrl,
                ServerRelativeFilePath = item.SpServerRelativeUrl,
                LastModified = item.SourceLastModified ?? DateTime.UtcNow,
                FileSize = item.FileSize,
            };
        }
        else
        {
            envelope.RestoreTarget = new PlaceholderRestoreTarget
            {
                SiteUrl = item.SpSiteUrl,
                WebUrl = webUrl,
                PlaceholderServerRelativeUrl = item.PlaceholderServerRelativeUrl ?? string.Empty,
                OriginalServerRelativeUrl = item.SpServerRelativeUrl,
            };
        }

        return envelope.IsValid ? envelope : null;
    }
}
