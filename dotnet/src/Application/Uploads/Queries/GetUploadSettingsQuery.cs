using MediatR;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Uploads.Queries;

public sealed record GetUploadSettingsQuery : IRequest<UploadSettingsDto>;

public sealed class GetUploadSettingsQueryHandler(IUploadSettingsRepository repository)
    : IRequestHandler<GetUploadSettingsQuery, UploadSettingsDto>
{
    public async Task<UploadSettingsDto> Handle(
        GetUploadSettingsQuery request,
        CancellationToken cancellationToken)
    {
        var persisted = await repository.GetPersistedAsync(cancellationToken);
        var settings = persisted ?? UploadSettings.Defaults();
        return new(
            settings.FileConcurrency,
            settings.MaxIndividualFileBytes,
            settings.MaxInFlightBytes,
            settings.ConcurrencyVersion,
            persisted is not null,
            persisted?.UpdatedAt,
            persisted?.UpdatedBy);
    }
}
