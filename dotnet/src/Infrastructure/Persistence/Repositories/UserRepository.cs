using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class UserRepository : IUserRepository
{
    private readonly AppDbContext _context;
    public UserRepository(AppDbContext context) => _context = context;

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
        => await _context.Users.AsNoTracking().ToListAsync(ct);

    public async Task<User?> GetByIdAsync(string id, CancellationToken ct = default)
        => await _context.Users.FindAsync(new object[] { id }, ct);

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
        => await _context.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public async Task<User?> GetByEntraIdentityAsync(string tenantId, string objectId, CancellationToken ct = default)
        => await _context.Users.FirstOrDefaultAsync(u => u.AuthenticationProvider == "entra" && u.EntraTenantId == tenantId && u.EntraObjectId == objectId, ct);

    public async Task<User> UpsertEntraProfileAsync(User user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(user.EntraTenantId) || string.IsNullOrWhiteSpace(user.EntraObjectId))
            throw new InvalidOperationException("Entra tenant and object IDs are required.");
        var existing = await GetByEntraIdentityAsync(user.EntraTenantId, user.EntraObjectId, ct);
        if (existing is null)
        {
            user.AuthenticationProvider = "entra";
            user.PasswordHash = null;
            user.PasswordResetRequired = false;
            _context.Users.Add(user);
            await _context.SaveChangesAsync(ct);
            return user;
        }
        existing.Username = user.Username;
        existing.FullName = user.FullName;
        existing.Email = user.Email;
        existing.LastLogin = user.LastLogin;
        await _context.SaveChangesAsync(ct);
        return existing;
    }

    public async Task SetActiveAsync(string id, bool isActive, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException($"User '{id}' was not found.");
        user.IsActive = isActive;
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateLastLoginAsync(string id, DateTime lastLogin, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException($"User '{id}' was not found.");
        user.LastLogin = lastLogin;
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddAsync(User user, CancellationToken ct = default)
    {
        await _context.Users.AddAsync(user, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        _context.Users.Update(user);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { id }, ct);
        if (user != null)
        {
            _context.Users.Remove(user);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<PasswordResetRequest>> GetResetRequestsAsync(CancellationToken ct = default)
        => await _context.PasswordResetRequests
            .Where(r => r.Status == "pending")
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task AddResetRequestAsync(PasswordResetRequest request, CancellationToken ct = default)
    {
        await _context.PasswordResetRequests.AddAsync(request, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateResetRequestAsync(PasswordResetRequest request, CancellationToken ct = default)
    {
        _context.PasswordResetRequests.Update(request);
        await _context.SaveChangesAsync(ct);
    }
}
