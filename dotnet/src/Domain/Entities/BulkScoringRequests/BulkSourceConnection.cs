using System.Security.Cryptography;

namespace TalentMatch.Domain.Entities.BulkScoringRequests;

public sealed class BulkSourceConnection
{
    public string Id { get; set; } = string.Empty;
    public string ProductActorId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public BulkSourceConnectionStatus Status { get; set; } = BulkSourceConnectionStatus.Authorizing;
    public string CanonicalUrlHash { get; set; } = string.Empty;
    public byte[]? PendingFolderUrlEncrypted { get; set; }
    public string? StateHash { get; set; }
    public byte[]? PkceVerifierEncrypted { get; set; }
    public string RedirectAfterPath { get; set; } = string.Empty;
    public string? SourceTenantId { get; set; }
    public string? SourceAccountBindingHash { get; set; }
    public string? SourceAccountDisplay { get; set; }
    public BulkSourceKind? SourceKind { get; set; }
    public string? SourceCloud { get; set; }
    public string? SourceDriveId { get; set; }
    public string? SourceFolderItemId { get; set; }
    public string? SourceFolderName { get; set; }
    public string? SourceFolderPath { get; set; }
    public string? SourceWebUrl { get; set; }
    public byte[]? ProtectedPayload { get; set; }
    public byte[]? WrappedDataKey { get; set; }
    public string? KeyVersion { get; set; }
    public DateTime ObtainedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? ConsumedByRequestId { get; set; }
    public DateTime? DiscardedAt { get; set; }
    public string? FailureCode { get; set; }
    public int ConcurrencyVersion { get; set; }

    public static BulkSourceConnection Begin(
        string id,
        string productActorId,
        string jobId,
        string canonicalUrlHash,
        byte[] pendingFolderUrlEncrypted,
        string stateHash,
        byte[] pkceVerifierEncrypted,
        string redirectAfterPath,
        DateTime utcNow,
        TimeSpan idleTtl,
        TimeSpan absoluteTtl)
    {
        Required(id, nameof(id));
        Required(productActorId, nameof(productActorId));
        Required(jobId, nameof(jobId));
        Required(canonicalUrlHash, nameof(canonicalUrlHash));
        Required(stateHash, nameof(stateHash));
        if (pendingFolderUrlEncrypted.Length == 0 || pkceVerifierEncrypted.Length == 0)
            throw new ArgumentException("Pending folder URL and PKCE verifier must be encrypted.");
        if (!IsLocalProductPath(redirectAfterPath))
            throw new ArgumentException("The return path must be local and relative to the product.", nameof(redirectAfterPath));
        ValidateTtls(idleTtl, absoluteTtl);

        return new BulkSourceConnection
        {
            Id = id,
            ProductActorId = productActorId,
            JobId = jobId,
            CanonicalUrlHash = canonicalUrlHash,
            PendingFolderUrlEncrypted = pendingFolderUrlEncrypted,
            StateHash = stateHash,
            PkceVerifierEncrypted = pkceVerifierEncrypted,
            RedirectAfterPath = redirectAfterPath,
            ObtainedAt = utcNow,
            LastActivityAt = utcNow,
            ExpiresAt = Expiry(utcNow, utcNow, idleTtl, absoluteTtl),
        };
    }

    public bool IsBoundTo(string connectionId, string productActorId, string jobId, string canonicalUrlHash) =>
        Id == connectionId
        && ProductActorId == productActorId
        && JobId == jobId
        && CanonicalUrlHash == canonicalUrlHash;

    public void ProtectPendingKey(byte[] wrappedDataKey, string keyVersion)
    {
        RequireStatus(BulkSourceConnectionStatus.Authorizing);
        if (wrappedDataKey is not { Length: > 0 })
            throw new ArgumentException("A wrapped pending data key is required.", nameof(wrappedDataKey));
        WrappedDataKey = wrappedDataKey;
        KeyVersion = Required(keyVersion, nameof(keyVersion));
    }

    private static bool IsLocalProductPath(string value) =>
        value.Length > 1
        && value[0] == '/'
        && value[1] != '/'
        && !value.Contains('\\')
        && !value.Contains('#')
        && !value.Any(char.IsControl);

    public bool IsExpired(DateTime utcNow) =>
        IsPending && utcNow >= ExpiresAt;

    public void CompleteAuthorization(
        string expectedStateHash,
        string sourceTenantId,
        string sourceAccountBindingHash,
        byte[] protectedPayload,
        byte[] wrappedDataKey,
        string keyVersion,
        DateTime utcNow,
        TimeSpan idleTtl,
        TimeSpan absoluteTtl)
    {
        RequireStatus(BulkSourceConnectionStatus.Authorizing);
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(StateHash ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(expectedStateHash)))
            throw new InvalidOperationException("The source connection state is invalid or already consumed.");
        Required(sourceTenantId, nameof(sourceTenantId));
        Required(sourceAccountBindingHash, nameof(sourceAccountBindingHash));
        Required(keyVersion, nameof(keyVersion));
        if (protectedPayload.Length == 0 || wrappedDataKey.Length == 0)
            throw new ArgumentException("Protected authorization material is required.");

