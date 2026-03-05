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
                request.RubricJson, request.MustHaveCriteriaJson, request.ScoringRunCount,
                request.AggregationStrategy, request.LonglistThreshold, request.ShortlistThreshold,
                request.VarianceThreshold));
            return Results.Created($"/api/jobs/{job.Id}", job);
        });

        group.MapGet("/{jobId}", async (string jobId, ISender mediator) =>
        {
            var job = await mediator.Send(new GetJobDetailQuery(jobId));
            return job != null ? Results.Ok(job) : Results.NotFound();
        });

        group.MapPut("/{jobId}/config", async (string jobId, UpdateJobConfigRequest request, ISender mediator) =>
        {
            var config = await mediator.Send(new UpdateJobConfigCommand(
                jobId, request.RubricJson, request.MustHaveCriteriaJson, request.ScoringRunCount,
                request.AggregationStrategy, request.LonglistThreshold, request.ShortlistThreshold,
                request.VarianceThreshold));
            return Results.Ok(config);
        });

        group.MapPost("/{jobId}/process", async (string jobId) =>
        {
            // Pipeline trigger - placeholder for orchestrator integration
            return Results.Accepted();
        });
    }
}

public record CreateJobRequest(
    string Title, string Department, string Organisation, DateTime PostingDate,
    string? RubricJson, string? MustHaveCriteriaJson, int ScoringRunCount,
    string AggregationStrategy, double LonglistThreshold, double ShortlistThreshold,
    double VarianceThreshold);

public record UpdateJobConfigRequest(
    string? RubricJson, string? MustHaveCriteriaJson, int ScoringRunCount,
    string AggregationStrategy, double LonglistThreshold, double ShortlistThreshold,
    double VarianceThreshold);
