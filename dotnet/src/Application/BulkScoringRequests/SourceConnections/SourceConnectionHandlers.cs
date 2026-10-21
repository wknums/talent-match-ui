using System.Net;
using MediatR;
using TalentMatch.Application.BulkScoringRequests.Services;

namespace TalentMatch.Application.BulkScoringRequests.SourceConnections;

public sealed record BeginSourceConnectionRequest(string FolderUrl, string? RetryOfRequestId = null);

public sealed record BeginSourceConnectionCommand(
    string JobId,
    BeginSourceConnectionRequest Request,
    Uri? CallbackUri = null,
    string? CorrelationId = null)
    : IRequest<BeginSourceConnectionResponse>;

public sealed record BeginSourceConnectionResponse(
    string ConnectionId,
    string Status,
    Uri AuthorizationUrl,
    DateTime ExpiresAt);

public sealed record SourceConnectionStart(
    string ProductActorId,
    string JobId,
    Uri FolderUrl,
    Uri CallbackUri,
    string? RetryOfRequestId,
    string CorrelationId);

public sealed record SourceConnectionCallbackRequest(
    string State,
    string? Code = null,
    string? Error = null,
    string? ErrorDescription = null);

public sealed record CompleteSourceConnectionCallbackCommand(
    SourceConnectionCallbackRequest Request,
    Uri? RedirectUri = null)
    : IRequest<SourceConnectionCallbackResult>;

public sealed record SourceConnectionCallback(
    string State,
    string? Code,
    string? Error,
    string? ErrorDescription,
    Uri RedirectUri);

public sealed record SourceConnectionCallbackResult(Uri RedirectUri);

public interface IBulkSourceClientAddress
{
    IPAddress GetAddress();
}

public interface ISourceConnectionWorkflow
{
    Task<int> CountUnexpiredPendingAsync(
        string productActorId,
        DateTime utcNow,
        CancellationToken cancellationToken);

    Task<BeginSourceConnectionResponse> BeginAsync(
        SourceConnectionStart start,
        CancellationToken cancellationToken);

    Task<SourceConnectionCallbackResult> CompleteCallbackAsync(
        SourceConnectionCallback callback,
        CancellationToken cancellationToken);
}

public sealed class BeginSourceConnectionHandler(
    BulkScoringAccess access,
    SourceLinkClassifier classifier,
    BulkSourceAdmissionService admission,
    ISourceConnectionWorkflow workflow,
    IBulkSourceClientAddress clientAddress,
    TimeProvider clock)
    : IRequestHandler<BeginSourceConnectionCommand, BeginSourceConnectionResponse>
{
    public async Task<BeginSourceConnectionResponse> Handle(
        BeginSourceConnectionCommand command,
        CancellationToken cancellationToken)
    {
        await access.RequireJobAsync(command.JobId, mutate: true, cancellationToken);
        string actorId;
        try
        {
            actorId = access.Actor;
        }
        catch (UnauthorizedAccessException)
        {
            throw BulkApiException.NotFound("The job was not found.");
        }
        var request = RequireShape(command.Request);

        if (!classifier.TryClassify(request.FolderUrl, out var link))
            throw BulkApiException.UnsupportedSource();

        var pending = await workflow.CountUnexpiredPendingAsync(
            actorId,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        if (pending >= access.Options.PendingConnectionsPerActor)
            throw AdmissionRejected(
                "source_connection_limit_reached",
                "Too many source connections are pending. Finish or abandon one before trying again.");

        if (!await admission.TryAdmitConnectionStartAsync(
                actorId,
                clientAddress.GetAddress(),
                pending,
                cancellationToken))
            throw AdmissionRejected(
                "source_connection_rate_limited",
                "Too many source connection attempts were made. Try again later.");

        return await workflow.BeginAsync(
            new SourceConnectionStart(
                actorId,
                command.JobId,
                link!.Uri,
                command.CallbackUri ?? new Uri("https://product.example/api/source-connections/callback"),
                request.RetryOfRequestId,
                Guid.TryParse(command.CorrelationId, out _)
                    ? command.CorrelationId!
                    : Guid.NewGuid().ToString()),
            cancellationToken);
    }

    private static BeginSourceConnectionRequest RequireShape(BeginSourceConnectionRequest? request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.FolderUrl)
            || request.FolderUrl.Length > 4096
            || !Uri.TryCreate(request.FolderUrl.Trim(), UriKind.Absolute, out _))
            throw BulkApiException.Invalid("A valid source folder URL is required.");

        if (request.RetryOfRequestId is not null
            && !Guid.TryParse(request.RetryOfRequestId, out _))
            throw BulkApiException.Invalid("The retry request identifier is invalid.");

        return request;
    }

    private static BulkApiException AdmissionRejected(string code, string message) =>
        new(429, code, message);
}

public sealed class CompleteSourceConnectionCallbackHandler(
    BulkSourceAdmissionService admission,
    ISourceConnectionWorkflow workflow,
    IBulkSourceClientAddress clientAddress)
    : IRequestHandler<CompleteSourceConnectionCallbackCommand, SourceConnectionCallbackResult>
{
    public async Task<SourceConnectionCallbackResult> Handle(
        CompleteSourceConnectionCallbackCommand command,
        CancellationToken cancellationToken)
    {
        if (!await admission.TryAdmitCallbackAsync(
                clientAddress.GetAddress(),
                cancellationToken))
            throw new BulkApiException(
                429,
                "source_callback_rate_limited",
                "Too many source authorization callbacks were received. Try again later.");

        var request = command.Request;
        if (request is null || string.IsNullOrWhiteSpace(request.State))
            throw BulkApiException.Invalid("The source connection callback state is required.");
        if (string.IsNullOrWhiteSpace(request.Code) && string.IsNullOrWhiteSpace(request.Error))
            throw BulkApiException.Invalid("The source connection callback result is invalid.");

        return await workflow.CompleteCallbackAsync(
            new SourceConnectionCallback(
                request.State,
                request.Code,
                request.Error,
                request.ErrorDescription,
                command.RedirectUri ?? new Uri("https://product.example/api/source-connections/callback")),
            cancellationToken);
    }
}
