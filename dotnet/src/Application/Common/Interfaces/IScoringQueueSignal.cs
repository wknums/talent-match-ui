namespace TalentMatch.Application.Common.Interfaces;

public interface IScoringQueueSignal
{
    void Pulse();
    Task WaitAsync(CancellationToken ct);
}
