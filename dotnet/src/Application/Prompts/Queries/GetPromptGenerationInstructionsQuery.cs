using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Queries;

public sealed record GetPromptGenerationInstructionsQuery(string? JobId)
    : IRequest<IReadOnlyList<PromptGenerationInstruction>>;

public sealed class GetPromptGenerationInstructionsQueryHandler(
    IPromptGenerationInstructionRepository repository)
    : IRequestHandler<GetPromptGenerationInstructionsQuery, IReadOnlyList<PromptGenerationInstruction>>
{
    public Task<IReadOnlyList<PromptGenerationInstruction>> Handle(
        GetPromptGenerationInstructionsQuery request,
        CancellationToken cancellationToken)
        => repository.ListAsync(request.JobId, cancellationToken);
}
