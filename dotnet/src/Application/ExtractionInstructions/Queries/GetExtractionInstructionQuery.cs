using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.ExtractionInstructions.Models;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.ExtractionInstructions.Queries;

public sealed record GetExtractionInstructionQuery(string VersionId) : IRequest<ExtractionInstructionVersionDetailModel?>;

public sealed class GetExtractionInstructionQueryHandler : IRequestHandler<GetExtractionInstructionQuery, ExtractionInstructionVersionDetailModel?>
{
    private readonly IExtractionInstructionRepository _repository;
    private readonly ICurrentUserService _currentUser;

    public GetExtractionInstructionQueryHandler(
        IExtractionInstructionRepository repository,
        ICurrentUserService currentUser)
    {
        _repository = repository;
        _currentUser = currentUser;
    }

    public async Task<ExtractionInstructionVersionDetailModel?> Handle(GetExtractionInstructionQuery request, CancellationToken cancellationToken)
    {
        EnsureAdmin();
        var version = await _repository.GetByIdAsync(request.VersionId, cancellationToken);
        return version?.ToDetailModel();
    }

    private void EnsureAdmin()
    {
        if (!_currentUser.IsAdmin)
            throw new UnauthorizedAccessException("Only admins can manage extraction instructions.");
    }
}
