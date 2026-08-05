using MediatR;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Navigation;

public enum NavigationShellAction
{
    Collapse,
    Expand,
}

public sealed record RecordNavigationAuditRequestDto(
    string? Action,
    string? CorrelationId,
    DateTimeOffset? RequestedAt);

public sealed record NavigationAuditResultDto(string CorrelationId);

/// <summary>Actor identity is resolved from the validated token, never from the request body.</summary>
public sealed record RecordNavigationAuditCommand(
    string ActorObjectId,
    RecordNavigationAuditRequestDto Request) : IRequest<NavigationAuditResultDto>;

public sealed class NavigationAuditValidationException(string message) : Exception(message);

public sealed class NavigationAuditUnavailableException(string message, Exception inner)
    : Exception(message, inner);

public sealed class RecordNavigationAuditCommandHandler(INavigationAuditRepository repository)
    : IRequestHandler<RecordNavigationAuditCommand, NavigationAuditResultDto>
{
    public async Task<NavigationAuditResultDto> Handle(
        RecordNavigationAuditCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActorObjectId))
            throw new NavigationAuditValidationException("The navigation actor could not be resolved.");

        var action = ParseAction(request.Request.Action);
        var correlationId = ParseCorrelationId(request.Request.CorrelationId);
        var requestedAt = request.Request.RequestedAt ?? DateTimeOffset.UtcNow;

        try
        {
            await repository.RecordAsync(
                new NavigationAuditEntry(
                    request.ActorObjectId,
                    action.ToString().ToLowerInvariant(),
                    correlationId.ToString(),
                    requestedAt),
                cancellationToken);
        }
        catch (Exception error)
        {
            throw new NavigationAuditUnavailableException(
                "The navigation audit could not be recorded.", error);
        }

        return new NavigationAuditResultDto(correlationId.ToString());
    }

    private static NavigationShellAction ParseAction(string? action) => action?.Trim().ToLowerInvariant() switch
    {
        "collapse" => NavigationShellAction.Collapse,
        "expand" => NavigationShellAction.Expand,
        _ => throw new NavigationAuditValidationException("The navigation action is not supported."),
    };

    private static Guid ParseCorrelationId(string? correlationId)
        => Guid.TryParse(correlationId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new NavigationAuditValidationException("The navigation correlation id is not valid.");
}
