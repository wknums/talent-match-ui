using MediatR;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads.Services;

namespace TalentMatch.Application.Uploads.Queries;

public sealed record ListOwnedUploadSessionsQuery(
    string? JobId,
    bool IncludeTerminal = true) : IRequest<IReadOnlyList<UploadSessionSummaryDto>>;

public sealed record GetUploadSessionQuery(
    string SessionId) : IRequest<UploadSessionDetailDto?>;

public sealed class ListOwnedUploadSessionsQueryHandler(UploadItemLifecycleService service)
    : IRequestHandler<ListOwnedUploadSessionsQuery, IReadOnlyList<UploadSessionSummaryDto>>
{
    public Task<IReadOnlyList<UploadSessionSummaryDto>> Handle(
        ListOwnedUploadSessionsQuery request,
        CancellationToken cancellationToken) =>
        service.ListOwnedAsync(request.JobId, request.IncludeTerminal, cancellationToken);
}

public sealed class GetUploadSessionQueryHandler(UploadItemLifecycleService service)
    : IRequestHandler<GetUploadSessionQuery, UploadSessionDetailDto?>
{
    public Task<UploadSessionDetailDto?> Handle(
        GetUploadSessionQuery request,
        CancellationToken cancellationToken) =>
        service.GetOwnedAsync(request.SessionId, cancellationToken);
}
