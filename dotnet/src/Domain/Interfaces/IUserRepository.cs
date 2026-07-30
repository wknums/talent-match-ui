namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);
    Task<User?> GetByEntraIdentityAsync(string tenantId, string objectId, CancellationToken cancellationToken = default);
    Task<User> UpsertEntraProfileAsync(User user, CancellationToken cancellationToken = default);
    Task SetActiveAsync(string id, bool isActive, CancellationToken cancellationToken = default);
    Task UpdateLastLoginAsync(string id, DateTime lastLogin, CancellationToken cancellationToken = default);
    Task AddAsync(User user, CancellationToken cancellationToken = default);
    Task UpdateAsync(User user, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PasswordResetRequest>> GetResetRequestsAsync(CancellationToken cancellationToken = default);
    Task AddResetRequestAsync(PasswordResetRequest request, CancellationToken cancellationToken = default);
    Task UpdateResetRequestAsync(PasswordResetRequest request, CancellationToken cancellationToken = default);
}
