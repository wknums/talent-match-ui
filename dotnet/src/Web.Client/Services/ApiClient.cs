using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace TalentMatch.Web.Client.Services;

public class ApiException : Exception
{
    public int StatusCode { get; }
    public string? ErrorCode { get; }
    public string? CorrelationId { get; }

    public ApiException(
        string message,
        int statusCode,
        string? errorCode = null,
        string? correlationId = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        CorrelationId = correlationId;
    }
}

public class ApiClient : INavigationAuditClient
{
    private readonly HttpClient _http;
    private readonly PublicAuthConfiguration _authConfiguration;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ApiClient(HttpClient http)
        : this(http, PublicAuthConfiguration.Simple(http.BaseAddress?.ToString() ?? "http://localhost/"))
    {
    }

    public ApiClient(HttpClient http, PublicAuthConfiguration authConfiguration)
    {
        _http = http;
        _authConfiguration = authConfiguration;
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, string fallbackMessage)
    {
        if (response.IsSuccessStatusCode) return;

        var correlationId = response.Headers.TryGetValues("X-Correlation-ID", out var values)
            ? values.FirstOrDefault()
            : null;
        var details = new ApiErrorDetails(fallbackMessage, null, correlationId);
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
                details = ExtractErrorDetails(body, fallbackMessage, correlationId);
        }
        catch { /* use fallback */ }

