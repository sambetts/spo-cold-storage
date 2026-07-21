using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Models.ColdStorage;

namespace Migration.Engine.Providers;

/// <summary>
/// The single place the transfer pipelines decide what to do with a step failure — extracted so
/// migrate and restore share exactly one implementation of the hard-won throttling policy instead
/// of two copies that can drift.
///
/// A <b>transient</b> failure (throttle / timeout / transient 5xx / I/O blip, per
/// <see cref="TransientErrorClassifier"/> — which also honours a <see cref="TransferProviderException"/>'s
/// own classification) that is still under the attempt ceiling parks the item in the non-terminal
/// <see cref="MigrationLifecycleStatus.RetryScheduled"/> status with a concrete <c>NextRetryAt</c>
/// (the server's Retry-After when known, else the exponential backoff), so the message processor
/// schedules an automatic bus retry. A permanent failure — or a transient one that has exhausted
/// its attempts — transitions to the caller's terminal status.
/// </summary>
public sealed class StepFailureHandler(TransferPipelineOptions options, ILogger logger, IJobStatusWriter statusWriter)
{
    private readonly TransferPipelineOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IJobStatusWriter _statusWriter = statusWriter ?? throw new ArgumentNullException(nameof(statusWriter));

    public async Task HandleAsync(
        Guid itemId,
        Exception ex,
        MigrationLifecycleStatus terminalStatus,
        string terminalMessage,
        CancellationToken cancellationToken)
    {
        if (TransientErrorClassifier.IsTransient(ex))
        {
            var attempts = await _statusWriter.IncrementAttemptsAsync(itemId, cancellationToken).ConfigureAwait(false);
            var maxAttempts = _options.MaxProcessAttempts;
            if (attempts < maxAttempts)
            {
                var backoffSeconds = ThrottleBackoff.SecondsFor(attempts, _options.ThrottleBackoffBaseSeconds, _options.ThrottleBackoffMaxSeconds);
                var retryAfterSeconds = ThrottleInfo.TryGetRetryAfterSeconds(ex);
                var waitSeconds = Math.Clamp(Math.Max(backoffSeconds, retryAfterSeconds ?? 0), 1, 3600);
                var nextRetryUtc = DateTime.UtcNow.AddSeconds(waitSeconds);
                var reason = retryAfterSeconds is int ra
                    ? $"SharePoint or Azure is busy and throttled the request (asked to wait {ra}s). It will be retried automatically at {nextRetryUtc:HH:mm:ss} UTC (attempt {attempts + 1} of {maxAttempts})."
                    : $"Throttled or hit a transient error; it will be retried automatically at {nextRetryUtc:HH:mm:ss} UTC (attempt {attempts + 1} of {maxAttempts}).";
                await _statusWriter.ScheduleRetryAsync(itemId, nextRetryUtc, retryAfterSeconds, reason, ex, cancellationToken).ConfigureAwait(false);
                return;
            }
            await _statusWriter.TransitionAsync(itemId, terminalStatus,
                $"Gave up after {attempts} throttled/transient attempts. {terminalMessage}",
                exception: ex, level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        await _statusWriter.TransitionAsync(itemId, terminalStatus, terminalMessage,
            exception: ex, level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
