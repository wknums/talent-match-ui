using MediatR;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests;

public sealed record ListBulkScoringRequestsQuery(string JobId, int Page, int PageSize) : IRequest<BulkRequestPageDto>;
public sealed record ListBulkScoringNotificationsQuery : IRequest<IReadOnlyList<BulkTerminalNotificationDto>>;
public sealed record GetBulkScoringOriginalQuery(string RequestId, string ItemId) : IRequest<BulkOriginalFile>;

public sealed class ListBulkScoringRequestsHandler(BulkScoringAccess access, IBulkScoringRepository repository)
    : IRequestHandler<ListBulkScoringRequestsQuery, BulkRequestPageDto>
{
    public async Task<BulkRequestPageDto> Handle(ListBulkScoringRequestsQuery query, CancellationToken ct)
    {
        var job = await access.RequireJobAsync(query.JobId, mutate: false, ct);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var page = await repository.ListByJobAsync(query.JobId, Math.Max(1, query.Page), pageSize, ct);
        var items = new List<BulkRequestSummaryDto>(page.Items.Count);
        foreach (var request in page.Items) items.Add(await access.SummaryAsync(request, job, ct));
        return new(items, page.Page, page.PageSize, page.Total);
    }
}

// The caller's own first-terminal snapshots. The repository owner predicate is the recipient
// boundary; job membership never broadens notification delivery.
public sealed class ListBulkScoringNotificationsHandler(BulkScoringAccess access, IBulkScoringRepository repository)
    : IRequestHandler<ListBulkScoringNotificationsQuery, IReadOnlyList<BulkTerminalNotificationDto>>
{
    public async Task<IReadOnlyList<BulkTerminalNotificationDto>> Handle(ListBulkScoringNotificationsQuery query, CancellationToken ct)
    {
        access.EnsureEnabled();
        var requests = await repository.ListUnacknowledgedAsync(access.Actor, ct);
        var result = new List<BulkTerminalNotificationDto>(requests.Count);
        foreach (var request in requests)
        {
            if (request.Status != BulkRequestStatus.Completed
                || request.Outcome is null
                || request.TerminalAt is null
                || !string.Equals(request.OwnerActorId, access.Actor, StringComparison.Ordinal))
                continue;

            result.Add(new(
                request.Id,
                request.OwnerActorId,
                request.Outcome.Value.ToWire(),
                new(
                    Math.Max(0, request.CompletedCount),
                    Math.Max(0, request.FailedCount),
                    Math.Max(0, request.SkippedCount),
                    Math.Max(0, request.CancelledCount)),
                request.TerminalAt.Value,
                $"/api/bulk-scoring-requests/{Uri.EscapeDataString(request.Id)}")
            {
                AuthorizationState = await access.AuthorizationStateAsync(request, ct),
            });
        }
        return result;
    }
}

internal static class BulkScoringQueryProjections
{
    public static async Task<BulkRequestDetailDto> DetailAsync(
        BulkScoringAccess access,
        IBulkScoringRepository? repository,
        BulkScoringRequest request,
        Job? job,
        BulkInclusionTotals? totals,
        CancellationToken ct)
    {
        var authorizationState = await access.AuthorizationStateAsync(request, ct);
        var names = await access.ActorDisplayNamesAsync(
            [request.OwnerActorId, request.CancelledByActorId],
            ct);
        var evidence = await ConfigurationEvidenceAsync(repository, request, job, ct);

        return new(
            request.Id,
            request.JobId,
            request.Status.ToWire(),
            request.Outcome?.ToWire(),
            request.SourceKind.ToWire(),
            request.SourceFolderName,
            request.SourceFolderPath,
            request.SourceWebUrl,
            NameOf(names, request.OwnerActorId),
            request.ConfirmedAt ?? request.CreatedAt,
            request.TerminalAt,
            request.ConvertToRasterPdf,
            request.AllowDuplicates,
            request.CancelRequested,
            request.RetryOfRequestId,
            request.AcknowledgedAt is not null,
            SafeCounts(request),
            request.ConcurrencyVersion,
            BulkScoringAccess.LimitsDto(BulkScoringAccess.FrozenLimits(request)),
            evidence.CapturedVersionId,
            evidence.CurrentVersionId,
            evidence.ChangedSinceAcceptance,
            totals is null ? null : access.Progress(request, totals),
            "delegated",
            authorizationState,
            request.SourceTenantId,
            request.CancelledByActorId is null ? null : NameOf(names, request.CancelledByActorId),
            request.CancelRequestedAt);
    }