        throw new ApiException(details.Message, (int)response.StatusCode, details.ErrorCode, details.CorrelationId);
    }

    private static ApiErrorDetails ExtractErrorDetails(
        string body,
        string fallbackMessage,
        string? headerCorrelationId = null)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.String)
                return new(root.GetString() ?? fallbackMessage, null, headerCorrelationId);

            if (root.ValueKind == JsonValueKind.Object)
            {
                var correlationId = root.TryGetProperty("correlationId", out var correlation)
                    && correlation.ValueKind == JsonValueKind.String
                        ? correlation.GetString()
                        : headerCorrelationId;
                var errorCode = root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : null;

                if (root.TryGetProperty("message", out var canonicalMessage)
                    && canonicalMessage.ValueKind == JsonValueKind.String)
                {
                    return new(
                        canonicalMessage.GetString() ?? fallbackMessage,
                        errorCode,
                        correlationId);
                }

                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    var messages = errors.EnumerateObject()
                        .SelectMany(property => property.Value.ValueKind == JsonValueKind.Array
                            ? property.Value.EnumerateArray()
                                .Where(item => item.ValueKind == JsonValueKind.String)
                                .Select(item => item.GetString())
                                .Where(message => !string.IsNullOrWhiteSpace(message))
                                .Select(message => $"{property.Name}: {message}")
                            : Enumerable.Empty<string>())
                        .ToList();

                    if (messages.Count > 0)
                        return new(string.Join(" ", messages), errorCode, correlationId);
                }

                if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    return new(title.GetString() ?? fallbackMessage, errorCode, correlationId);

                if (!string.IsNullOrWhiteSpace(errorCode))
                    return new(errorCode, errorCode, correlationId);
            }
        }
        catch
        {
            // Fall back to the raw response body when it is not valid JSON.
        }

        return new(body.Trim().Trim('"'), null, headerCorrelationId);
    }

    // Auth
    public async Task<UserInfo?> LoginAsync(string username, string password)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<UserInfo>();
    }

    public async Task LogoutAsync()
    {
        var response = await _http.PostAsync("/api/auth/logout", null);
        if (_authConfiguration.IsEntra)
            await EnsureSuccessOrThrowAsync(response, "Failed to record logout.");
    }

    public async Task<UserInfo?> GetCurrentUserAsync()
    {
        try
        {
            return (await GetCurrentUserResultAsync()).User;
        }
        catch (AccessTokenNotAvailableException) when (_authConfiguration.IsEntra)
        {
            return null;
        }
        catch when (_authConfiguration.IsSimple)
        {
            return null;
        }
    }

    public async Task<CurrentUserResult> GetCurrentUserResultAsync()
    {
        var response = await _http.GetAsync("/api/auth/me");
        var headerCorrelationId = response.Headers.TryGetValues("X-Correlation-ID", out var values)
            ? values.FirstOrDefault()
            : null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var details = string.IsNullOrWhiteSpace(body)
                ? new ApiErrorDetails("Sign in is required.", null, headerCorrelationId)
                : ExtractErrorDetails(body, "Sign in is required.", headerCorrelationId);
            return new(null, (int)response.StatusCode, details.ErrorCode, details.Message, details.CorrelationId);
        }

        if (_authConfiguration.IsEntra)
        {
            var context = await response.Content.ReadFromJsonAsync<AuthorizationContextResponse>(JsonOptions);
            if (context is null)
                throw new ApiException("The authorization context response was empty.", 200, correlationId: headerCorrelationId);

            return new(context.ToUserInfo(), 200, null, null, headerCorrelationId);
        }

        var user = await response.Content.ReadFromJsonAsync<UserInfo>(JsonOptions);
        return new(user, 200, null, null, headerCorrelationId);
    }

    public async Task<IReadOnlyList<OrganizationAdministrationOrganization>> ListJobScopeOrganizationsAsync()
    {
        var response = await _http.GetAsync("/api/auth/me");
        var context = await ReadRequiredResponseAsync<AuthorizationContextResponse>(
            response, "Failed to load authorized job scopes.");
        var recruiterScopes = context.Authorizations
            .Where(authorization =>
                authorization.Role == "recruiter"
                && authorization.OrganizationId is not null
                && authorization.DepartmentId is not null)
            .Select(authorization => (authorization.OrganizationId!, authorization.DepartmentId!))
            .ToHashSet();
        var organizations = context.Memberships
            .Where(membership => recruiterScopes.Any(scope => scope.Item1 == membership.OrganizationId))
            .Select(membership => new OrganizationAdministrationOrganization(
                membership.OrganizationId,
                membership.OrganizationName,
                "active",
                membership.Departments
                    .Where(department => recruiterScopes.Contains((membership.OrganizationId, department.DepartmentId)))
                    .Select(department => new OrganizationAdministrationDepartment(
                        department.DepartmentId,
                        membership.OrganizationId,
                        department.DepartmentName,
                        "active"))
                    .ToArray()))
            .ToDictionary(organization => organization.Id, StringComparer.OrdinalIgnoreCase);

        var hasAdministrativeScope = context.GlobalRole == "admin"
            || context.Authorizations.Any(authorization => authorization.Role == "organization_admin");
        if (hasAdministrativeScope)
        {
            foreach (var organization in await ListOrganizationsAsync())
                organizations[organization.Id] = organization;
        }

        return organizations.Values.OrderBy(organization => organization.Name).ToArray();
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/change-password", new { CurrentPassword = currentPassword, NewPassword = newPassword });
        return response.IsSuccessStatusCode;
    }

    public async Task RequestPasswordResetFromLoginAsync(string username, string? reason = null)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/request-password-reset", new { Username = username, Reason = reason });
        await EnsureSuccessOrThrowAsync(response, "Failed to submit password reset request.");
    }

    // Users
    public async Task<List<UserInfo>> GetUsersAsync()
        => await _http.GetFromJsonAsync<List<UserInfo>>("/api/users") ?? new();

    public async Task CreateUserAsync(string username, string role, string department, string password, string fullName, string email)
    {
        var response = await _http.PostAsJsonAsync("/api/users", new { Username = username, Role = role, Department = department, Password = password, FullName = fullName, Email = email });
        await EnsureSuccessOrThrowAsync(response, "Failed to create user.");
    }

    public async Task DeleteUserAsync(string userId)
    {
        var response = await _http.DeleteAsync($"/api/users/{userId}");
        await EnsureSuccessOrThrowAsync(response, "Failed to delete user.");
    }

    public async Task ResetUserPasswordAsync(string userId, string newPassword)
    {
        var response = await _http.PostAsJsonAsync($"/api/users/{userId}/reset-password", new { NewPassword = newPassword });
        await EnsureSuccessOrThrowAsync(response, "Failed to reset password.");
    }

    public async Task UpdateUserAsync(string userId, string fullName, string email, string role, string department)
    {
        var response = await _http.PutAsJsonAsync($"/api/users/{userId}", new { FullName = fullName, Email = email, Role = role, Department = department });
        await EnsureSuccessOrThrowAsync(response, "Failed to update user.");
    }

    // Entra access management
    public async Task<EntraAccessUserPage> ListEntraAccessUsersAsync(
        string? search = null,
        string? organizationId = null,
        string? status = null,
        string? cursor = null,
        int limit = 25)
    {
        var query = new List<string> { $"limit={limit}" };
        AddQueryValue(query, "search", search);
        AddQueryValue(query, "organizationId", organizationId);
        AddQueryValue(query, "status", status);
        AddQueryValue(query, "cursor", cursor);

        var response = await _http.GetAsync($"/api/access-management/users?{string.Join("&", query)}");
        return await ReadRequiredResponseAsync<EntraAccessUserPage>(response, "Failed to load Entra access profiles.");
    }

    public async Task<EntraAccessUser> GetEntraAccessUserAsync(string objectId)
    {
        var response = await _http.GetAsync($"/api/access-management/users/{Uri.EscapeDataString(objectId)}");
        return await ReadRequiredResponseAsync<EntraAccessUser>(response, "Failed to load the Entra access profile.");
    }

    public async Task<EntraAccessUser> PutEntraOrganizationAccessAsync(
        string objectId,
        string organizationId,
        PutEntraOrganizationAccessRequest request)
    {
        var response = await _http.PutAsJsonAsync(
            $"/api/access-management/users/{Uri.EscapeDataString(objectId)}/organizations/{Uri.EscapeDataString(organizationId)}",
            request,
            JsonOptions);
        return await ReadRequiredResponseAsync<EntraAccessUser>(response, "Failed to update organization access.");
    }

    public async Task<EntraAccessUser> UpdateEntraAccessUserAsync(
        string objectId,
        UpdateEntraAccessUserRequest request)
    {
        var response = await _http.PatchAsJsonAsync(
            $"/api/access-management/users/{Uri.EscapeDataString(objectId)}",
            request,
            JsonOptions);
        return await ReadRequiredResponseAsync<EntraAccessUser>(response, "Failed to update the Entra identity.");
    }

    public async Task<EntraAccessUser> RevokeEntraRoleAssignmentAsync(
        string objectId,
        string organizationId,
        string assignmentId,
        int expectedVersion)
    {
        var response = await _http.DeleteAsync(
            $"/api/access-management/users/{Uri.EscapeDataString(objectId)}/organizations/{Uri.EscapeDataString(organizationId)}/role-assignments/{Uri.EscapeDataString(assignmentId)}?expectedVersion={expectedVersion}");
        return await ReadRequiredResponseAsync<EntraAccessUser>(response, "Failed to revoke the delegated role.");
    }

    // Organization administration
    public async Task<IReadOnlyList<OrganizationAdministrationOrganization>> ListOrganizationsAsync()
    {
        var response = await _http.GetAsync("/api/organizations");
        return await ReadRequiredResponseAsync<IReadOnlyList<OrganizationAdministrationOrganization>>(
            response, "Failed to load organizations.");
    }

    public async Task<OrganizationAdministrationOrganization> CreateOrganizationAsync(
        CreateOrganizationRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/organizations", request, JsonOptions);
        return await ReadRequiredResponseAsync<OrganizationAdministrationOrganization>(
            response, "Failed to create the organization.");
    }

    public async Task<OrganizationAdministrationDepartment> CreateDepartmentAsync(
        string organizationId,
        CreateDepartmentRequest request)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/organizations/{Uri.EscapeDataString(organizationId)}/departments",
            request,
            JsonOptions);
        return await ReadRequiredResponseAsync<OrganizationAdministrationDepartment>(
            response, "Failed to create the department.");
    }

    public async Task<OrganizationAdministrationDepartment> UpdateDepartmentAsync(
        string organizationId,
        string departmentId,
        UpdateDepartmentRequest request)
    {
        var response = await _http.PatchAsJsonAsync(
            $"/api/organizations/{Uri.EscapeDataString(organizationId)}/departments/{Uri.EscapeDataString(departmentId)}",
            request,
            JsonOptions);
        return await ReadRequiredResponseAsync<OrganizationAdministrationDepartment>(
            response, "Failed to update the department.");
    }

    public async Task<OrganizationAdministrationMembership> RegisterOrganizationMembershipAsync(
        string organizationId,
        RegisterOrganizationMembershipRequest request)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/organizations/{Uri.EscapeDataString(organizationId)}/memberships",
            request,
            JsonOptions);
        return await ReadRequiredResponseAsync<OrganizationAdministrationMembership>(
            response, "Failed to update organization membership.");
    }

    public async Task<OrganizationAdministrationRoleAssignment> GrantOrganizationRoleAsync(
        string organizationId,
        GrantOrganizationRoleRequest request)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/organizations/{Uri.EscapeDataString(organizationId)}/role-assignments",
            request,
            JsonOptions);
        return await ReadRequiredResponseAsync<OrganizationAdministrationRoleAssignment>(
            response, "Failed to grant the delegated role.");
    }

    public async Task RevokeOrganizationRoleAsync(string organizationId, string assignmentId)
    {
        var response = await _http.DeleteAsync(
            $"/api/organizations/{Uri.EscapeDataString(organizationId)}/role-assignments/{Uri.EscapeDataString(assignmentId)}");
        await EnsureSuccessOrThrowAsync(response, "Failed to revoke the delegated role.");
    }

    private static void AddQueryValue(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            query.Add($"{name}={Uri.EscapeDataString(value)}");
    }

    private static async Task<T> ReadRequiredResponseAsync<T>(
        HttpResponseMessage response,
        string fallbackMessage)
    {
        await EnsureSuccessOrThrowAsync(response, fallbackMessage);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        if (result is not null)
            return result;

        var correlationId = response.Headers.TryGetValues("X-Correlation-ID", out var values)
            ? values.FirstOrDefault()
            : null;
        throw new ApiException("The server returned an empty response.", (int)response.StatusCode, correlationId: correlationId);
    }

    // Password Reset Requests
    public async Task<List<ResetRequestDto>> GetResetRequestsAsync()
        => await _http.GetFromJsonAsync<List<ResetRequestDto>>("/api/users/reset-requests") ?? new();

    public async Task<bool> SubmitResetRequestAsync(string reason)
    {
        var response = await _http.PostAsJsonAsync("/api/users/reset-requests", new { Reason = reason });
        return response.IsSuccessStatusCode;
    }

    public async Task ResolveResetRequestAsync(string requestId, string action, string? newPassword = null)
    {
        var response = await _http.PutAsJsonAsync($"/api/users/reset-requests/{requestId}", new { Action = action, NewPassword = newPassword });
        await EnsureSuccessOrThrowAsync(response, "Failed to resolve reset request.");
    }

    // Jobs
    public async Task<List<JobSummaryDto>> GetJobsAsync()
        => await _http.GetFromJsonAsync<List<JobSummaryDto>>("/api/jobs", JsonOptions) ?? new();

    public async Task<JobDto?> GetJobAsync(string jobId)
        => await _http.GetFromJsonAsync<JobDto>($"/api/jobs/{jobId}");

    public async Task<bool> CreateJobAsync(CreateJobDto job)
    {
        var response = await _http.PostAsJsonAsync("/api/jobs", job);
        await EnsureSuccessOrThrowAsync(response, "Failed to create job.");
        return true;
    }

    public async Task UpdateJobConfigAsync(string jobId, UpdateConfigDto config)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{jobId}/config", config);
        await EnsureSuccessOrThrowAsync(response, "Failed to update job configuration.");
    }

    public async Task<ProcessJobResponse> ProcessJobAsync(string jobId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/process", null);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new ProcessJobResponse(0, 0, [$"HTTP {(int)response.StatusCode}: {errorBody}"]);
        }
        var result = await response.Content.ReadFromJsonAsync<ProcessJobResponse>();
        return result ?? new ProcessJobResponse(0, 0, ["Empty response from server"]);
    }

    public async Task<ReAggregateResponse> ReAggregateJobAsync(string jobId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/reaggregate", null);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new ReAggregateResponse(0, 0, $"HTTP {(int)response.StatusCode}: {errorBody}");
        }
        return await response.Content.ReadFromJsonAsync<ReAggregateResponse>() ?? new(0, 0, null);
    }

    public async Task<bool> DeleteJobAsync(string jobId)
    {
        var response = await _http.DeleteAsync($"/api/jobs/{jobId}");
        return response.IsSuccessStatusCode;
    }

    public async Task<ExtractSpecResult?> ExtractJobSpecAsync(string fileName, string content, string mimeType)
    {
        var response = await _http.PostAsJsonAsync("/api/jobs/extract-spec", new { FileName = fileName, Content = content, MimeType = mimeType });
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Extraction failed ({(int)response.StatusCode}): {errorBody}");
        }
        return await response.Content.ReadFromJsonAsync<ExtractSpecResult>();
    }

    public async Task<ExtractRubricResult?> ExtractRubricAsync(string fileName, string content, string mimeType)
    {
        var response = await _http.PostAsJsonAsync("/api/jobs/extract-rubric", new { FileName = fileName, Content = content, MimeType = mimeType });
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Rubric extraction failed ({(int)response.StatusCode}): {errorBody}");
        }
        return await response.Content.ReadFromJsonAsync<ExtractRubricResult>();
    }

    public async Task<JobConfigDto?> GetJobConfigAsync(string jobId)
    {
        try { return await _http.GetFromJsonAsync<JobConfigDto>($"/api/jobs/{jobId}/config"); }
        catch { return null; }
    }

    // Applications
    public async Task<List<ApplicationDto>> GetApplicationsAsync(string jobId, string? list = null, string? applicantName = null)
    {
        var queryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(list))
            queryParts.Add($"list={Uri.EscapeDataString(list)}");
        if (!string.IsNullOrWhiteSpace(applicantName))
            queryParts.Add($"applicantName={Uri.EscapeDataString(applicantName)}");

        var requestUri = queryParts.Count == 0
            ? $"/api/jobs/{jobId}/applications"
            : $"/api/jobs/{jobId}/applications?{string.Join("&", queryParts)}";

        var response = await _http.GetAsync(requestUri);
        await EnsureSuccessOrThrowAsync(response, "Failed to load job applications.");
        return await response.Content.ReadFromJsonAsync<List<ApplicationDto>>() ?? new();
    }

    public async Task<ApplicationDto?> GetApplicationAsync(string applicationId)
        => await _http.GetFromJsonAsync<ApplicationDto>($"/api/applications/{applicationId}");

    public async Task<bool> UploadApplicationsAsync(string jobId, MultipartFormDataContent content)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/applications/upload", content);
        return response.IsSuccessStatusCode;
    }

    // Scoring
    public async Task<List<ScoringRunDto>> GetScoringRunsAsync(string applicationId)
        => await _http.GetFromJsonAsync<List<ScoringRunDto>>($"/api/applications/{applicationId}/runs") ?? new();

    public async Task<ReparseScoringRunResultDto?> ReparseScoringRunAsync(string applicationId, string scoringRunId, string? rawJson = null)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/applications/{applicationId}/runs/{scoringRunId}/reparse",
            new { RawJson = rawJson });
        await EnsureSuccessOrThrowAsync(response, "Failed to re-parse scoring run.");
        return await response.Content.ReadFromJsonAsync<ReparseScoringRunResultDto>();
    }

    public async Task<AggregatedResultDto?> GetAggregatedResultAsync(string applicationId)
    {
        try { return await _http.GetFromJsonAsync<AggregatedResultDto>($"/api/applications/{applicationId}/result"); }
        catch { return null; }
    }

    public async Task<ExtractionDto?> GetExtractionAsync(string applicationId)
        => await _http.GetFromJsonAsync<ExtractionDto>($"/api/applications/{applicationId}/extraction");

    // Manual Review
    public async Task<ManualReviewDto?> GetManualReviewAsync(string applicationId)
    {
        try { return await _http.GetFromJsonAsync<ManualReviewDto>($"/api/applications/{applicationId}/manual-review"); }
        catch { return null; }
    }

    public async Task<bool> SaveManualReviewAsync(string applicationId, ManualReviewDto review)
    {
        var response = await _http.PostAsJsonAsync($"/api/applications/{applicationId}/manual-review", review);
        return response.IsSuccessStatusCode;
    }

    // Stats & DLQ
    public async Task<SystemStatsDto?> GetSystemStatsAsync()
        => await _http.GetFromJsonAsync<SystemStatsDto>("/api/stats");

    public async Task<List<RecruiterAnalyticsDto>> GetRecruiterAnalyticsAsync()
    {
        var response = await _http.GetAsync("/api/stats/recruiters");
        return await ReadRequiredResponseAsync<List<RecruiterAnalyticsDto>>(
            response, "Failed to load recruiter analytics.");
    }

    public async Task<List<DepartmentAnalyticsDto>> GetDepartmentAnalyticsAsync()
    {
        var response = await _http.GetAsync("/api/stats/departments");
        return await ReadRequiredResponseAsync<List<DepartmentAnalyticsDto>>(
            response, "Failed to load department analytics.");
    }

    public async Task<List<DlqItemDto>> GetDlqItemsAsync()
        => await _http.GetFromJsonAsync<List<DlqItemDto>>("/api/dlq") ?? new();

    public async Task<bool> RetryDlqItemAsync(string itemId)
    {
        var response = await _http.PostAsync($"/api/dlq/{itemId}/retry", null);
        return response.IsSuccessStatusCode;
    }

    public async Task BulkRetryDlqAsync(List<string> ids)
    {
        var response = await _http.PostAsJsonAsync("/api/dlq/bulk-retry", new { Ids = ids });
        await EnsureSuccessOrThrowAsync(response, "Failed to retry selected DLQ items.");
    }

    public async Task BulkDeleteDlqAsync(List<string> ids)
    {
        var response = await _http.PostAsJsonAsync("/api/dlq/bulk-delete", new { Ids = ids });
        await EnsureSuccessOrThrowAsync(response, "Failed to delete selected DLQ items.");
    }

    public async Task<List<AuditEventDto>> GetAuditEventsAsync(string? entityType = null, string? eventType = null)
        => await _http.GetFromJsonAsync<List<AuditEventDto>>($"/api/audit?entityType={entityType}&eventType={eventType}") ?? new();

    // Scoring Prompts
    public async Task<List<ScoringPromptDto>> GetPromptsAsync(string jobId)
        => await _http.GetFromJsonAsync<List<ScoringPromptDto>>($"/api/jobs/{jobId}/prompts") ?? new();

    public async Task<ScoringPromptDto?> CreatePromptAsync(string jobId, string promptText, string source, string? generationMetadataJson = null)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{jobId}/prompts", new CreatePromptRequest(promptText, source, generationMetadataJson));
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<ScoringPromptDto>();
    }

    public async Task<ScoringPromptDto?> EditPromptAsync(string jobId, string promptId, string promptText)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{jobId}/prompts/{promptId}/edit", new { PromptText = promptText });
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<ScoringPromptDto>();
    }

    public async Task<bool> ActivatePromptAsync(string jobId, string promptId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/prompts/{promptId}/activate", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RatePromptAsync(string jobId, string promptId, int rating, string? comments = null)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{jobId}/prompts/{promptId}/rate", new { Rating = rating, Comments = comments });
        return response.IsSuccessStatusCode;
    }

    public async Task<GeneratePromptResult?> GeneratePromptAsync(string jobId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/prompts/generate", null);
        await EnsureSuccessOrThrowAsync(response, "Failed to generate prompt.");

        var payload = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(payload)) return null;

        var generated = JsonSerializer.Deserialize<GeneratePromptResult>(payload, JsonOptions);
        if (!string.IsNullOrWhiteSpace(generated?.PromptText))
            return generated;

        var fullPrompt = JsonSerializer.Deserialize<ScoringPromptDto>(payload, JsonOptions);
        if (!string.IsNullOrWhiteSpace(fullPrompt?.PromptText))
            return new GeneratePromptResult(fullPrompt.PromptText, fullPrompt.GenerationMetadataJson);

        throw new ApiException("Prompt generation succeeded but returned an unexpected payload.", (int)response.StatusCode);
    }

    public async Task<bool> ApprovePromptForProductionAsync(string jobId, string promptId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/prompts/{promptId}/approve-production", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SetProductionPromptAsync(string jobId, string promptId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/prompts/{promptId}/set-production", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<PromptTestRunDto?> CreateTestRunAsync(string jobId, string promptId, object files)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{jobId}/prompts/{promptId}/test-runs", new { Files = files });
        await EnsureSuccessOrThrowAsync(response, "Failed to create prompt test run.");
        return await response.Content.ReadFromJsonAsync<PromptTestRunDto>();
    }

    public async Task<List<PromptTestRunDto>> GetTestRunsAsync(string jobId, string promptId)
        => await _http.GetFromJsonAsync<List<PromptTestRunDto>>($"/api/jobs/{jobId}/prompts/{promptId}/test-runs") ?? new();

    public async Task<PromptTestRunDto?> GetTestRunAsync(string jobId, string promptId, string testRunId)
        => await _http.GetFromJsonAsync<PromptTestRunDto>($"/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRunId}");

    public async Task<PromptTestRunDetailDto?> GetTestRunDetailAsync(string jobId, string promptId, string testRunId)
        => await _http.GetFromJsonAsync<PromptTestRunDetailDto>($"/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRunId}");

    public async Task<ReconcilePromptTestRunsResponseDto?> ReconcileTestRunsAsync(string jobId, string promptId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/prompts/{promptId}/test-runs/reconcile", null);
        await EnsureSuccessOrThrowAsync(response, "Failed to reconcile prompt test runs.");
        return await response.Content.ReadFromJsonAsync<ReconcilePromptTestRunsResponseDto>();
    }

    public async Task<bool> ApproveTestRunAsync(string jobId, string promptId, string testRunId)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRunId}/approve", new { ReviewNotes = (string?)null });
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RetryTestRunAsync(string jobId, string promptId, string testRunId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRunId}/retry", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<NavigationAuditResult> RecordNavigationAsync(
        NavigationAuditRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("/api/navigation/audit", new
        {
            action = request.Action.ToString().ToLowerInvariant(),
            correlationId = request.CorrelationId,
            requestedAt = request.RequestedAt,
        }, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, "Navigation change could not be recorded.");

        var result = await response.Content.ReadFromJsonAsync<NavigationAuditResponse>(
            JsonOptions, cancellationToken);
        return new NavigationAuditResult(
            Guid.TryParse(result?.CorrelationId, out var correlationId) ? correlationId : request.CorrelationId);
    }

    public async Task<List<DocumentDto>> GetDocumentsAsync(string applicationId)
    {
        try { return await _http.GetFromJsonAsync<List<DocumentDto>>($"/api/applications/{applicationId}/documents") ?? new(); }
        catch { return new(); }
    }
}

