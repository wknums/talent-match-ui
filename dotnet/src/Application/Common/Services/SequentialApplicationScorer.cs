using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Prompts.Services;

namespace TalentMatch.Application.Common.Services;

public sealed class SequentialApplicationScorer(
    ISender mediator,
    IApplicationRepository applications,
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
            PersistResults: false), ct);

        var finalized = await queue.FinalizeAsync(work.ApplicationId, work.Owner, async token =>
        {
            foreach (var run in result.Runs)
                await applications.AddScoringRunAsync(run, token);

            if (!string.IsNullOrWhiteSpace(result.CandidateName))
            {
                var application = await applications.GetByIdAsync(work.ApplicationId, token)
                    ?? throw new InvalidOperationException($"Application {work.ApplicationId} no longer exists.");
                application.CandidateName = result.CandidateName;
                await applications.UpdateAsync(application, token);
            }

            await finalizer.FinalizeAsync(
                work.ApplicationId, work.JobId, result.Runs, work.RunCount,
                work.VarianceThreshold, work.LonglistThreshold, token);
        }, ct);

        if (!finalized)
            throw new InvalidOperationException("Scoring ownership was lost before the result could be saved.");
    }
}
