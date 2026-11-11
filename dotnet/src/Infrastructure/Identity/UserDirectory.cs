using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Identity;

public sealed class UserDirectory(AppDbContext context) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<string, string>> ResolveDisplayNamesAsync(
        IReadOnlyCollection<string> actorIds,
        CancellationToken cancellationToken = default)
    {
        var ids = actorIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var users = await context.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.FullName })
            .ToDictionaryAsync(user => user.Id, user => user.FullName, StringComparer.Ordinal, cancellationToken);

        return ids.ToDictionary(
            id => id,
            id => users.TryGetValue(id, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : id,
            StringComparer.Ordinal);
    }
}