public sealed record NavigationAuditResponse(string? CorrelationId);

// DTOs
public sealed record PublicAuthConfiguration(
    string AuthMode,
    string? TenantId = null,
    string? ClientId = null,
    string? Authority = null,
    string? ApiScope = null,
    string ApiBaseAddress = "",
    string? ProviderError = null)
{
    public bool IsSimple => string.Equals(AuthMode, "simple", StringComparison.OrdinalIgnoreCase);
    public bool IsEntra => string.Equals(AuthMode, "entra", StringComparison.OrdinalIgnoreCase);
    public bool IsValid => ProviderError is null && (IsSimple || IsEntra);
    public string ApiOrigin => new Uri(ApiBaseAddress).GetLeftPart(UriPartial.Authority);
    public string ApiAuthorizationUrl => $"{ApiOrigin}/api";

    public PublicAuthConfiguration WithBaseAddress(string baseAddress) => this with { ApiBaseAddress = baseAddress };

    public PublicAuthConfiguration Validate()
    {
        if (IsSimple)
            return this;

        if (!IsEntra)
            return this with { ProviderError = $"Unsupported authentication mode '{AuthMode}'." };

        if (string.IsNullOrWhiteSpace(TenantId)
            || string.IsNullOrWhiteSpace(ClientId)
            || string.IsNullOrWhiteSpace(Authority)
            || string.IsNullOrWhiteSpace(ApiScope))
        {
            return this with { ProviderError = "Microsoft Entra authentication configuration is incomplete." };
        }

        return this;
    }

    public static PublicAuthConfiguration Simple(string baseAddress)
        => new("simple", ApiBaseAddress: baseAddress);

    public static PublicAuthConfiguration Failed(string baseAddress, string message)
        => new("unavailable", ApiBaseAddress: baseAddress, ProviderError: message);
}

