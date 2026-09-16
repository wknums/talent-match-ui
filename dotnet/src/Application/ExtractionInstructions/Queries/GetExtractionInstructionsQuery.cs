using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.ExtractionInstructions.Models;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.ExtractionInstructions.Queries;

public sealed record GetExtractionInstructionsQuery() : IRequest<IReadOnlyList<ExtractionInstructionVersionModel>>;

public sealed class GetExtractionInstructionsQueryHandler : IRequestHandler<GetExtractionInstructionsQuery, IReadOnlyList<ExtractionInstructionVersionModel>>
{
    private readonly IExtractionInstructionRepository _repository;
    private readonly ICurrentUserService _currentUser;

    public GetExtractionInstructionsQueryHandler(
        IExtractionInstructionRepository repository,
        ICurrentUserService currentUser)
    {
        _repository = repository;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<ExtractionInstructionVersionModel>> Handle(GetExtractionInstructionsQuery request, CancellationToken cancellationToken)
    {
        EnsureAdmin();
        var versions = await _repository.ListAsync(cancellationToken);
        return versions.Select(version => version.ToModel()).ToList();
    }

    private void EnsureAdmin()
    {
        if (!_currentUser.IsAdmin)
            throw new UnauthorizedAccessException("Only admins can manage extraction instructions.");
    }
}
