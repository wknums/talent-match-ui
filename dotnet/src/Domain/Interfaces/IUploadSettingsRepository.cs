using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Interfaces;

public interface IUploadSettingsRepository
{
    Task<UploadSettings?> GetPersistedAsync(CancellationToken cancellationToken = default);
    Task<UploadSettings> SaveAsync(
        UploadSettings settings,
        int expectedConcurrencyVersion,
        CancellationToken cancellationToken = default);
}
