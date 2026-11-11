using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed partial class EntraAccessManagementRepository : IEntraAccessManagementRepository
{
    private readonly AppDbContext _context;
    private readonly TimeProvider _clock;

    public EntraAccessManagementRepository(AppDbContext context) : this(context, TimeProvider.System) { }

    public EntraAccessManagementRepository(AppDbContext context, TimeProvider clock)
    {
        _context = context;
        _clock = clock;
    }

    public async Task<EntraAccessAggregate> ConvergeSystemAdministratorContextAsync(
        ConvergeSystemAdministratorContext request, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _context.ExecuteInTransactionAsync(async token =>
                {
                    var now = _clock.GetUtcNow().UtcDateTime;
                    var configurations = await _context.SystemAdministrationConfigurations.Where(x =>
                        x.TenantId == request.TenantId && (request.ConfigurationId == null || x.Id == request.ConfigurationId))
                        .Take(2).ToListAsync(token);
                    var configuration = configurations.Count == 1 ? configurations[0] : null;
                    if (request.CandidateTransitionId is not null)
                    {
                        var pending = await _context.AdministrationTransitions.AsNoTracking().SingleOrDefaultAsync(
                            x => x.Id == request.CandidateTransitionId, token);
                        if (pending is null || pending.Phase is not ("sql_switched" or "replacement_verified")
                            || pending.LeaseExpiresAt <= now)
                            throw new EntraAccessManagementException("administration_transition_conflict", "Candidate is not pending.");
                        var desired = JsonSerializer.Deserialize<SystemAdministrationConfiguration>(pending.CandidateConfiguration,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (desired is null || desired.Id != request.ConfigurationId || desired.TenantId != request.TenantId
                            || desired.AuthorityVersion != pending.CandidateVersion
                            || (configuration?.AuthorityVersion ?? 0) != pending.CommittedVersion)
                            throw new EntraAccessManagementException("administration_transition_conflict", "Candidate changed.");
                        configuration = desired;
                    }
                    if (configuration is null || configuration.Mode != "group"
                        || configuration.AuthorityVersion != request.ExpectedAuthorityVersion)
                        throw new EntraAccessManagementException("administration_transition_conflict",
                            "Administration state changed. Refresh and retry.");
                    var users = await LockUsersAsync(request.TenantId, request.ObjectId, request.ObjectId, token);
                    var user = users.SingleOrDefault();
                    if (user is not null && !user.IsActive)
                        throw new EntraAccessManagementException("identity_disabled", "This application identity is disabled.");
                    RequireEvidence(request, configuration, now);
                    if (!await _context.RoleGroupMappings.AnyAsync(x =>
                        x.Id == configuration.SystemRoleGroupMappingId && x.TenantId == configuration.TenantId
                        && x.GroupObjectId == configuration.GroupObjectId && x.Enabled && x.Purpose == "system_admin"
                        && x.Role == "admin" && x.OrganizationId == null && x.DepartmentId == null, token))
                        throw new EntraAccessManagementException("admin_configuration_invalid",
                            "Administration configuration is not ready. Contact the deployment operator.");
                    if (!await _context.Organizations.AnyAsync(x =>
                        x.Id == configuration.InitialOrganizationId && x.Status == "active", token)
                        || !await _context.Departments.AnyAsync(x => x.Id == configuration.InitialDepartmentId
                            && x.OrganizationId == configuration.InitialOrganizationId && x.Status == "active", token))
                        throw InvalidScope("The configured initial organization and department must be active.");

                    OrganizationMembership? membership = null;
                    DepartmentMembership? department = null;
                    DepartmentMembership? currentDefault = null;
                    if (user is not null)
                    {
                        membership = await _context.OrganizationMemberships.SingleOrDefaultAsync(x =>
                            x.UserId == user.Id && x.OrganizationId == configuration.InitialOrganizationId && x.Status == "active", token);
                        department = await _context.DepartmentMemberships.SingleOrDefaultAsync(x =>
                            x.UserId == user.Id && x.OrganizationId == configuration.InitialOrganizationId
                            && x.DepartmentId == configuration.InitialDepartmentId && x.Status == "active", token);
                        if (membership?.DefaultDepartmentMembershipId is not null)
                        {
                            currentDefault = await _context.DepartmentMemberships
                                .SingleOrDefaultAsync(x => x.Id == membership.DefaultDepartmentMembershipId, token);
                            if (currentDefault is null || currentDefault.UserId != user.Id
                                || currentDefault.OrganizationId != configuration.InitialOrganizationId)
                                throw InvalidScope("The existing default department belongs to a different scope.");
                            if (currentDefault.Status != "active")
                                currentDefault = null;
                            else if (!await _context.Departments.AnyAsync(x =>
                                x.Id == currentDefault.DepartmentId && x.OrganizationId == configuration.InitialOrganizationId
                                && x.Status == "active", token))
                                throw InvalidScope("The existing default department is not active.");
                        }
                    }
                    var equivalent = user is not null && ProfileMatches(user, request.Profile)
                        && membership is not null && department is not null && currentDefault is not null;
                    if (!equivalent && (user is null
                        ? request.ExpectedAuthorizationVersion is not (null or 0)
                        : user.AuthorizationVersion != request.ExpectedAuthorizationVersion))
                        throw VersionConflict();
                    if (!equivalent)
                    {
                        if (user is null)
                        {
                            if (await _context.Users.AnyAsync(x => x.Id == request.ObjectId, token))
                                throw InvalidScope("The immutable identity key is already in use.");
                            user = UserRepository.CreateEntraProfile(request.TenantId, request.ObjectId, request.Profile, now);
                            _context.Users.Add(user);
                        }
                        ApplyProfile(user, request.Profile);
                        user.LastLogin = now;
                        if (department is null)
                        {
                            department = new DepartmentMembership
                            {
                                UserId = user.Id, OrganizationId = configuration.InitialOrganizationId,
                                DepartmentId = configuration.InitialDepartmentId, UpdatedBy = request.ObjectId,
                                EffectiveAt = now,
                            };
                            _context.DepartmentMemberships.Add(department);
                        }
                        if (membership is null)
                        {
                            membership = new OrganizationMembership
                            {
                                UserId = user.Id, OrganizationId = configuration.InitialOrganizationId,
                                UpdatedBy = request.ObjectId, EffectiveAt = now,
                            };
                            _context.OrganizationMemberships.Add(membership);
                        }
                        if (currentDefault is null)
                        {
                            membership.DefaultDepartmentMembershipId = department.Id;
                            membership.DefaultDepartmentMembership = department;
                            membership.UpdatedBy = request.ObjectId;
                        }
                        user.AuthorizationVersion++;
                        _context.ProcessingEvents.Add(new ProcessingEvent
                        {
                            Actor = request.ObjectId, EventType = "auth.admin.context.provisioned",
                            EntityType = "User", EntityId = user.Id, CorrelationId = request.CorrelationId, Timestamp = now,
                            PayloadJson = JsonSerializer.Serialize(new
                            {
                                PrincipalObjectId = configuration.GroupObjectId, SubjectObjectId = request.ObjectId,
                                configuration.EnvironmentKey, configuration.SqlTargetKey, configuration.AuthorityVersion,
                                user.AuthorizationVersion, Phase = "automatic_context", Result = "succeeded",
                            }),
                        });
                        await _context.SaveChangesAsync(token);
                    }
                    return await ReadAggregateAsync(user!, new StoredAuthority(true, Array.Empty<string>()), token);
                }, cancellationToken, IsolationLevel.Serializable);
            }
            catch (Exception exception) when (attempt < 5 && IsAutomaticRetry(exception))
            {
                _context.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
            catch (Exception exception)
            {
                _context.ChangeTracker.Clear();
                try
                {
                    _context.ProcessingEvents.Add(new ProcessingEvent
                    {
                        Actor = request.ObjectId, EventType = "auth.admin.group.denied",
                        EntityType = "Authorization", EntityId = request.ObjectId, CorrelationId = request.CorrelationId,
                        Timestamp = _clock.GetUtcNow().UtcDateTime,
                        PayloadJson = JsonSerializer.Serialize(new { request.TenantId, SubjectObjectId = request.ObjectId,
                            AuthorityVersion = request.ExpectedAuthorityVersion, Result = "denied",
                            ReasonCode = exception is EntraAccessManagementException denied ? denied.Code : "internal_error" }),
                    });
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch { _context.ChangeTracker.Clear(); } // Never mask the original denial or produce an allow.
                throw;
            }
        }
    }

    private bool IsAutomaticRetry(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || (_context.Database.IsSqlite()
            && (exception is SqliteException { SqliteErrorCode: 5 or 6 }
                || exception is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 or 19 } }));

    private static void RequireEvidence(ConvergeSystemAdministratorContext request,
        SystemAdministrationConfiguration configuration, DateTime now)
    {
        var evidence = request.Evidence;
        if (evidence.AccountEnabled == false)
            throw new EntraAccessManagementException("identity_disabled", "This directory identity is disabled.");
        if (!evidence.Complete || !evidence.CompositionValidated || evidence.AccountEnabled != true || evidence.UserType != "Member"
            || evidence.TenantId != configuration.TenantId || evidence.GroupObjectId != configuration.GroupObjectId
            || evidence.ActorObjectId != request.ObjectId || evidence.AuthorityVersion != configuration.AuthorityVersion
            || !evidence.DirectMemberObjectIds.Contains(request.ObjectId) || !evidence.EligibleUserObjectIds.Contains(request.ObjectId)
            || evidence.CollectionStartedAt > evidence.CollectedAt || evidence.CollectedAt > now
            || evidence.ExpiresAt <= now || evidence.ExpiresAt > evidence.CollectionStartedAt.AddMinutes(5))
            throw new EntraAccessManagementException("group_evidence_unavailable",
                "Current directory access could not be verified. Retry or contact the deployment operator.");
    }

    public async Task<EntraAccessPage> ListAsync(
        AccessManagementActor actor,
        EntraAccessSearch search,
        CancellationToken cancellationToken = default)
    {
        var authority = await ReadAuthorityAsync(actor, cancellationToken);
        RequireAdministrator(authority);
        if (search.OrganizationId is not null)
            RequireOrganization(authority, search.OrganizationId);

        var query = _context.Users.AsNoTracking().Where(user =>
            user.AuthenticationProvider == "entra"
            && user.EntraTenantId == actor.TenantId);

        if (!string.IsNullOrWhiteSpace(search.Search))
        {
            var text = search.Search.Trim();
            query = query.Where(user =>
                user.Username.Contains(text)
                || user.FullName.Contains(text)
                || user.Email.Contains(text));
        }

        if (!string.IsNullOrWhiteSpace(search.Cursor))
            query = query.Where(user => string.Compare(user.Id, search.Cursor) > 0);

        if (search.OrganizationId is not null)
        {
            query = query.Where(user => user.OrganizationMemberships.Any(membership =>
                membership.OrganizationId == search.OrganizationId && membership.Status == "active"));
        }

        query = search.Status switch
        {
            "disabled" => query.Where(user => !user.IsActive),
            "pending" => query.Where(user => user.IsActive
                && !user.OrganizationMemberships.Any(membership => membership.Status == "active")),
            "active" => query.Where(user => user.IsActive
                && user.OrganizationMemberships.Any(membership => membership.Status == "active")),
            _ => query,
        };

        if (!authority.GlobalAdmin)
        {
            query = query.Where(user =>
                !user.OrganizationMemberships.Any(membership => membership.Status == "active")
                || user.OrganizationMemberships.Any(membership =>
                    membership.Status == "active"
                    && authority.OrganizationAdminIds.Contains(membership.OrganizationId)));
        }

        var users = await query.OrderBy(user => user.Id).Take(search.Limit + 1).ToListAsync(cancellationToken);
        var pageUsers = users.Take(search.Limit).ToArray();
        var items = new List<EntraAccessAggregate>(pageUsers.Length);
        foreach (var user in pageUsers)
        {
            var aggregate = await ReadAggregateAsync(user, authority, cancellationToken);
            items.Add(aggregate);
        }

        return new EntraAccessPage(
            items,
            users.Count > search.Limit ? pageUsers[^1].Id : null);
    }

    public async Task<EntraAccessAggregate?> GetAsync(
        AccessManagementActor actor,
        string objectId,
        CancellationToken cancellationToken = default)
    {
        var authority = await ReadAuthorityAsync(actor, cancellationToken);
        RequireAdministrator(authority);
        var target = await FindTargetAsync(actor.TenantId, objectId, cancellationToken);
        if (target is null)
            return null;

        var aggregate = await ReadAggregateAsync(target, authority, cancellationToken);
        if (!authority.GlobalAdmin
            && aggregate.Organizations.Count == 0
            && await HasActiveMembershipOutsideAuthorityAsync(target.Id, authority, cancellationToken))
        {
            return null;
        }

        return aggregate;
    }

    public async Task<EntraAccessAggregate> UpdateUserAsync(
        AccessManagementActor actor,
        string objectId,
        UpdateEntraAccessUser request,
        CancellationToken cancellationToken = default)
    {
        return await _context.ExecuteInTransactionAsync(async token =>
        {
            var (authority, target) = await LockAuthorityAndTargetAsync(actor, objectId, token);
            if (!authority.GlobalAdmin)
                throw Forbidden("Application Admin access is required.");

            var equivalent = UserUpdateMatches(target, request);
            if (target.AuthorizationVersion != request.ExpectedVersion)
            {
                if (!equivalent)
                    throw VersionConflict();
            }
            else if (!equivalent)
            {
                if (request.Profile is not null)
                    ApplyProfile(target, request.Profile);
                if (request.IsActive.HasValue)
                    target.IsActive = request.IsActive.Value;
                target.AuthorizationVersion++;
            }

            var action = request.IsActive switch
            {
                false => "auth.access.disabled",
                true => "auth.access.reactivated",
                _ => "auth.access.updated",
            };
            AppendSuccessAudit(actor, objectId, action, "update_user", target.AuthorizationVersion);
            await _context.SaveChangesAsync(token);
            return await ReadAggregateAsync(target, authority, token);
        }, cancellationToken, IsolationLevel.Serializable);
    }

    public async Task<EntraAccessAggregate> PutOrganizationAccessAsync(
        AccessManagementActor actor,
        string objectId,
        string organizationId,
        PutEntraOrganizationAccess request,
        CancellationToken cancellationToken = default)
    {
        return await _context.ExecuteInTransactionAsync(async token =>
        {
            var (authority, target) = await LockAuthorityAndTargetAsync(actor, objectId, token);
            RequireOrganization(authority, organizationId);
            ValidateDesiredState(authority, organizationId, request);
            await RequireActiveOrganizationAndDepartmentsAsync(organizationId, request, token);
            var hadActiveMembership = await HasActiveOrganizationMembershipAsync(
                target.Id, organizationId, token);

            var equivalent = await OrganizationAccessMatchesAsync(
                target, organizationId, request, token);
            if (target.AuthorizationVersion != request.ExpectedVersion)
            {
                if (!equivalent)
                    throw VersionConflict();
            }
            else if (!equivalent)
            {
                ApplyProfile(target, request.Profile);
                await ConvergeMembershipAsync(actor, target, organizationId, request, token);
                await ConvergeDelegatedRolesAsync(actor, target, organizationId, request, token);
                target.AuthorizationVersion++;
            }

            var action = !hadActiveMembership && request.Membership.Status == "active"
                ? "auth.access.onboarded"
                : "auth.access.updated";
            AppendSuccessAudit(
                actor, objectId, action, "put_organization_access", target.AuthorizationVersion, organizationId);
            await _context.SaveChangesAsync(token);
            return await ReadAggregateAsync(target, authority, token);
        }, cancellationToken, IsolationLevel.Serializable);
    }

    public async Task<EntraAccessAggregate> RevokeRoleAsync(
        AccessManagementActor actor,
        string objectId,
        string organizationId,
        string assignmentId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return await _context.ExecuteInTransactionAsync(async token =>
        {
            var (authority, target) = await LockAuthorityAndTargetAsync(actor, objectId, token);
            RequireOrganization(authority, organizationId);
            var assignment = await _context.RoleAssignments.SingleOrDefaultAsync(item =>
                item.Id == assignmentId
                && item.UserId == target.Id
                && item.OrganizationId == organizationId
                && item.Source == "delegated", token);
            if (assignment is null)
                throw NotFound("The delegated role assignment was not found.");

            if (target.AuthorizationVersion != expectedVersion)
            {
                if (assignment.Status != "revoked")
                    throw VersionConflict();
            }
            else if (assignment.Status == "active")
            {
                assignment.Status = "revoked";
                assignment.RevokedAt = DateTime.UtcNow;
                assignment.UpdatedAt = DateTime.UtcNow;
                assignment.UpdatedBy = actor.ObjectId;
                target.AuthorizationVersion++;
            }

            AppendSuccessAudit(
                actor, objectId, "auth.assignment.revoked", "revoke_role", target.AuthorizationVersion, organizationId);
            await _context.SaveChangesAsync(token);
            return await ReadAggregateAsync(target, authority, token);
        }, cancellationToken, IsolationLevel.Serializable);
    }

    public async Task RecordFailureAuditAsync(
        AccessManagementActor actor,
        string objectId,
        string operation,
        string code,
        CancellationToken cancellationToken = default)
    {
        _context.ProcessingEvents.Add(new ProcessingEvent
        {
            Actor = actor.ObjectId,
            EventType = "auth.access.failed",
            EntityType = "entra_access_user",
            EntityId = objectId,
            CorrelationId = actor.CorrelationId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                operation,
                code,
                result = "failed",
                tenantId = actor.TenantId,
                targetObjectId = objectId,
            }),
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<(StoredAuthority Authority, User Target)> LockAuthorityAndTargetAsync(
        AccessManagementActor actor,
        string targetObjectId,
        CancellationToken cancellationToken)
    {
        var users = await LockUsersAsync(actor.TenantId, actor.ObjectId, targetObjectId, cancellationToken);
        var actorUser = users.SingleOrDefault(user => user.EntraObjectId == actor.ObjectId);
        if (actorUser is null || !actorUser.IsActive)
            throw Forbidden("The actor is not an active Entra identity.");
        var target = users.SingleOrDefault(user => user.EntraObjectId == targetObjectId);
        if (target is null)
            throw NotFound("The tenant-verified Entra profile was not found.");
        var authority = await ReadAuthorityAsync(actor, actorUser, cancellationToken);
        return (authority, target);
    }

    private async Task<IReadOnlyList<User>> LockUsersAsync(
        string tenantId,
        string actorObjectId,
        string targetObjectId,
        CancellationToken cancellationToken)
    {
        if (_context.Database.IsSqlServer())
        {
            return await _context.Users.FromSqlInterpolated($$"""
                SELECT * FROM [talentmatch].[Users] WITH (UPDLOCK, HOLDLOCK)
                WHERE [AuthenticationProvider] = 'entra'
                  AND [EntraTenantId] = {{tenantId}}
                  AND ([EntraObjectId] = {{actorObjectId}} OR [EntraObjectId] = {{targetObjectId}})
                """)
                .OrderBy(user => user.Id)
                .ToListAsync(cancellationToken);
        }

        var objectIds = new[] { actorObjectId, targetObjectId };
        return await _context.Users
            .Where(user => user.AuthenticationProvider == "entra"
                && user.EntraTenantId == tenantId
                && objectIds.Contains(user.EntraObjectId!))
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<StoredAuthority> ReadAuthorityAsync(
        AccessManagementActor actor,
        CancellationToken cancellationToken)
    {
        var actorUser = await _context.Users.SingleOrDefaultAsync(user =>
            user.AuthenticationProvider == "entra"
            && user.EntraTenantId == actor.TenantId
            && user.EntraObjectId == actor.ObjectId,
            cancellationToken);
        if (actorUser is null || !actorUser.IsActive)
            throw Forbidden("The actor is not an active Entra identity.");
        return await ReadAuthorityAsync(actor, actorUser, cancellationToken);
    }

    private async Task<StoredAuthority> ReadAuthorityAsync(
        AccessManagementActor actor,
        User actorUser,
        CancellationToken cancellationToken)
    {
        var assignments = await _context.RoleAssignments
            .Where(assignment => assignment.UserId == actorUser.Id
                && assignment.TenantId == actor.TenantId
                && assignment.UserObjectId == actor.ObjectId
                && assignment.Status == "active"
                && (assignment.Role == "admin" || assignment.Role == "organization_admin"))
            .OrderBy(assignment => assignment.Id)
            .ToListAsync(cancellationToken);
        var activeOrganizations = await _context.OrganizationMemberships
            .Where(membership => membership.UserId == actorUser.Id && membership.Status == "active")
            .Select(membership => membership.OrganizationId)
            .ToListAsync(cancellationToken);
        var configurations = await _context.SystemAdministrationConfigurations.Where(x => x.TenantId == actor.TenantId)
            .ToListAsync(cancellationToken);
        var global = false;
        if (configurations.Count == 0)
            global = assignments.Any(assignment => assignment.Role == "admin"); // Retained pre-amendment fixture/bootstrap path.
        else if (actor.AdministrationProof is { } proof && actor.GlobalAdmin)
        {
            var configuration = configurations.SingleOrDefault(x => x.EnvironmentKey == proof.EnvironmentKey && x.SqlTargetKey == proof.SqlTargetKey);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (configuration is not null && proof.AuthorityVersion == configuration.AuthorityVersion
                && proof.AuthorizationVersion == actorUser.AuthorizationVersion && proof.Mode == configuration.Mode
                && proof.ValidUntil > now && proof.ValidUntil <= now.AddMinutes(5))
            {
                if (configuration.Mode == "group" && proof.Source == "system_group")
                    global = await _context.RoleGroupMappings.AnyAsync(x => x.Id == configuration.SystemRoleGroupMappingId
                        && x.TenantId == actor.TenantId && x.GroupObjectId == configuration.GroupObjectId && x.Enabled
                        && x.Purpose == "system_admin" && x.Role == "admin" && x.OrganizationId == null && x.DepartmentId == null, cancellationToken);
                else if (configuration.Mode == "individual" && proof.Source == "legacy_individual"
                    && configuration.LegacyIndividualObjectId == actor.ObjectId)
                    global = assignments.Any(x => x.Role == "admin" && x.Source == "bootstrap");
            }
        }
        return new StoredAuthority(
            global,
            assignments
                .Where(assignment => assignment.Role == "organization_admin"
                    && assignment.OrganizationId is not null
                    && activeOrganizations.Contains(assignment.OrganizationId))
                .Where(assignment => actor.OrganizationAdminIds.Contains(assignment.OrganizationId!, StringComparer.OrdinalIgnoreCase))
                .Select(assignment => assignment.OrganizationId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private async Task<User?> FindTargetAsync(
        string tenantId,
        string objectId,
        CancellationToken cancellationToken) =>
        await _context.Users.AsNoTracking().SingleOrDefaultAsync(user =>
            user.AuthenticationProvider == "entra"
            && user.EntraTenantId == tenantId
            && user.EntraObjectId == objectId,
            cancellationToken);

    private async Task<EntraAccessAggregate> ReadAggregateAsync(
        User target,
        StoredAuthority authority,
        CancellationToken cancellationToken)
    {
        var memberships = await _context.OrganizationMemberships.AsNoTracking()
            .Where(membership => membership.UserId == target.Id && membership.Status == "active")
            .OrderBy(membership => membership.OrganizationId)
            .ToListAsync(cancellationToken);
        if (!authority.GlobalAdmin)
        {
            memberships = memberships
                .Where(membership => authority.OrganizationAdminIds.Contains(membership.OrganizationId))
                .ToList();
        }

        var organizationIds = memberships.Select(membership => membership.OrganizationId).ToArray();
        var departments = await _context.DepartmentMemberships.AsNoTracking()
            .Where(membership => membership.UserId == target.Id
                && membership.Status == "active"
                && organizationIds.Contains(membership.OrganizationId))
            .OrderBy(membership => membership.DepartmentId)
            .ToListAsync(cancellationToken);
        var assignments = await _context.RoleAssignments.AsNoTracking()
            .Where(assignment => assignment.UserId == target.Id
                && assignment.Status == "active"
                && assignment.OrganizationId != null
                && organizationIds.Contains(assignment.OrganizationId)
                && (assignment.Source == "delegated" || assignment.Source == "group")
                && assignment.Role != "admin")
            .OrderBy(assignment => assignment.Role)
            .ThenBy(assignment => assignment.DepartmentId)
            .ToListAsync(cancellationToken);

        var organizations = memberships.Select(membership =>
        {
            var departmentMemberships = departments
                .Where(item => item.OrganizationId == membership.OrganizationId)
                .ToArray();
            var defaultDepartmentId = departmentMemberships
                .SingleOrDefault(item => item.Id == membership.DefaultDepartmentMembershipId)?.DepartmentId;
            return new EntraOrganizationAccess(
                membership.OrganizationId,
                membership.Status,
                departmentMemberships.Select(item => item.DepartmentId).ToArray(),
                defaultDepartmentId,
                assignments
                    .Where(item => item.OrganizationId == membership.OrganizationId)
                    .Select(item => new EntraAccessRoleAssignment(
                        item.Id,
                        item.Role,
                        item.OrganizationId!,
                        item.DepartmentId,
                        item.Source,
                        item.Status))
                    .ToArray());
        }).ToArray();

        return new EntraAccessAggregate(
            target.EntraObjectId!,
            target.Username,
            target.FullName,
            string.IsNullOrWhiteSpace(target.Email) ? null : target.Email,
            target.IsActive,
            target.AuthorizationVersion,
            organizations);
    }

    private async Task<bool> HasActiveMembershipOutsideAuthorityAsync(
        string userId,
        StoredAuthority authority,
        CancellationToken cancellationToken) =>
        await _context.OrganizationMemberships.AnyAsync(membership =>
            membership.UserId == userId
            && membership.Status == "active"
            && !authority.OrganizationAdminIds.Contains(membership.OrganizationId),
            cancellationToken);

    private async Task RequireActiveOrganizationAndDepartmentsAsync(
        string organizationId,
        PutEntraOrganizationAccess request,
        CancellationToken cancellationToken)
    {
        var organizationExists = await _context.Organizations.AnyAsync(organization =>
            organization.Id == organizationId && organization.Status == "active", cancellationToken);
        if (!organizationExists)
            throw NotFound("The organization was not found.");
        if (request.Membership.Status == "revoked")
            return;

        var departmentCount = await _context.Departments.CountAsync(department =>
            department.OrganizationId == organizationId
            && department.Status == "active"
            && request.Membership.DepartmentIds.Contains(department.Id),
            cancellationToken);
        if (departmentCount != request.Membership.DepartmentIds.Count)
            throw InvalidScope("Every department must be active in the selected organization.");
    }

    private async Task<bool> OrganizationAccessMatchesAsync(
        User target,
        string organizationId,
        PutEntraOrganizationAccess request,
        CancellationToken cancellationToken)
    {
        if (!ProfileMatches(target, request.Profile))
            return false;

        var membership = await _context.OrganizationMemberships.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == target.Id
                && item.OrganizationId == organizationId
                && item.Status == "active", cancellationToken);
        var departments = await _context.DepartmentMemberships.AsNoTracking()
            .Where(item => item.UserId == target.Id
                && item.OrganizationId == organizationId
                && item.Status == "active")
            .ToListAsync(cancellationToken);
        var roles = await _context.RoleAssignments.AsNoTracking()
            .Where(item => item.UserId == target.Id
                && item.OrganizationId == organizationId
                && item.Source == "delegated"
                && item.Status == "active")
            .ToListAsync(cancellationToken);

        if (request.Membership.Status == "revoked")
            return membership is null && departments.Count == 0 && roles.Count == 0;
        if (membership is null)
            return false;

        var currentDepartmentIds = departments.Select(item => item.DepartmentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!currentDepartmentIds.SetEquals(request.Membership.DepartmentIds))
            return false;
        var defaultDepartmentId = departments
            .SingleOrDefault(item => item.Id == membership.DefaultDepartmentMembershipId)?.DepartmentId;
        if (!string.Equals(defaultDepartmentId, request.Membership.DefaultDepartmentId, StringComparison.OrdinalIgnoreCase))
            return false;
        var currentRoles = roles.Select(RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredRoles = request.RoleAssignments.Select(RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return currentRoles.SetEquals(desiredRoles);
    }

    private async Task ConvergeMembershipAsync(
        AccessManagementActor actor,
        User target,
        string organizationId,
        PutEntraOrganizationAccess request,
        CancellationToken cancellationToken)
    {
        var organizationMembership = await _context.OrganizationMemberships.SingleOrDefaultAsync(item =>
            item.UserId == target.Id && item.OrganizationId == organizationId && item.Status == "active",
            cancellationToken);
        var activeDepartments = await _context.DepartmentMemberships.Where(item =>
            item.UserId == target.Id && item.OrganizationId == organizationId && item.Status == "active")
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);

        if (request.Membership.Status == "revoked")
        {
            var revokedAt = DateTime.UtcNow;
            foreach (var department in activeDepartments)
            {
                department.Status = "revoked";
                department.RevokedAt = revokedAt;
                department.UpdatedBy = actor.ObjectId;
            }
            if (organizationMembership is not null)
            {
                organizationMembership.Status = "revoked";
                organizationMembership.RevokedAt = revokedAt;
                organizationMembership.UpdatedBy = actor.ObjectId;
            }
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var desiredIds = request.Membership.DepartmentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var revokedAtForRemoved = DateTime.UtcNow;
        foreach (var department in activeDepartments.Where(item => !desiredIds.Contains(item.DepartmentId)))
        {
            department.Status = "revoked";
            department.RevokedAt = revokedAtForRemoved;
            department.UpdatedBy = actor.ObjectId;
        }

        var desiredMemberships = activeDepartments
            .Where(item => desiredIds.Contains(item.DepartmentId))
            .ToDictionary(item => item.DepartmentId, StringComparer.OrdinalIgnoreCase);
        foreach (var departmentId in request.Membership.DepartmentIds)
        {
            if (desiredMemberships.ContainsKey(departmentId))
                continue;
            var membership = new DepartmentMembership
            {
                UserId = target.Id,
                OrganizationId = organizationId,
                DepartmentId = departmentId,
                UpdatedBy = actor.ObjectId,
            };
            _context.DepartmentMemberships.Add(membership);
            desiredMemberships.Add(departmentId, membership);
        }
        await _context.SaveChangesAsync(cancellationToken);

        var defaultMembership = desiredMemberships[request.Membership.DefaultDepartmentId!];
        if (organizationMembership is null)
        {
            organizationMembership = new OrganizationMembership
            {
                UserId = target.Id,
                OrganizationId = organizationId,
                DefaultDepartmentMembershipId = defaultMembership.Id,
                DefaultDepartmentMembership = defaultMembership,
                UpdatedBy = actor.ObjectId,
            };
            _context.OrganizationMemberships.Add(organizationMembership);
        }
        else
        {
            organizationMembership.DefaultDepartmentMembershipId = defaultMembership.Id;
            organizationMembership.UpdatedBy = actor.ObjectId;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task ConvergeDelegatedRolesAsync(
        AccessManagementActor actor,
        User target,
        string organizationId,
        PutEntraOrganizationAccess request,
        CancellationToken cancellationToken)
    {
        var existing = await _context.RoleAssignments.Where(item =>
            item.UserId == target.Id
            && item.OrganizationId == organizationId
            && item.Source == "delegated"
            && item.Status == "active")
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var desired = request.Membership.Status == "active"
            ? request.RoleAssignments
            : Array.Empty<EntraDesiredRole>();
        var desiredKeys = desired.Select(RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var revokedAt = DateTime.UtcNow;
        foreach (var assignment in existing.Where(item => !desiredKeys.Contains(RoleKey(item))))
        {
            assignment.Status = "revoked";
            assignment.RevokedAt = revokedAt;
            assignment.UpdatedAt = revokedAt;
            assignment.UpdatedBy = actor.ObjectId;
        }

        var existingKeys = existing.Select(RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var role in desired.Where(role => !existingKeys.Contains(RoleKey(role))))
        {
            _context.RoleAssignments.Add(new RoleAssignment
            {
                UserId = target.Id,
                TenantId = actor.TenantId,
                UserObjectId = target.EntraObjectId!,
                Role = role.Role,
                OrganizationId = organizationId,
                DepartmentId = role.DepartmentId,
                Source = "delegated",
                UpdatedBy = actor.ObjectId,
            });
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> HasActiveOrganizationMembershipAsync(
        string userId,
        string organizationId,
        CancellationToken cancellationToken) =>
        await _context.OrganizationMemberships.AnyAsync(item =>
            item.UserId == userId && item.OrganizationId == organizationId && item.Status == "active",
            cancellationToken);

    private void AppendSuccessAudit(
        AccessManagementActor actor,
        string objectId,
        string action,
        string operation,
        int authorizationVersion,
        string? organizationId = null)
    {
        if (actor.AdministrationProof is { } proof && proof.ValidUntil <= _clock.GetUtcNow().UtcDateTime)
            throw Forbidden("Current administration proof expired. Refresh and retry.");
        _context.ProcessingEvents.Add(new ProcessingEvent
        {
            Actor = actor.ObjectId,
            EventType = action,
            EntityType = "entra_access_user",
            EntityId = objectId,
            CorrelationId = actor.CorrelationId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                operation,
                code = "success",
                result = "success",
                tenantId = actor.TenantId,
                targetObjectId = objectId,
                organizationId,
                authorizationVersion,
            }),
        });
    }

    private static void ValidateDesiredState(
        StoredAuthority authority,
        string organizationId,
        PutEntraOrganizationAccess request)
    {
        if (request.ExpectedVersion < 0)
            throw InvalidScope("A valid expected version is required.");
        var departments = request.Membership.DepartmentIds;
        if (departments.Distinct(StringComparer.OrdinalIgnoreCase).Count() != departments.Count)
            throw InvalidScope("Department memberships must be unique.");
        if (request.Membership.Status == "active")
        {
            if (departments.Count == 0
                || request.Membership.DefaultDepartmentId is null
                || !departments.Contains(request.Membership.DefaultDepartmentId, StringComparer.OrdinalIgnoreCase))
            {
                throw InvalidScope("Active access requires an explicit default from its departments.");
            }
        }
        else if (request.Membership.Status == "revoked")
        {
            if (departments.Count > 0 || request.Membership.DefaultDepartmentId is not null || request.RoleAssignments.Count > 0)
                throw InvalidScope("Revoked access cannot retain departments, a default, or delegated roles.");
        }
        else
        {
            throw InvalidScope("Membership status must be active or revoked.");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in request.RoleAssignments)
        {
            if (role.Role is not ("organization_admin" or "recruiter" or "business_panel"))
                throw InvalidScope("The delegated role is not supported.");
            if (!authority.GlobalAdmin && role.Role == "organization_admin")
                throw Forbidden("Organization Admin access cannot be delegated by this actor.");
            if (role.Role == "recruiter" && role.DepartmentId is null)
                throw InvalidScope("Recruiter access requires a department.");
            if (role.Role == "organization_admin" && role.DepartmentId is not null)
                throw InvalidScope("Organization Admin access cannot have a department.");
            if (role.DepartmentId is not null && !departments.Contains(role.DepartmentId, StringComparer.OrdinalIgnoreCase))
                throw InvalidScope("Role departments must be active memberships.");
            if (!keys.Add(RoleKey(role)))
                throw InvalidScope("Delegated roles must be unique.");
        }
    }

    private static bool UserUpdateMatches(User target, UpdateEntraAccessUser request) =>
        (request.Profile is null || ProfileMatches(target, request.Profile))
        && (!request.IsActive.HasValue || target.IsActive == request.IsActive.Value);

    private static bool ProfileMatches(User target, EntraAccessProfile profile) =>
        target.Username == profile.Username
        && target.FullName == profile.FullName
        && string.Equals(
            string.IsNullOrWhiteSpace(target.Email) ? null : target.Email,
            string.IsNullOrWhiteSpace(profile.Email) ? null : profile.Email,
            StringComparison.OrdinalIgnoreCase);

    private static void ApplyProfile(User target, EntraAccessProfile profile)
        => UserRepository.ApplyEntraProfile(target, profile);

    private static string RoleKey(RoleAssignment assignment) =>
        $"{assignment.Role}:{assignment.DepartmentId ?? string.Empty}";

    private static string RoleKey(EntraDesiredRole assignment) =>
        $"{assignment.Role}:{assignment.DepartmentId ?? string.Empty}";

    private static void RequireAdministrator(StoredAuthority authority)
    {
        if (!authority.GlobalAdmin && authority.OrganizationAdminIds.Count == 0)
            throw Forbidden("Administrative access is required.");
    }

    private static void RequireOrganization(StoredAuthority authority, string organizationId)
    {
        if (!authority.GlobalAdmin
            && !authority.OrganizationAdminIds.Contains(organizationId, StringComparer.OrdinalIgnoreCase))
        {
            throw Forbidden("This organization is outside the actor scope.");
        }
    }

    private static EntraAccessManagementException InvalidScope(string message) =>
        new("invalid_scope", message);

    private static EntraAccessManagementException Forbidden(string message) =>
        new("forbidden", message);

    private static EntraAccessManagementException NotFound(string message) =>
        new("not_found", message);

    private static EntraAccessManagementException VersionConflict() =>
        new("version_conflict", "Authorization state changed. Refresh and retry.");

    private sealed record StoredAuthority(
        bool GlobalAdmin,
        IReadOnlyList<string> OrganizationAdminIds);
}