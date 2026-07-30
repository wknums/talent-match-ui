using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IUserRepository _userRepository;
    private readonly IOrganizationRepository _organizationRepository;
    private readonly IRoleAssignmentRepository _roleAssignmentRepository;
    private readonly IConfiguration _configuration;

    public CurrentUserService(
        IHttpContextAccessor httpContextAccessor,
        IUserRepository userRepository,
        IOrganizationRepository organizationRepository,
        IRoleAssignmentRepository roleAssignmentRepository,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _userRepository = userRepository;
        _organizationRepository = organizationRepository;
        _roleAssignmentRepository = roleAssignmentRepository;
        _configuration = configuration;
    }

    public string? UserId =>
        _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? _httpContextAccessor.HttpContext?.User?.FindFirstValue("oid");
    public string? Username =>
        _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Name)
        ?? _httpContextAccessor.HttpContext?.User?.FindFirstValue("preferred_username")
        ?? _httpContextAccessor.HttpContext?.User?.FindFirstValue("name");
    public string? Role => _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Role);
    public string? Department => _httpContextAccessor.HttpContext?.User?.FindFirstValue("department");
    public bool IsAdmin => Role?.Equals("admin", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<CurrentAuthorizationState?> GetAuthorizationStateAsync(CancellationToken cancellationToken = default)
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var tenantId = principal.FindFirstValue("tid");
        var objectId = principal.FindFirstValue("oid") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var issuedAtValue = principal.FindFirstValue("iat");
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(objectId)
            || !long.TryParse(issuedAtValue, out var issuedAtSeconds))
        {
            return null;
        }

        var roles = principal.FindAll("roles")
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(claim => claim.Value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var groups = principal.FindAll("groups")
            .Select(claim => claim.Value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claims = new CurrentEntraClaims(
            tenantId,
            objectId,
            DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds),
            roles,
            groups);

        var user = await _userRepository.GetByEntraIdentityAsync(tenantId, objectId, cancellationToken);
        if (user is null)
        {
            return new CurrentAuthorizationState(
                claims,
                null,
                [],
                [],
                [],
                [],
                _configuration["AZURE_TENANT_ID"] ?? string.Empty,
                _configuration["ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID"]);
        }

        var organizationMemberships = await _organizationRepository.GetActiveMembershipsAsync(user.Id, cancellationToken);
        var departmentMemberships = await _organizationRepository.GetActiveDepartmentMembershipsAsync(user.Id, cancellationToken);
        var assignments = await _roleAssignmentRepository.GetActiveForIdentityAsync(tenantId, objectId, cancellationToken);
        var mappings = await _roleAssignmentRepository.GetEnabledMappingsAsync(tenantId, groups, cancellationToken);

        return new CurrentAuthorizationState(
            claims,
            user,
            organizationMemberships,
            departmentMemberships,
            assignments,
            mappings,
            _configuration["AZURE_TENANT_ID"] ?? string.Empty,
            _configuration["ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID"]);
    }
}
