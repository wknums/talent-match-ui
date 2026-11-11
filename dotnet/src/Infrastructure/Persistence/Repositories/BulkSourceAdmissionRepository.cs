using System.Data;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class BulkSourceAdmissionRepository(AppDbContext context) : IBulkSourceAdmissionRepository
{
    public Task<bool> TryAdmitConnectionStartAsync(
        string actorSubjectHash,
        string ipSubjectHash,
        DateTime utcNow,
        int actorStartsPerMinute,
        int ipStartsPerMinute,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var actorCount = await CountAsync(BulkSourceAdmissionKind.ConnectionActor, actorSubjectHash, utcNow, ct);
            var ipCount = await CountAsync(BulkSourceAdmissionKind.ConnectionIp, ipSubjectHash, utcNow, ct);
            if (actorCount >= actorStartsPerMinute || ipCount >= ipStartsPerMinute) return false;

            await context.BulkSourceAdmissionEvents.AddRangeAsync(
                New(BulkSourceAdmissionKind.ConnectionActor, actorSubjectHash, utcNow),
                New(BulkSourceAdmissionKind.ConnectionIp, ipSubjectHash, utcNow));
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<bool> TryAdmitCallbackAsync(
        string ipSubjectHash,
        DateTime utcNow,
        int callbacksPerMinute,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var count = await CountAsync(BulkSourceAdmissionKind.CallbackIp, ipSubjectHash, utcNow, ct);
            if (count >= callbacksPerMinute) return false;
            await context.BulkSourceAdmissionEvents.AddAsync(
                New(BulkSourceAdmissionKind.CallbackIp, ipSubjectHash, utcNow), ct);
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<int> CleanupExpiredAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        context.BulkSourceAdmissionEvents
            .Where(row => row.ExpiresAt <= utcNow)
            .ExecuteDeleteAsync(cancellationToken);

    private Task<int> CountAsync(
        BulkSourceAdmissionKind kind,
        string subjectHash,
        DateTime utcNow,
        CancellationToken cancellationToken) =>
        context.BulkSourceAdmissionEvents.CountAsync(
            row => row.Kind == kind && row.SubjectHash == subjectHash && row.ExpiresAt > utcNow,
            cancellationToken);

    private static BulkSourceAdmissionEvent New(
        BulkSourceAdmissionKind kind,
        string subjectHash,
        DateTime utcNow) =>
        new()
        {
            Kind = kind,
            SubjectHash = subjectHash,
            OccurredAt = utcNow,
            ExpiresAt = utcNow.AddMinutes(1),
        };
}
