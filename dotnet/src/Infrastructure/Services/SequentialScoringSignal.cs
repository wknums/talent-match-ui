using System.Threading.Channels;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.Services;

public sealed class SequentialScoringSignal : IScoringQueueSignal
{
    // The database holds the work. Repeated wakeups can safely be coalesced.
    private readonly Channel<bool> _wakeups = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Pulse() => _wakeups.Writer.TryWrite(true);
    public async Task WaitAsync(CancellationToken ct) => await _wakeups.Reader.ReadAsync(ct);
}
