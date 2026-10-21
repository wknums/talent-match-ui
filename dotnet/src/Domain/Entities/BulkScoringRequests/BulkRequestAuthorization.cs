namespace TalentMatch.Domain.Entities.BulkScoringRequests;

using System.Security.Cryptography;

// Encrypted per-request delegated authorization. It is bound to exactly one accepted request,
// product actor, source tenant/account, drive, and folder. A retry always creates a new binding.
public sealed class BulkRequestAuthorization
{
    public string RequestId { get; set; } = string.Empty;
    public string ProductActorId { get; set; } = string.Empty;
    public string SourceTenantId { get; set; } = string.Empty;
    public string SourceAccountBindingHash { get; set; } = string.Empty;
    public string SourceDriveId { get; set; } = string.Empty;
    public string SourceFolderItemId { get; set; } = string.Empty;
    public byte[]? Payload { get; set; }
    public byte[]? WrappedDataKey { get; set; }
    public string KeyVersion { get; set; } = string.Empty;
    public BulkAuthorizationStatus Status { get; set; } = BulkAuthorizationStatus.Active;
    public string? LapseReason { get; set; }
    public DateTime ObtainedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? LapsedAt { get; set; }
    public DateTime? DiscardedAt { get; set; }
    public BulkScoringRequest? Request { get; set; }

    public static BulkRequestAuthorization Establish(
        string requestId,
        string productActorId,
        string sourceTenantId,
        string sourceAccountBindingHash,
        string sourceDriveId,
        string sourceFolderItemId,
        byte[] payload,
        byte[] wrappedDataKey,
        string keyVersion,
        DateTime utcNow)
    {
        Required(requestId, nameof(requestId));
        Required(productActorId, nameof(productActorId));
        Required(sourceTenantId, nameof(sourceTenantId));
        Required(sourceAccountBindingHash, nameof(sourceAccountBindingHash));
        Required(sourceDriveId, nameof(sourceDriveId));
        Required(sourceFolderItemId, nameof(sourceFolderItemId));
        Required(keyVersion, nameof(keyVersion));
        if (payload.Length == 0 || wrappedDataKey.Length == 0)
            throw new ArgumentException("An authorization requires protected payload and key material.");

        return new BulkRequestAuthorization
        {
            RequestId = requestId,
            ProductActorId = productActorId,
            SourceTenantId = sourceTenantId,
            SourceAccountBindingHash = sourceAccountBindingHash,
            SourceDriveId = sourceDriveId,
            SourceFolderItemId = sourceFolderItemId,
            Payload = payload,
            WrappedDataKey = wrappedDataKey,
            KeyVersion = keyVersion,
            ObtainedAt = utcNow,
        };
    }

    [Obsolete("Pre-v3 authorization has no trustworthy delegated source binding and is retained as unusable history only.")]
    public static BulkRequestAuthorization Establish(
        string requestId,
        string productActorId,
        byte[] payload,
        byte[] wrappedDataKey,
        string keyVersion,
        DateTime utcNow)
    {
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(wrappedDataKey);
        return new BulkRequestAuthorization
        {
            RequestId = requestId,
            ProductActorId = productActorId,
            KeyVersion = keyVersion,
            Status = BulkAuthorizationStatus.UnavailableHistorical,
            ObtainedAt = utcNow,
        };
    }

    public bool IsActive => Status == BulkAuthorizationStatus.Active;

    public bool IsBoundTo(
        string requestId,
        string productActorId,
        string sourceTenantId,
        string sourceAccountBindingHash,
        string sourceDriveId,
        string sourceFolderItemId) =>
        RequestId == requestId
        && ProductActorId == productActorId
        && SourceTenantId == sourceTenantId
        && SourceAccountBindingHash == sourceAccountBindingHash
        && SourceDriveId == sourceDriveId
        && SourceFolderItemId == sourceFolderItemId;

    public void MarkLapsed(string reason, DateTime utcNow)
    {
        Move(BulkAuthorizationStatus.Lapsed);
        LapseReason = reason;
        LapsedAt = utcNow;
        Clear();
    }

    public void MarkRevoked(string reason, DateTime utcNow) =>
        MakeUnusable(BulkAuthorizationStatus.Revoked, reason, utcNow);

    public void MarkAccessLost(string reason, DateTime utcNow) =>
        MakeUnusable(BulkAuthorizationStatus.AccessLost, reason, utcNow);

    public void Discard(DateTime utcNow)
    {
        Move(BulkAuthorizationStatus.Discarded);
        DiscardedAt = utcNow;
        Clear();
    }

    public void RecordUse(DateTime utcNow)
    {
        if (!IsActive) throw new InvalidOperationException("Only an active authorization can be used.");
        LastUsedAt = utcNow;
    }

    // The identity provider rotates the session cache as it is used; the new cache replaces the old one.
    // The original ObtainedAt is kept: the age limit counts from the recruiter's sign-in, not from the last refresh.
    public void ReplaceProtectedMaterial(byte[] payload, byte[] wrappedDataKey, string keyVersion, DateTime utcNow)
    {
        if (!IsActive) throw new InvalidOperationException("Only an active authorization can be updated.");
        if (payload.Length == 0 || wrappedDataKey.Length == 0)
            throw new ArgumentException("An authorization requires protected payload and key material.");
        if (Payload is not null) Array.Clear(Payload);
        if (WrappedDataKey is not null) Array.Clear(WrappedDataKey);
        Payload = payload;
        WrappedDataKey = wrappedDataKey;
        KeyVersion = keyVersion;
        LastUsedAt = utcNow;
    }

    public bool IsOlderThan(TimeSpan maxAge, DateTime utcNow) => utcNow - ObtainedAt > maxAge;

    private void Move(BulkAuthorizationStatus next)
    {
        if (!BulkStateMachine.CanMove(Status, next))
            throw new InvalidOperationException($"Illegal authorization transition {Status.ToWire()} -> {next.ToWire()}.");
        Status = next;
    }

    private void Clear()
    {
        if (Payload is not null) CryptographicOperations.ZeroMemory(Payload);
        if (WrappedDataKey is not null) CryptographicOperations.ZeroMemory(WrappedDataKey);
        Payload = null;
        WrappedDataKey = null;
    }

    private void MakeUnusable(BulkAuthorizationStatus status, string reason, DateTime utcNow)
    {
        Move(status);
        LapseReason = Required(reason, nameof(reason));
        LapsedAt = utcNow;
        Clear();
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A binding value is required.", name) : value;
}