public sealed record CurrentUserResult(
    UserInfo? User,
    int StatusCode,
    string? ErrorCode,
    string? Message,
    string? CorrelationId);

public sealed record AuthorizationContextResponse(
    string UserId,
    string TenantId,
    string ObjectId,
    string Username,
    string FullName,
    string? Email,
    string? GlobalRole,
    int AuthorizationVersion,
    IReadOnlyList<OrganizationMembershipResponse> Memberships,
    IReadOnlyList<ScopedAuthorizationResponse> Authorizations,
    DateTimeOffset TokenIssuedAt,
    DateTimeOffset RefreshRequiredAt)
{
    public UserInfo ToUserInfo()
    {
        var primaryAuthorization = Authorizations
            .OrderByDescending(authorization => GetRoleRank(authorization.Role))
            .FirstOrDefault();
        var role = GlobalRole ?? primaryAuthorization?.Role ?? "business_panel";
        var department = Memberships
            .Select(membership => membership.Departments.FirstOrDefault(candidate =>
                candidate.DepartmentId == membership.DefaultDepartmentId))
            .FirstOrDefault(candidate => candidate is not null)
            ?.DepartmentName
            ?? string.Empty;

        return new UserInfo(UserId, Username, role, department, FullName, Email ?? string.Empty);
    }

    private static int GetRoleRank(string role) => role switch
    {
        "admin" => 4,
        "organization_admin" => 3,
        "recruiter" => 2,
        "business_panel" => 1,
        _ => 0,
    };
}

