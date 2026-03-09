using MediatR;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Application.Jobs.Queries;

namespace TalentMatch.Web.Server.Endpoints;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs").WithTags("Jobs").RequireAuthorization();

        group.MapGet("/", async (ISender mediator) =>
        {
            var jobs = await mediator.Send(new GetJobsQuery());
            return Results.Ok(jobs);
        });

        group.MapPost("/", async (CreateJobRequest request, ISender mediator) =>
        {
            var job = await mediator.Send(new CreateJobCommand(
                request.Title, request.Department, request.Organisation, request.PostingDate,
                request.RubricJson, request.MustHaveCriteriaJson, request.DesiredCriteriaJson,
                request.ScoringRunCount, request.AggregationStrategy, request.LonglistThreshold,
                request.ShortlistThreshold, request.VarianceThreshold, request.JobDescription));
            return Results.Created($"/api/jobs/{job.Id}", job);
        });

        group.MapPost("/extract-spec", async (ExtractDocumentRequest request, IHttpClientFactory httpClientFactory) =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
            if (string.IsNullOrEmpty(endpoint))
                return Results.Problem("AWR_SEQ_API_ENDPOINT is not configured", statusCode: 503);

            using var client = httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync($"{endpoint}/extract-spec", new
            {
                request.FileName,
                request.Content,
                request.MimeType
            });
            var result = await response.Content.ReadAsStringAsync();
            return Results.Content(result, "application/json", statusCode: (int)response.StatusCode);
        });

        group.MapPost("/extract-rubric", async (ExtractDocumentRequest request, IHttpClientFactory httpClientFactory) =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
            if (string.IsNullOrEmpty(endpoint))
                return Results.Problem("AWR_SEQ_API_ENDPOINT is not configured", statusCode: 503);

            using var client = httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync($"{endpoint}/extract-rubric", new
            {
                request.FileName,
                request.Content,
                request.MimeType
            });
            var result = await response.Content.ReadAsStringAsync();
            return Results.Content(result, "application/json", statusCode: (int)response.StatusCode);
        });

        group.MapGet("/{jobId}", async (string jobId, ISender mediator) =>
        {
            var job = await mediator.Send(new GetJobDetailQuery(jobId));
            return job != null ? Results.Ok(job) : Results.NotFound();
        });

        group.MapPut("/{jobId}/config", async (string jobId, UpdateJobConfigRequest request, ISender mediator) =>
        {
            var config = await mediator.Send(new UpdateJobConfigCommand(
                jobId, request.RubricJson, request.MustHaveCriteriaJson, request.DesiredCriteriaJson,
                request.ScoringRunCount, request.AggregationStrategy, request.LonglistThreshold,
                request.ShortlistThreshold, request.VarianceThreshold));
            return Results.Ok(config);
        });

        group.MapPost("/{jobId}/process", async (string jobId, ISender mediator) =>
        {
            // Check for production-approved prompt before allowing scoring pipeline trigger
            var prompts = await mediator.Send(new TalentMatch.Application.Prompts.Queries.GetPromptsQuery(jobId));
            var hasProductionPrompt = prompts.Any(p => p.Status == "production-approved");
            if (!hasProductionPrompt)
                return Results.Problem("No production-approved prompt exists for this job. Approve a prompt before processing.", statusCode: 400);

            // Pipeline trigger - placeholder for orchestrator integration
            return Results.Accepted();
        });
    }
}

public record CreateJobRequest(
    string Title, string Department, string Organisation, DateTime PostingDate,
    string? RubricJson, string? MustHaveCriteriaJson, string? DesiredCriteriaJson,
    int ScoringRunCount, string AggregationStrategy, double LonglistThreshold,
    double ShortlistThreshold, double VarianceThreshold, string? JobDescription);

public record UpdateJobConfigRequest(
    string? RubricJson, string? MustHaveCriteriaJson, string? DesiredCriteriaJson,
    int ScoringRunCount, string AggregationStrategy, double LonglistThreshold,
    double ShortlistThreshold, double VarianceThreshold);

public record ExtractDocumentRequest(
    string FileName, string Content, string MimeType);
