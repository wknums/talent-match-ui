using TalentMatch.Application.Authorization;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Web.Server.Endpoints;

public sealed class JobResourceAuthorizationFilter(
    IConfiguration configuration, ICurrentUserService currentUser,
    IJobRepository jobs, IOrganizationRepository organizations,
    IApplicationRepository applications, IScoringPromptRepository prompts,
    IPromptTestRunRepository testRuns) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!string.Equals(configuration["APP_AUTH_MODE"], "entra", StringComparison.OrdinalIgnoreCase))
            return await next(context);
        var jobId = http.GetRouteValue("jobId") as string;
        var applicationId = http.GetRouteValue("applicationId") as string;
        if (applicationId is not null)
        {
            var application = await applications.GetByIdAsync(applicationId, http.RequestAborted);
            if (application is null) return Results.NotFound();
            jobId = application.JobId;
        }
        if (jobId is null) return await next(context);
        var job = await jobs.GetByIdAsync(jobId, http.RequestAborted);
        if (job is null) return Results.NotFound();
        try
        {
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                await JobAuthorization.EnsureCanReadAsync(job, currentUser, organizations, http.RequestAborted);
            else
                await JobAuthorization.EnsureCanMutateAsync(job, currentUser, organizations, http.RequestAborted);
            if (http.GetRouteValue("promptId") is string promptId)
            {
                var prompt = await prompts.GetByIdAsync(promptId, http.RequestAborted);
                if (prompt?.JobId != jobId) return Results.NotFound();
                if (http.GetRouteValue("testRunId") is string runId)
                {
                    var run = await testRuns.GetByIdAsync(runId, http.RequestAborted);
                    if (run?.PromptId != promptId) return Results.NotFound();
                }
            }
            return await next(context);
        }
        catch (InvalidJobScopeException) { return AuthorizationErrorResults.Create(http, AuthorizationErrorCodes.InvalidJobScope); }
        catch (UnauthorizedAccessException) { return AuthorizationErrorResults.Create(http, AuthorizationErrorCodes.Forbidden); }
        catch (DirectoryAdministrationEvidenceException exception) { return AuthorizationErrorResults.Create(http, exception.Code); }
    }
}