public sealed record OrganizationMembershipResponse(
    string OrganizationId,
    string OrganizationName,
    string DefaultDepartmentId,
    IReadOnlyList<DepartmentMembershipResponse> Departments);

public sealed record DepartmentMembershipResponse(string DepartmentId, string DepartmentName);

public sealed record ScopedAuthorizationResponse(
    string Role,
    string RoleLabel,
    string? OrganizationId,
    string? DepartmentId,
    string AssignmentSource);

public sealed record EntraAccessProfile(string Username, string FullName, string? Email);

public sealed record EntraAccessMembership(
    string Status,
    IReadOnlyList<string> DepartmentIds,
    string? DefaultDepartmentId);

public sealed record EntraDesiredRole(string Role, string? DepartmentId);

public sealed record PutEntraOrganizationAccessRequest(
    int ExpectedVersion,
    EntraAccessProfile Profile,
    EntraAccessMembership Membership,
    IReadOnlyList<EntraDesiredRole> RoleAssignments);

public sealed record UpdateEntraAccessUserRequest(
    int ExpectedVersion,
    EntraAccessProfile? Profile = null,
    bool? IsActive = null);

public sealed record EntraAccessUserPage(
    IReadOnlyList<EntraAccessUser> Items,
    string? NextCursor);

public sealed record EntraAccessUser(
    string ObjectId,
    string Username,
    string FullName,
    string? Email,
    bool IsActive,
    int AuthorizationVersion,
    IReadOnlyList<EntraOrganizationAccess> Organizations);

