using System.Threading.Channels;

namespace Web.Services;

/// <summary>
/// In-process hand-off from the <c>migrations/start</c> request to the background
/// expander, so the request can return immediately. Durability across app restarts
/// comes from the persisted <c>MigrationJob.SubmissionRequestJson</c> + the expander
/// service's startup re-drive — this queue is only the fast path.
/// </summary>
public interface IMigrationSubmissionQueue
{
    void Enqueue(Guid jobId);
    ChannelReader<Guid> Reader { get; }
}

public sealed class MigrationSubmissionQueue : IMigrationSubmissionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);
}