        Zero(PendingFolderUrlEncrypted);
        Zero(PkceVerifierEncrypted);
        Zero(ProtectedPayload);
        Zero(WrappedDataKey);
        PendingFolderUrlEncrypted = null;
        PkceVerifierEncrypted = null;
        Status = BulkSourceConnectionStatus.Authorized;
        SourceTenantId = sourceTenantId;
        SourceAccountBindingHash = sourceAccountBindingHash;
        ProtectedPayload = protectedPayload;
        WrappedDataKey = wrappedDataKey;
        KeyVersion = keyVersion;
        StateHash = null;
        Touch(utcNow, idleTtl, absoluteTtl);
    }

    public void VerifyFolder(
        BulkSourceKind sourceKind,
        string sourceDriveId,
        string sourceFolderItemId,
        string sourceFolderName,
        string sourceFolderPath,
        string sourceWebUrl,
        string? sourceAccountDisplay,
        DateTime utcNow,
        TimeSpan idleTtl,
        TimeSpan absoluteTtl)
    {
        RequireStatus(BulkSourceConnectionStatus.Authorized);
        SourceKind = sourceKind;
        SourceCloud = "commercial";
        SourceDriveId = Required(sourceDriveId, nameof(sourceDriveId));
        SourceFolderItemId = Required(sourceFolderItemId, nameof(sourceFolderItemId));
        SourceFolderName = Required(sourceFolderName, nameof(sourceFolderName));
        SourceFolderPath = Required(sourceFolderPath, nameof(sourceFolderPath));
        SourceWebUrl = Required(sourceWebUrl, nameof(sourceWebUrl));
        SourceAccountDisplay = sourceAccountDisplay;
        Status = BulkSourceConnectionStatus.Verified;
        Touch(utcNow, idleTtl, absoluteTtl);
    }

    public void Touch(DateTime utcNow, TimeSpan idleTtl, TimeSpan absoluteTtl)
    {
        if (!IsPending) throw new InvalidOperationException("Only a pending source connection can be touched.");
        ValidateTtls(idleTtl, absoluteTtl);
        LastActivityAt = utcNow;
        ExpiresAt = Expiry(ObtainedAt, utcNow, idleTtl, absoluteTtl);
    }

    public void Consume(string requestId, DateTime utcNow)
    {
        RequireStatus(BulkSourceConnectionStatus.Verified);
        ConsumedByRequestId = Required(requestId, nameof(requestId));
        Status = BulkSourceConnectionStatus.Consumed;
        DiscardedAt = utcNow;
        ClearProtectedMaterial();
    }

    public void Abandon(DateTime utcNow) => Finish(BulkSourceConnectionStatus.Abandoned, null, utcNow);

    public void Expire(DateTime utcNow) => Finish(BulkSourceConnectionStatus.Expired, null, utcNow);

    public void Fail(string safeFailureCode, DateTime utcNow) =>
        Finish(BulkSourceConnectionStatus.Failed, Required(safeFailureCode, nameof(safeFailureCode)), utcNow);

    public bool IsPending => Status is
        BulkSourceConnectionStatus.Authorizing or
        BulkSourceConnectionStatus.Authorized or
        BulkSourceConnectionStatus.Verified;

    private void Finish(BulkSourceConnectionStatus status, string? failureCode, DateTime utcNow)
    {
        if (!IsPending) throw new InvalidOperationException("A terminal source connection cannot transition again.");
        Status = status;
        FailureCode = failureCode;
        DiscardedAt = utcNow;
        ClearProtectedMaterial();
    }

    private void ClearProtectedMaterial()
    {
        Zero(PendingFolderUrlEncrypted);
        Zero(PkceVerifierEncrypted);
        Zero(ProtectedPayload);
        Zero(WrappedDataKey);
        PendingFolderUrlEncrypted = null;
        PkceVerifierEncrypted = null;
        ProtectedPayload = null;
        WrappedDataKey = null;
        StateHash = null;
        KeyVersion = null;
    }

    private void RequireStatus(BulkSourceConnectionStatus expected)
    {
        if (Status != expected)
            throw new InvalidOperationException($"Expected {expected.ToWire()}, found {Status.ToWire()}.");
    }

    private static DateTime Expiry(DateTime obtainedAt, DateTime activityAt, TimeSpan idleTtl, TimeSpan absoluteTtl) =>
        new(Math.Min((activityAt + idleTtl).Ticks, (obtainedAt + absoluteTtl).Ticks), DateTimeKind.Utc);

    private static void ValidateTtls(TimeSpan idleTtl, TimeSpan absoluteTtl)
    {
        if (idleTtl <= TimeSpan.Zero || absoluteTtl <= TimeSpan.Zero || idleTtl > absoluteTtl)
            throw new ArgumentOutOfRangeException(nameof(idleTtl), "Connection TTLs must be positive and idle must not exceed absolute.");
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A binding value is required.", name) : value;

    private static void Zero(byte[]? value)
    {
        if (value is { Length: > 0 }) CryptographicOperations.ZeroMemory(value);
    }
}