public sealed record EntraOrganizationAccess(
    string OrganizationId,
    string Status,
    IReadOnlyList<string> DepartmentIds,
    string? DefaultDepartmentId,
    IReadOnlyList<EntraAccessRoleAssignment> RoleAssignments);

public sealed record EntraAccessRoleAssignment(
    string Id,
    string Role,
    string OrganizationId,
    string? DepartmentId,
    string Source,
    string Status);

public sealed record OrganizationAdministrationOrganization(
    string Id,
    string Name,
    string Status,
    IReadOnlyList<OrganizationAdministrationDepartment> Departments);

public sealed record OrganizationAdministrationDepartment(
    string Id,
    string OrganizationId,
    string Name,
    string Status);

public sealed record OrganizationAdministrationMembership(
    string UserObjectId,
    string OrganizationId,
    IReadOnlyList<string> DepartmentIds,
    string DefaultDepartmentId);

public sealed record OrganizationAdministrationRoleAssignment(
    string Id,
    string UserObjectId,
    string Role,
    string OrganizationId,
    string? DepartmentId,
    string Source,
    string Status);

public sealed record CreateOrganizationRequest(string Name, string InitialDepartmentName);
public sealed record CreateDepartmentRequest(string Name);
public sealed record UpdateDepartmentRequest(string? Name = null, string? Status = null);
public sealed record RegisterOrganizationMembershipRequest(
    string UserObjectId,
    IReadOnlyList<string> DepartmentIds,
    string DefaultDepartmentId);
