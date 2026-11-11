namespace TalentMatch.Domain.Entities.BulkScoringRequests;

// Single source of truth for legal transitions; validated against the shared contract fixtures
// so both stacks reject the same illegal moves.
public static class BulkStateMachine
{
    private static readonly IReadOnlyDictionary<BulkItemStatus, BulkItemStatus[]> ItemMoves = new Dictionary<BulkItemStatus, BulkItemStatus[]>
    {
        [BulkItemStatus.Waiting] = [BulkItemStatus.Importing, BulkItemStatus.Cancelled, BulkItemStatus.Failed],
        [BulkItemStatus.Importing] = [BulkItemStatus.Converting, BulkItemStatus.Scoring, BulkItemStatus.Skipped, BulkItemStatus.Waiting, BulkItemStatus.Failed, BulkItemStatus.Cancelled],
        [BulkItemStatus.Converting] = [BulkItemStatus.Scoring, BulkItemStatus.Waiting, BulkItemStatus.Failed, BulkItemStatus.Cancelled],
        [BulkItemStatus.Scoring] = [BulkItemStatus.Completed, BulkItemStatus.Failed],
        [BulkItemStatus.Completed] = [],
        [BulkItemStatus.Failed] = [],
        [BulkItemStatus.Skipped] = [],
        [BulkItemStatus.Cancelled] = [],
    };

    private static readonly IReadOnlyDictionary<BulkRequestStatus, BulkRequestStatus[]> RequestMoves = new Dictionary<BulkRequestStatus, BulkRequestStatus[]>
    {
        [BulkRequestStatus.Draft] = [BulkRequestStatus.Active],
        [BulkRequestStatus.Active] = [BulkRequestStatus.Completed],
        [BulkRequestStatus.Completed] = [],
    };

    private static readonly IReadOnlyDictionary<BulkAuthorizationStatus, BulkAuthorizationStatus[]> AuthorizationMoves = new Dictionary<BulkAuthorizationStatus, BulkAuthorizationStatus[]>
    {
        [BulkAuthorizationStatus.Active] =
        [
            BulkAuthorizationStatus.Lapsed,
            BulkAuthorizationStatus.Revoked,
            BulkAuthorizationStatus.AccessLost,
            BulkAuthorizationStatus.Discarded,
        ],
        [BulkAuthorizationStatus.Lapsed] = [BulkAuthorizationStatus.Discarded],
        [BulkAuthorizationStatus.Revoked] = [BulkAuthorizationStatus.Discarded],
        [BulkAuthorizationStatus.AccessLost] = [BulkAuthorizationStatus.Discarded],
        [BulkAuthorizationStatus.Discarded] = [],
        [BulkAuthorizationStatus.UnavailableHistorical] = [],
    };

    // Reasons a recruiter may retry through a new request; the last two only when they now resolve.
    private static readonly BulkReasonCode[] Retryable =
    [
        BulkReasonCode.TransientExhausted,
        BulkReasonCode.SourceChanged,
        BulkReasonCode.ConversionTimeout,
        BulkReasonCode.AuthorizationLapsed,
        BulkReasonCode.CancelledByUser,
        BulkReasonCode.SourceMissing,
        BulkReasonCode.AccessDenied,
    ];

    private static readonly BulkReasonCode[] RetryableOnlyWhenResolved =
    [
        BulkReasonCode.SourceMissing,
        BulkReasonCode.AccessDenied,
    ];

    public static bool CanMove(BulkItemStatus from, BulkItemStatus to) => ItemMoves[from].Contains(to);

    public static bool CanMove(BulkRequestStatus from, BulkRequestStatus to) => RequestMoves[from].Contains(to);

    public static bool CanMove(BulkAuthorizationStatus from, BulkAuthorizationStatus to) => AuthorizationMoves[from].Contains(to);

    public static IReadOnlyList<BulkItemStatus> NextItemStatuses(BulkItemStatus from) => ItemMoves[from];

    public static IReadOnlyList<BulkRequestStatus> NextRequestStatuses(BulkRequestStatus from) => RequestMoves[from];

    public static IReadOnlyList<BulkAuthorizationStatus> NextAuthorizationStatuses(BulkAuthorizationStatus from) => AuthorizationMoves[from];

    public static bool IsTerminal(this BulkItemStatus status) =>
        status is BulkItemStatus.Completed or BulkItemStatus.Failed or BulkItemStatus.Skipped or BulkItemStatus.Cancelled;

    public static bool IsRetryable(BulkReasonCode reason) => Retryable.Contains(reason);

    public static bool RetryRequiresResolution(BulkReasonCode reason) => RetryableOnlyWhenResolved.Contains(reason);

    // Scoring-stage failures are never retried by this feature; an item that reached scoring is not retryable.
    public static bool IsRetryableItem(BulkItemStatus status, BulkReasonCode? reason, DateTime? handedOffAt) =>
        status is BulkItemStatus.Failed or BulkItemStatus.Cancelled
        && handedOffAt is null
        && reason is { } r
        && IsRetryable(r);
}
