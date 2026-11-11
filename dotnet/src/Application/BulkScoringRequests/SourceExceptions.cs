using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.BulkScoringRequests;

// Source-layer failures. The worker classifies them (R9); handlers map them to HTTP errors.
public abstract class BulkSourceException(string message, Exception? inner = null) : Exception(message, inner);

// The connected recruiter does not have delegated access to this item.
public sealed class SourceAccessDeniedException(string message) : BulkSourceException(message);

public sealed class SourceNotFoundException(string message) : BulkSourceException(message);

// The recruiter's stored authorization can no longer yield a token (lapse), as opposed to a transient error.
public sealed class SourceAuthorizationLapsedException(string reason, Exception? inner = null)
    : BulkSourceException($"The recruiter's authorization lapsed ({reason}).", inner)
{
    public string Reason { get; } = reason;
}

// The caller's session cannot support a background authorization; the recruiter must sign in again.
public sealed class AuthorizationRequiredException(string message) : BulkSourceException(message);

public sealed class SourceLimitExceededException(string message, long maxBytes) : BulkSourceException(message)
{
    public long MaxBytes { get; } = maxBytes;
}

public sealed class SourceTransientException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : BulkSourceException(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class UnsupportedSourceException(string message) : BulkSourceException(message);

public sealed class BulkConversionException(BulkReasonCode reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public BulkReasonCode Reason { get; } = reason;
}