    public static async Task<BulkRequestSummaryDto> SummaryAsync(
        BulkScoringAccess access,
        IBulkScoringRepository? repository,
        BulkScoringRequest request,
        Job? job,
        CancellationToken ct)
    {
        var authorizationState = await access.AuthorizationStateAsync(request, ct);
        var evidence = await ConfigurationEvidenceAsync(repository, request, job, ct);
        return new(
            request.Id,
            request.JobId,
            request.Status.ToWire(),
            request.Outcome?.ToWire(),
            request.SourceKind.ToWire(),
            request.SourceFolderName,
            request.SourceFolderPath,
            request.TerminalAt,
            request.ConvertToRasterPdf,
            request.AllowDuplicates,
            request.CancelRequested,
            request.RetryOfRequestId,
            request.AcknowledgedAt is not null,
            SafeCounts(request),
            request.ConcurrencyVersion,
            authorizationState,
            evidence.CapturedVersionId,
            evidence.CurrentVersionId,
            evidence.ChangedSinceAcceptance);
    }

    private static async Task<ConfigurationEvidence> ConfigurationEvidenceAsync(
        IBulkScoringRepository? repository,
        BulkScoringRequest request,
        Job? job,
        CancellationToken ct)
    {
        var captured = request.CapturedScoringConfigVersionId;
        var current = job?.CurrentConfigVersionId;
        if (captured is null)
            return new(null, current, false);

        var changed = job is not null
            && !string.Equals(captured, current, StringComparison.Ordinal);
        if (!changed)
            changed = request.Items.Any(item =>
                item.ScoringConfigVersionId is not null
                && !string.Equals(item.ScoringConfigVersionId, captured, StringComparison.Ordinal));
        if (!changed && repository is not null)
            changed = await repository.HasUsedScoringConfigVersionDifferentAsync(request.Id, captured, ct);
        return new(captured, current, changed);
    }

    private static BulkCountsDto SafeCounts(BulkScoringRequest request)
    {
        var waiting = NonNegative(request.WaitingCount);
        var importing = NonNegative(request.ImportingCount);
        var converting = NonNegative(request.ConvertingCount);
        var scoring = NonNegative(request.ScoringCount);
        var completed = NonNegative(request.CompletedCount);
        var failed = NonNegative(request.FailedCount);
        var skipped = NonNegative(request.SkippedCount);
        var cancelled = NonNegative(request.CancelledCount);
        var reconciledDiscovered = (int)Math.Min(
            int.MaxValue,
            (long)waiting + importing + converting + scoring + completed + failed + skipped + cancelled);
        return new(
            reconciledDiscovered,
            NonNegative(request.ExcludedCount),
            NonNegative(request.InaccessibleCount),
            waiting,
            importing,
            converting,
            scoring,
            completed,
            failed,
            skipped,
            cancelled);
    }

    private static int NonNegative(int count) => Math.Max(0, count);

    private static string NameOf(IReadOnlyDictionary<string, string> names, string actorId) =>
        names.TryGetValue(actorId, out var name) ? name : actorId;

    private sealed record ConfigurationEvidence(
        string? CapturedVersionId,
        string? CurrentVersionId,
        bool ChangedSinceAcceptance);
}

public sealed class GetBulkScoringOriginalHandler(BulkScoringAccess access, IBulkScoringRepository repository, IBulkContentStore contentStore)
    : IRequestHandler<GetBulkScoringOriginalQuery, BulkOriginalFile>
{
    public async Task<BulkOriginalFile> Handle(GetBulkScoringOriginalQuery query, CancellationToken ct)
    {
        var request = await BulkRequestLoader.LoadAsync(access, repository, query.RequestId, mutate: false, ct);
        var item = await repository.GetItemAsync(request.Id, query.ItemId, ct) ?? throw BulkApiException.NotFound("The file was not found.");
        var content = await repository.GetContentAsync(item.Id, BulkContentKind.Original, ct)
            ?? throw BulkApiException.NotFound("The original file is not retained.");
        return new(await contentStore.ReadAsync(content, ct), item.FileName);
    }
}
