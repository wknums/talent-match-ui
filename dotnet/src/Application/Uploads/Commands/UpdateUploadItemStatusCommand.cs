using MediatR;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads.Services;

namespace TalentMatch.Application.Uploads.Commands;

public sealed record UpdateUploadItemStatusCommand(
    string SessionId,
    string ItemId,
    UpdateUploadItemStatusRequest Request,
    string CorrelationId) : IRequest<UploadItemDto>;

public sealed record HeartbeatUploadSessionCommand(
    string SessionId,
    int ExpectedConcurrencyVersion,
    string CorrelationId) : IRequest<UploadSessionSummaryDto>;

public sealed class UpdateUploadItemStatusCommandHandler(UploadItemLifecycleService service)
    : IRequestHandler<UpdateUploadItemStatusCommand, UploadItemDto>
{
    public Task<UploadItemDto> Handle(
        UpdateUploadItemStatusCommand command,
        CancellationToken cancellationToken) =>
        service.UpdateClientStatusAsync(
            command.SessionId,
            command.ItemId,
            command.Request,
            command.CorrelationId,
            cancellationToken);
}

public sealed class HeartbeatUploadSessionCommandHandler(UploadItemLifecycleService service)
    : IRequestHandler<HeartbeatUploadSessionCommand, UploadSessionSummaryDto>
{
    public Task<UploadSessionSummaryDto> Handle(
        HeartbeatUploadSessionCommand command,
        CancellationToken cancellationToken) =>
        service.HeartbeatAsync(
            command.SessionId,
            command.ExpectedConcurrencyVersion,
            command.CorrelationId,
            cancellationToken);
}
