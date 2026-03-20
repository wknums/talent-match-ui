using MediatR;
using TalentMatch.Application.Prompts.Commands;
using TalentMatch.Application.Prompts.Queries;

namespace TalentMatch.Web.Server.Endpoints;

public static class PromptEndpoints
{
    public static void MapPromptEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs/{jobId}/prompts")
            .WithTags("Prompts")
            .RequireAuthorization();

        group.MapGet("/", async (string jobId, ISender mediator) =>
        {
            var prompts = await mediator.Send(new GetPromptsQuery(jobId));
            return Results.Ok(prompts);
        });

        group.MapGet("/{promptId}", async (string jobId, string promptId, ISender mediator) =>
        {
            var prompt = await mediator.Send(new GetPromptQuery(promptId));
            return prompt != null ? Results.Ok(prompt) : Results.NotFound();
        });

        group.MapPost("/", async (string jobId, CreatePromptRequest request, ISender mediator) =>
        {
            var prompt = await mediator.Send(new CreatePromptCommand(
                jobId, request.PromptText, request.Source, request.GenerationMetadataJson, "current-user"));
            return Results.Created($"/api/jobs/{jobId}/prompts/{prompt.Id}", prompt);
        });

        group.MapPost("/{promptId}/edit", async (string jobId, string promptId, EditPromptRequest request, ISender mediator) =>
        {
            var prompt = await mediator.Send(new EditPromptCommand(promptId, request.PromptText, "current-user"));
            return Results.Created($"/api/jobs/{jobId}/prompts/{prompt.Id}", prompt);
        });

        group.MapPost("/{promptId}/activate", async (string jobId, string promptId, ISender mediator) =>
        {
            var prompt = await mediator.Send(new ActivatePromptCommand(promptId, "current-user"));
            return Results.Ok(prompt);
        });

        group.MapPost("/{promptId}/rate", async (string jobId, string promptId, RatePromptRequest request, ISender mediator) =>
        {
            var prompt = await mediator.Send(new RatePromptCommand(promptId, request.Rating, request.Comments));
            return Results.Ok(prompt);
        });

        group.MapPost("/generate", async (string jobId, ISender mediator) =>
        {
            var prompt = await mediator.Send(new GeneratePromptCommand(jobId, "current-user"));
            return Results.Created($"/api/jobs/{jobId}/prompts/{prompt.Id}", prompt);
        });

        group.MapPost("/{promptId}/approve-production", async (string jobId, string promptId, ISender mediator) =>
        {
            var prompt = await mediator.Send(new ApprovePromptForProductionCommand(promptId, "current-user"));
            return Results.Ok(prompt);
        });

        // Test run endpoints
        var testRunGroup = app.MapGroup("/api/jobs/{jobId}/prompts/{promptId}/test-runs")
            .WithTags("PromptTestRuns")
            .RequireAuthorization();

        testRunGroup.MapGet("/", async (string jobId, string promptId, ISender mediator) =>
        {
            var runs = await mediator.Send(new GetPromptTestRunsQuery(promptId));
            return Results.Ok(runs);
        });

        testRunGroup.MapGet("/{testRunId}", async (string jobId, string promptId, string testRunId, ISender mediator) =>
        {
            var detail = await mediator.Send(new GetPromptTestRunQuery(testRunId));
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        });

        testRunGroup.MapPost("/", async (string jobId, string promptId, CreateTestRunRequest request, ISender mediator) =>
        {
            var files = request.Files.Select(f =>
            {
                var bytes = Convert.FromBase64String(f.Content);
                var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                return new TestRunFile(f.FileName, f.MimeType, f.SizeBytes, f.Content, fingerprint);
            }).ToList();
            var testRun = await mediator.Send(new CreatePromptTestRunCommand(jobId, promptId, files));
            return Results.Created($"/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRun.Id}", testRun);
        });

        testRunGroup.MapPost("/{testRunId}/approve", async (string jobId, string promptId, string testRunId,
            ApproveTestRunRequest? request, ISender mediator) =>
        {
            try
            {
                var testRun = await mediator.Send(new ApprovePromptTestRunCommand(testRunId, "current-user", request?.ReviewNotes));
                return Results.Ok(testRun);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public record CreatePromptRequest(string PromptText, string Source, string? GenerationMetadataJson);
public record EditPromptRequest(string PromptText);
public record RatePromptRequest(int Rating, string? Comments);
public record ApproveTestRunRequest(string? ReviewNotes);
public record CreateTestRunFileRequest(string FileName, string Content, string MimeType, long SizeBytes);
public record CreateTestRunRequest(List<CreateTestRunFileRequest> Files);
