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
