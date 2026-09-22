using System.Security.Claims;
using MediatR;
using TalentMatch.Application.Prompts.Commands;
using TalentMatch.Application.Prompts.Queries;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Prompts.Services;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Web.Server.Endpoints;

public static class PromptEndpoints
{
    public static void MapPromptEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scoring-profile", (IScoringProfileProvider profiles)
                => Results.Ok(profiles.Current))
            .WithTags("Prompts")
            .RequireAuthorization();

        app.MapGet("/api/reasoning-models", async (
            IReasoningModelCatalog reasoningModels,
            CancellationToken cancellationToken) =>
                Results.Ok(await reasoningModels.GetAsync(cancellationToken)))
            .WithTags("Prompts")
            .RequireAuthorization();

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

        group.MapPost("/", async (string jobId, CreatePromptRequest request, HttpContext httpContext, ISender mediator) =>
        {
            var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var prompt = await mediator.Send(new CreatePromptCommand(
                jobId,
                request.PromptText,
                request.Source,
                request.GenerationMetadataJson,
                actor,
                request.ModelId,
                request.ReasoningLevel));
            return Results.Created($"/api/jobs/{jobId}/prompts/{prompt.Id}", prompt);
        });

        group.MapPost("/{promptId}/edit", async (string jobId, string promptId, EditPromptRequest request, HttpContext httpContext, ISender mediator) =>
        {
            var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var prompt = await mediator.Send(new EditPromptCommand(
                promptId,
                request.PromptText,
                actor,
                request.ModelId,
                request.ReasoningLevel));
            return Results.Ok(prompt);
        });

        group.MapPost("/{promptId}/activate", async (string jobId, string promptId, HttpContext httpContext, ISender mediator) =>
        {
            var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var prompt = await mediator.Send(new ActivatePromptCommand(promptId, actor));
            return Results.Ok(prompt);
        });

        group.MapPost("/{promptId}/rate", async (string jobId, string promptId, RatePromptRequest request, ISender mediator) =>
        {
            var prompt = await mediator.Send(new RatePromptCommand(promptId, request.Rating, request.Comments));
            return Results.Ok(prompt);
        });

        group.MapPost("/generate", async (
            string jobId,
            GeneratePromptRequest request,
            HttpContext httpContext,
            ISender mediator) =>
        {
            var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var prompt = await mediator.Send(new GeneratePromptCommand(
                jobId,
                actor,
                request.ModelId,
                request.ReasoningLevel));
            return Results.Created($"/api/jobs/{jobId}/prompts/{prompt.Id}", prompt);
        });

        group.MapPost("/{promptId}/approve-production", async (string jobId, string promptId, HttpContext httpContext, ISender mediator) =>
        {
            try
            {
                var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
                var prompt = await mediator.Send(new ApprovePromptForProductionCommand(promptId, actor));
                return Results.Ok(prompt);
            }
            catch (ScoringProfileMismatchException ex)
            {
                return Results.Conflict(new { error = "scoring_profile_mismatch", message = ex.Message });
            }
        });

        group.MapPost("/{promptId}/set-production", async (string jobId, string promptId, HttpContext httpContext, ISender mediator) =>
        {
            try
            {
                var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
                var prompt = await mediator.Send(new SetProductionPromptCommand(promptId, actor));
                return Results.Ok(prompt);
            }
            catch (ScoringProfileMismatchException ex)
            {
                return Results.Conflict(new { error = "scoring_profile_mismatch", message = ex.Message });
            }
        });

        group.MapGet("/{promptId}/profile", async (
            string jobId,
            string promptId,
            ISender mediator,
            IPromptProfileGuard profileGuard,
            CancellationToken cancellationToken) =>
        {
            var prompt = await mediator.Send(new GetPromptQuery(promptId), cancellationToken);
            if (prompt is null || prompt.JobId != jobId)
                return Results.NotFound();
            return Results.Ok(await profileGuard.GetStatusAsync(prompt, cancellationToken));
        });

        var jobInstructionGroup = app.MapGroup("/api/jobs/{jobId}/prompt-generation-instructions")
            .WithTags("PromptGenerationInstructions")
            .RequireAuthorization();

        jobInstructionGroup.MapGet("/", async (
            string jobId, ISender mediator, CancellationToken cancellationToken) =>
            Results.Ok(await mediator.Send(
                new GetPromptGenerationInstructionsQuery(jobId), cancellationToken)));

        jobInstructionGroup.MapPost("/", async (
            string jobId,
            CreatePromptGenerationInstructionRequest request,
            HttpContext httpContext,
            ISender mediator,
            CancellationToken cancellationToken) =>
        {
            var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var created = await mediator.Send(
                new CreatePromptGenerationInstructionCommand(
                    jobId,
                    request.InstructionText,
                    request.ChangeNote,
                    actor,
                    request.ModelId,
                    request.ReasoningLevel),
                cancellationToken);
            return Results.Created(
                $"/api/jobs/{jobId}/prompt-generation-instructions/{created.Id}",
                created);
        });

        jobInstructionGroup.MapPost("/{instructionId}/activate", async (
            string jobId,
            string instructionId,
            HttpContext httpContext,
            ISender mediator,
            CancellationToken cancellationToken) =>
        {
            var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            return Results.Ok(await mediator.Send(
                new ActivatePromptGenerationInstructionCommand(
                    instructionId, jobId, actor),
                cancellationToken));
        });

        var globalInstructionGroup = app.MapGroup("/api/admin/prompt-generation-instructions")
            .WithTags("PromptGenerationInstructions")
            .RequireAuthorization("AdminOnly");

        globalInstructionGroup.MapGet("/", async (
            ISender mediator, CancellationToken cancellationToken) =>
            Results.Ok(await mediator.Send(
                new GetPromptGenerationInstructionsQuery(null), cancellationToken)));

        globalInstructionGroup.MapPost("/", async (
            CreatePromptGenerationInstructionRequest request,
            HttpContext httpContext,
            ISender mediator,
            CancellationToken cancellationToken) =>
        {
            var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var created = await mediator.Send(
                new CreatePromptGenerationInstructionCommand(
                    null,
                    request.InstructionText,
                    request.ChangeNote,
                    actor,
                    request.ModelId,
                    request.ReasoningLevel),
                cancellationToken);
            return Results.Created(
                $"/api/admin/prompt-generation-instructions/{created.Id}",
                created);
        });

        globalInstructionGroup.MapPost("/{instructionId}/activate", async (
            string instructionId,
            HttpContext httpContext,
            ISender mediator,
            CancellationToken cancellationToken) =>
        {
            var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            return Results.Ok(await mediator.Send(
                new ActivatePromptGenerationInstructionCommand(
                    instructionId, null, actor),
                cancellationToken));
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

        testRunGroup.MapPost("/reconcile", async (string jobId, string promptId, ISender mediator, IPromptTestRunRepository testRunRepo) =>
        {
            var before = (await testRunRepo.GetByPromptIdAsync(promptId))
                .ToDictionary(r => r.Id, r => r.Status, StringComparer.OrdinalIgnoreCase);

            var runs = (await mediator.Send(new GetPromptTestRunsQuery(promptId))).ToList();

            var healedCount = runs.Count(r =>
                before.TryGetValue(r.Id, out var oldStatus)
                && !string.Equals(oldStatus, r.Status, StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new ReconcilePromptTestRunsResponse(healedCount, runs));
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
            ApproveTestRunRequest? request, HttpContext httpContext, ISender mediator) =>
        {
            try
            {
                var actor = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
                var testRun = await mediator.Send(new ApprovePromptTestRunCommand(testRunId, actor, request?.ReviewNotes));
                return Results.Ok(testRun);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        testRunGroup.MapPost("/{testRunId}/retry", async (string jobId, string promptId, string testRunId, ISender mediator) =>
        {
            try
            {
                var testRun = await mediator.Send(new RetryTestRunCommand(testRunId));
                return Results.Ok(testRun);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        testRunGroup.MapPost("/{testRunId}/retest", async (
            string jobId, string promptId, string testRunId, ISender mediator) =>
        {
            try
            {
                return Results.Ok(await mediator.Send(new RetryTestRunCommand(testRunId)));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public record CreatePromptRequest(
    string PromptText,
    string Source,
    string? GenerationMetadataJson,
    string ModelId = "o3",
    string ReasoningLevel = "high");
public record EditPromptRequest(
    string PromptText,
    string ModelId = "o3",
    string ReasoningLevel = "high");
public sealed record GeneratePromptRequest(
    string ModelId = "o3",
    string ReasoningLevel = "high");
public record RatePromptRequest(int Rating, string? Comments);
public record ApproveTestRunRequest(string? ReviewNotes);
public record CreateTestRunFileRequest(string FileName, string Content, string MimeType, long SizeBytes);
public record CreateTestRunRequest(List<CreateTestRunFileRequest> Files);
public record ReconcilePromptTestRunsResponse(int HealedCount, IReadOnlyList<Domain.Entities.PromptTestRun> Runs);
public sealed record CreatePromptGenerationInstructionRequest(
    string InstructionText,
    string? ChangeNote,
    string ModelId = "o3",
    string ReasoningLevel = "high");
