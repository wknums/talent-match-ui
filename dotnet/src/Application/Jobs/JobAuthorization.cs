using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs;

internal static class JobAuthorization
{
    public static bool HasNormalizedScope(Job job)
        => Guid.TryParse(job.OrganizationId, out _)
            && Guid.TryParse(job.DepartmentId, out _);

    private static bool IsGlobalAdmin(CurrentAuthorizationState state)
        => state.Assignments.Any(assignment =>
            assignment.Status == "active"
            && assignment.Role == "admin"
            && assignment.OrganizationId is null
            && assignment.DepartmentId is null);

    public static bool CanRead(CurrentAuthorizationState state, Job job)
        => HasApplicableAssignment(state, job, allowReadOnly: true);

    public static bool CanMutate(CurrentAuthorizationState state, Job job)
        => HasApplicableAssignment(state, job, allowReadOnly: false);

    public static async Task EnsureCanReadAsync(
        Job job,
        ICurrentUserService? currentUser,
        IOrganizationRepository? organizationRepository,
        CancellationToken cancellationToken)
    {
        if (currentUser is null)
            return;

        var state = await currentUser.GetAuthorizationStateAsync(cancellationToken);
        if (state is null)
            return;

        if (!CanRead(state, job))
            throw new UnauthorizedAccessException("The current user cannot read this job.");

        if (!HasNormalizedScope(job) && IsGlobalAdmin(state))
            return;

        await EnsureValidScopeAsync(job, state, organizationRepository, cancellationToken);
    }

    public static async Task EnsureCanMutateAsync(
        Job job,
        ICurrentUserService? currentUser,
        IOrganizationRepository? organizationRepository,
        CancellationToken cancellationToken)
    {
        if (currentUser is null)
            return;

        var state = await currentUser.GetAuthorizationStateAsync(cancellationToken);
        if (state is null)
            return;

        if (!CanMutate(state, job))
            throw new UnauthorizedAccessException("The current user cannot mutate this job.");

        await EnsureValidScopeAsync(job, state, organizationRepository, cancellationToken);
    }

    public static async Task EnsureValidScopeAsync(
        Job job,
        CurrentAuthorizationState state,
        IOrganizationRepository? organizationRepository,
        CancellationToken cancellationToken)
    {
        if (!HasNormalizedScope(job))
            throw new InvalidJobScopeException();

        var organization = organizationRepository is null
            ? null
            : await organizationRepository.GetByIdAsync(job.OrganizationId!, cancellationToken);
        var department = organizationRepository is null
            ? null
            : await organizationRepository.GetDepartmentAsync(
                job.OrganizationId!, job.DepartmentId!, cancellationToken);

        var knownFromAuthorizationState = state.DepartmentMemberships.Any(membership =>
            membership.Status == "active"
            && string.Equals(membership.OrganizationId, job.OrganizationId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(membership.DepartmentId, job.DepartmentId, StringComparison.OrdinalIgnoreCase));

        if (organizationRepository is not null
            && (organization?.Status != "active" || department?.Status != "active"))
            throw new InvalidJobScopeException();

        if (organizationRepository is null && !knownFromAuthorizationState)
            throw new InvalidJobScopeException();
    }

    private static bool HasApplicableAssignment(
        CurrentAuthorizationState state,
        Job job,
        bool allowReadOnly)
    {
        if (!HasNormalizedScope(job))
            // Unscoped rows predate organizations. Keep them reachable for a global admin so the
            // backfill can be observed and repaired, but never mutable until they are scoped.
            return allowReadOnly && IsGlobalAdmin(state);

        return state.Assignments.Any(assignment =>
        {
            if (assignment.Status != "active")
                return false;

            if (assignment.Role == "admin"
                && assignment.OrganizationId is null
                && assignment.DepartmentId is null)
                return true;

            if (!string.Equals(
                    assignment.OrganizationId,
                    job.OrganizationId,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return assignment.Role switch
            {
                "organization_admin" => assignment.DepartmentId is null,
                "recruiter" => string.Equals(
                    assignment.DepartmentId,
                    job.DepartmentId,
                    StringComparison.OrdinalIgnoreCase),
                "business_panel" when allowReadOnly => assignment.DepartmentId is null
                    || string.Equals(
                        assignment.DepartmentId,
                        job.DepartmentId,
                        StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        });
    }
}

public sealed class InvalidJobScopeException()
    : InvalidOperationException("The job organization and department do not form a valid pair.");