public sealed record GrantOrganizationRoleRequest(
    string UserObjectId,
    string Role,
    string? DepartmentId);

internal sealed record ApiErrorDetails(string Message, string? ErrorCode, string? CorrelationId);

public record UserInfo(string Id, string Username, string Role, string Department, string FullName, string Email, DateTime? LastLogin = null);
public record JobDto(string Id, string JobCode, string Title, string Department, string Organisation, DateTime PostingDate, string Status, string? CurrentConfigVersionId, string? JobDescription, string? CreatedBy, DateTime CreatedAt);
public record JobSummaryDto(string Id, string JobCode, string Title, string Department, string Organisation, DateTime PostingDate, string Status, string? CurrentConfigVersionId, string? JobDescription, string? CreatedBy, DateTime CreatedAt, string CreatedByName, int TotalApplications, int CompletedApplications);
public record CreateJobDto(string Title, string Department, string Organisation, DateTime PostingDate, string? RubricJson, string? MustHavesJson, string? DesiredCriteriaJson, int ScoringRunCount, string AggregationStrategy, double LonglistThreshold, double ShortlistThreshold, double VarianceThreshold, string? JobDescription, string? OrganizationId = null, string? DepartmentId = null);
public record UpdateConfigDto(string? RubricJson, string? MustHavesJson, string? DesiredCriteriaJson, int ScoringRunCount, string AggregationStrategy, double LonglistThreshold, double ShortlistThreshold, double VarianceThreshold, string? RubricApprovalStatus = null);
public record ApplicationDto(string Id, string JobId, string CandidateRef, string? CandidateName, string? CandidateEmail, string Status, double? FinalScore, string? FinalDecision, double? Variance, DateTime CreatedAt, string? LastError = null, string? TestRunId = null);
public record ScoringRunDto(string Id, int RunIndex, double TotalScore, string CategoryScoresJson, string MustHaveEvaluationJson, string EvidenceCitationsJson, string ImprovementTipsJson, string AiModelId, string PromptVersion, int InputTokens, int OutputTokens, DateTime CreatedAt = default);
public record ReparseScoringRunResultDto(ScoringRunDto ScoringRun, bool FallbackParsingActivated, bool EligibilityFallbackActivated, bool TotalScoreFallbackActivated, bool GateDetected, string EligibilityPath, string Source);
public record AggregatedResultDto(string Id, double FinalScore, string Decision, double Variance, double Confidence, string ConsolidatedRationale, string MergedImprovementTipsJson);
public record DocumentDto(string Id, string FileName, string FileType, long FileSize, string? ContentBase64);
public record ExtractionDto(string Id, string NormalisedText, double ConfidenceScore, string Status);
public record ManualReviewDto(string RubricScoresJson, string OverallComment, double? AdjustedFinalScore, string AuditTrailJson, bool HumanEdited = false, string? FinalDecision = null);
public record SystemStatsDto(int Queued, int Extracting, int Scoring, int Aggregating, int Completed, int NeedsManualReview, int Failed, int TotalJobs, int TotalApplications);
public record DlqItemDto(string Id, string EntityType, string EntityId, string FailureReason, int RetryCount, DateTime CreatedAt);
public record AuditEventDto(string Id, string Actor, string EventType, string EntityType, string EntityId, DateTime Timestamp, string CorrelationId);
public record ResetRequestDto(string Id, string UserId, string Username, string Reason, string Status, DateTime CreatedAt);
public record JobConfigDto(string? RubricJson, string? MustHavesJson, string? DesiredCriteriaJson, int ScoringRunCount, string AggregationStrategy, double LonglistThreshold, double ShortlistThreshold, double VarianceThreshold, string? RubricApprovalStatus = null, string? RubricSource = null);
public record ExtractSpecResult(string? Title, string? Department, string? Organisation, string? JobDescription, List<MustHaveItem>? MustHaves, List<DesiredCriterionItem>? DesiredCriteria, List<RubricCategoryItem>? Rubric);
public record ExtractRubricResult(string? Title, List<RubricCategoryItem>? Categories);
public record MustHaveItem(string Criterion, string? Description);
public record DesiredCriterionItem(string Qualification, string? Description);
public record RubricCategoryItem(string Name, double Weight, string? Description);
public record ProcessJobResponse(int Processed, int Total, List<string> Errors);
public record ReAggregateResponse(int Updated, int Total, string? Error);
public record ScoringPromptDto(string Id, string JobId, int VersionNumber, string PromptText, string Status, DateTime CreatedAt, DateTime LastModifiedAt, string Author, int? Rating, string? Comments, string Source, string? GenerationMetadataJson);
public record PromptTestRunDto(string Id, string JobId, string PromptId, string Status, string ApplicationIdsJson, DateTime CreatedAt, DateTime? CompletedAt, string? ReviewedBy, string? ReviewNotes);
public record TestRunApplicationDetailDto(ApplicationDto Application, List<ScoringRunDto> ScoringRuns);
public record PromptTestRunDetailDto(PromptTestRunDto TestRun, List<TestRunApplicationDetailDto> Applications);
public record ReconcilePromptTestRunsResponseDto(int HealedCount, List<PromptTestRunDto> Runs);
public record CreatePromptRequest(string PromptText, string Source, string? GenerationMetadataJson);
public record GeneratePromptResult(string PromptText, string? GenerationMetadataJson);
public record RecruiterAnalyticsDto(string RecruiterId, string RecruiterName, string Department, int ApplicationsInQueue, int ManualReviewsPerformed, int ShortlistRecommendations, double? AverageProcessingTime, int ActiveJobs);
public record DepartmentAnalyticsDto(string Department, int TotalRecruiters, int ApplicationsInQueue, int ManualReviewsPerformed, int ShortlistRecommendations, int ActiveJobs, List<RecruiterAnalyticsDto> Recruiters);
