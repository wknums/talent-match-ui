using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class UploadSettingsRepository(AppDbContext context) : IUploadSettingsRepository
{
    public Task<UploadSettings?> GetPersistedAsync(CancellationToken cancellationToken = default) =>
        context.UploadSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == UploadSettings.SingletonId, cancellationToken);

    public Task<UploadSettings> SaveAsync(
        UploadSettings settings,
        int expectedConcurrencyVersion,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var existing = await context.UploadSettings
                .SingleOrDefaultAsync(x => x.Id == UploadSettings.SingletonId, ct);
            if (existing is null)
            {
                if (expectedConcurrencyVersion != 0)
                    throw new InvalidOperationException("stale_version");
                settings.Id = UploadSettings.SingletonId;
                settings.ConcurrencyVersion = 1;
                await context.UploadSettings.AddAsync(settings, ct);
            }
            else
            {
                if (existing.ConcurrencyVersion != expectedConcurrencyVersion)
                    throw new InvalidOperationException("stale_version");
                existing.FileConcurrency = settings.FileConcurrency;
                existing.MaxIndividualFileBytes = settings.MaxIndividualFileBytes;
                existing.MaxInFlightBytes = settings.MaxInFlightBytes;
                existing.UpdatedAt = settings.UpdatedAt;
                existing.UpdatedBy = settings.UpdatedBy;
                existing.ConcurrencyVersion++;
                settings = existing;
            }

            await context.SaveChangesAsync(ct);
            return settings;
        }, cancellationToken);
}
