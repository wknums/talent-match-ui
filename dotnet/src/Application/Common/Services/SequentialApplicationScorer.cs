using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Prompts.Services;

namespace TalentMatch.Application.Common.Services;

public sealed class SequentialApplicationScorer(
    ISender mediator,
    ISequentialScoringQueueRepository queue,
    IApplicationScoringFinalizer finalizer,
    IScoringPromptRepository? prompts = null,
    IPromptProfileGuard? profileGuard = null) : ISequentialApplicationScorer
{
    public async Task ScoreAsync(SequentialScoringWork work, CancellationToken ct)
    {
        if (prompts is not null && profileGuard is not null)
        {
            var prompt = await prompts.GetByIdAsync(work.PromptVersionId, ct)
                ?? throw new InvalidOperationException($"Scoring prompt {work.PromptVersionId} not found.");
            await profileGuard.EnsureProductionReadyAsync(prompt, ct);
        }

        var result = await mediator.Send(new ScoreApplicationCommand(
            work.ApplicationId, work.JobId, work.RunCount,
            work.PromptVersionId, work.JobDescription, work.RubricJson,
            PersistResults: false,
            ScoringConfigVersionId: work.ScoringConfigVersionId), ct);

        var prepared = finalizer.Prepare(
            work.ApplicationId, result.Runs, work.RunCount,
            work.VarianceThreshold, work.LonglistThreshold, result.CandidateName);
        var finalized = await queue.FinalizeAsync(work.ApplicationId, work.Owner, prepared, ct);

        if (!finalized)
            throw new InvalidOperationException("Scoring ownership was lost before the result could be saved.");
    }
}
