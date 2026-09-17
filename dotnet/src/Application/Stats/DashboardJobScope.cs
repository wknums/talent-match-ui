using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Stats;

internal static class DashboardJobScope
{
    public static async Task<IReadOnlyList<Job>> GetVisibleJobsAsync(
        IJobRepository jobs, ICurrentUserService user, CancellationToken ct)
    {
        var authorization = await user.GetAuthorizationStateAsync(ct);
        if (authorization is not null)
            return (await jobs.GetAllAsync(ct))
                .Where(job => JobAuthorization.CanRead(authorization, job)).ToArray();

        return user.IsAdmin || string.Equals(user.Department, "all", StringComparison.OrdinalIgnoreCase)
            ? await jobs.GetAllAsync(ct)
            : await jobs.GetByDepartmentsOrCreatorAsync(
                (user.Department ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                user.UserId ?? "", ct);
    }
}
