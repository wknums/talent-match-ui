using System.Net.Http.Json;

namespace TalentMatch.Web.Client.Services;

public class ApiException : Exception
{
    public int StatusCode { get; }

    public ApiException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}

public class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(HttpClient http)
    {
        _http = http;
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, string fallbackMessage)
    {
        if (response.IsSuccessStatusCode) return;

        string errorMessage = fallbackMessage;
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
                errorMessage = body;
        }
        catch { /* use fallback */ }

        throw new ApiException(errorMessage, (int)response.StatusCode);
    }

    // Auth
    public async Task<UserInfo?> LoginAsync(string username, string password)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<UserInfo>();
    }

    public async Task LogoutAsync()
        => await _http.PostAsync("/api/auth/logout", null);

    public async Task<UserInfo?> GetCurrentUserAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<UserInfo>("/api/auth/me");
        }
        catch { return null; }
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/change-password", new { CurrentPassword = currentPassword, NewPassword = newPassword });
        return response.IsSuccessStatusCode;
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
    public async Task<List<JobDto>> GetJobsAsync()
        => await _http.GetFromJsonAsync<List<JobDto>>("/api/jobs") ?? new();

    public async Task<JobDto?> GetJobAsync(string jobId)
        => await _http.GetFromJsonAsync<JobDto>($"/api/jobs/{jobId}");

    public async Task<bool> CreateJobAsync(CreateJobDto job)
    {
        var response = await _http.PostAsJsonAsync("/api/jobs", job);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateJobConfigAsync(string jobId, UpdateConfigDto config)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{jobId}/config", config);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ProcessJobAsync(string jobId)
    {
        var response = await _http.PostAsync($"/api/jobs/{jobId}/process", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<JobConfigDto?> GetJobConfigAsync(string jobId)
    {
        try { return await _http.GetFromJsonAsync<JobConfigDto>($"/api/jobs/{jobId}/config"); }
        catch { return null; }
    }

    // Applications
    public async Task<List<ApplicationDto>> GetApplicationsAsync(string jobId, string? list = null)
        => await _http.GetFromJsonAsync<List<ApplicationDto>>($"/api/jobs/{jobId}/applications?list={list}") ?? new();

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

    public async Task<AggregatedResultDto?> GetAggregatedResultAsync(string applicationId)
        => await _http.GetFromJsonAsync<AggregatedResultDto>($"/api/applications/{applicationId}/result");

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

    public async Task<List<DlqItemDto>> GetDlqItemsAsync()
        => await _http.GetFromJsonAsync<List<DlqItemDto>>("/api/dlq") ?? new();

    public async Task<bool> RetryDlqItemAsync(string itemId)
    {
        var response = await _http.PostAsync($"/api/dlq/{itemId}/retry", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<List<AuditEventDto>> GetAuditEventsAsync(string? entityType = null, string? eventType = null)
        => await _http.GetFromJsonAsync<List<AuditEventDto>>($"/api/audit?entityType={entityType}&eventType={eventType}") ?? new();
}

// DTOs
public record UserInfo(string Id, string Username, string Role, string Department, string FullName, string Email, DateTime? LastLogin = null);
public record JobDto(string Id, string JobCode, string Title, string Department, string Organisation, DateTime PostingDate, string Status, string? CurrentConfigVersionId, DateTime CreatedAt);
public record CreateJobDto(string Title, string Department, string Organisation, DateTime PostingDate, string? RubricJson, string? MustHaveCriteriaJson, int ScoringRunCount, string AggregationStrategy, double LonglistThreshold, double ShortlistThreshold, double VarianceThreshold);
public record UpdateConfigDto(string? RubricJson, string? MustHaveCriteriaJson, int ScoringRunCount, string AggregationStrategy, double LonglistThreshold, double ShortlistThreshold, double VarianceThreshold);
public record ApplicationDto(string Id, string JobId, string Status, double? FinalScore, string? FinalDecision, double? Variance, DateTime CreatedAt);
public record ScoringRunDto(string Id, int RunIndex, double TotalScore, string CategoryScoresJson, string MustHaveEvaluationJson, string EvidenceCitationsJson, string ImprovementTipsJson, string AiModelId, string PromptVersion, int InputTokens, int OutputTokens);
public record AggregatedResultDto(string Id, double FinalScore, string Decision, double Variance, double Confidence, string ConsolidatedRationale, string MergedImprovementTipsJson);
public record ExtractionDto(string Id, string NormalisedText, double ConfidenceScore, string Status);
public record ManualReviewDto(string RubricScoresJson, string OverallComment, double? AdjustedFinalScore, string AuditTrailJson);
public record SystemStatsDto(int Queued, int Extracting, int Scoring, int Aggregating, int Completed, int NeedsManualReview, int Failed, int TotalJobs, int TotalApplications);
public record DlqItemDto(string Id, string EntityType, string EntityId, string FailureReason, int RetryCount, DateTime CreatedAt);
public record AuditEventDto(string Id, string Actor, string EventType, string EntityType, string EntityId, DateTime Timestamp, string CorrelationId);
public record ResetRequestDto(string Id, string UserId, string Username, string Reason, string Status, DateTime CreatedAt);
public record JobConfigDto(string? RubricJson, string? MustHaveCriteriaJson, int ScoringRunCount, string AggregationStrategy, double LonglistThreshold, double ShortlistThreshold, double VarianceThreshold);
