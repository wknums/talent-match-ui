using System.Text.Json;
using FluentValidation;
using MediatR;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.Jobs;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Application.Jobs.Queries;
using TalentMatch.Application.JobExtraction.Commands;
using TalentMatch.Application.JobExtraction.Queries;
using TalentMatch.Application.Rubrics.Models;
using TalentMatch.Application.Rubrics.Services;

namespace TalentMatch.Web.Server.Endpoints;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs").WithTags("Jobs").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (InvalidJobScopeException)
            {
                return AuthorizationErrorResults.Create(
                    context.HttpContext,
                    AuthorizationErrorCodes.InvalidJobScope);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(
                    context.HttpContext,
                    AuthorizationErrorCodes.Forbidden);
            }
        });

        group.MapGet("/", async (ISender mediator) =>
        {
            var jobs = await mediator.Send(new GetJobSummariesQuery());
            return Results.Ok(jobs);
        });

        group.MapPost("/", async (
            CreateJobRequest request,
            ISender mediator,
            HttpContext httpContext) =>
        {
            try
            {
                var job = await mediator.Send(new CreateJobCommand(
                    request.Title, request.Department, request.Organisation, request.PostingDate,
                    request.RubricJson, request.MustHavesJson, request.DesiredCriteriaJson,
                    request.ScoringRunCount, request.AggregationStrategy, request.LonglistThreshold,
                    request.ShortlistThreshold, request.VarianceThreshold, request.JobDescription,
                    request.RubricSource ?? "manual", request.RawExtractionResponse,
                    request.ExtractionId, request.ExtractionInstructionVersionId,
                    request.OrganizationId, request.DepartmentId));
                return Results.Created($"/api/jobs/{job.Id}", new
                {
                    job.Id, job.JobCode, job.Title, job.Department,
                    job.Organisation, job.PostingDate, job.Status,
                    job.JobDescription, job.CreatedBy, job.CreatedAt, job.UpdatedAt
                });
            }
            catch (ValidationException exception)
            {
                var errorCode = IsOrganizationAliasValidation(exception, request)
                    ? AuthorizationErrorCodes.InvalidJobScope
                    : AuthorizationErrorCodes.InvalidScope;
                return AuthorizationErrorResults.Create(httpContext, errorCode);
            }
            catch (InvalidJobScopeException)
            {
                return AuthorizationErrorResults.Create(
                    httpContext,
                    AuthorizationErrorCodes.InvalidJobScope);
            }
            catch (InvalidOperationException)
            {
                return AuthorizationErrorResults.Create(
                    httpContext,
                    AuthorizationErrorCodes.InvalidScope);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(
                    httpContext,
                    AuthorizationErrorCodes.Forbidden);
            }
        });

        group.MapPost("/extract-spec", async (ExtractDocumentRequest request, ISender mediator, HttpContext httpContext) =>
        {
            try
            {
                var result = await mediator.Send(new ExtractJobSpecCommand(request.FileName, request.Content, request.MimeType));
                if (!result.IsValid)
                {
                    return Results.Json(new
                    {
                        error = "validation_failed",
                        message = "Extraction response failed contract validation.",
                        result.ExtractionId,
                        result.InstructionVersionId,
                        result.ProtectedContractVersion,
                        result.ValidationStatus,
                        result.ValidationFindings,
                        correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                    }, statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                return Results.Ok(new
                {
                    result.ExtractionId,
                    result.InstructionVersionId,
                    result.ProtectedContractVersion,
                    result.ValidationStatus,
                    result.ValidationFindings,
                    Title = result.Title,
                    JobDescription = result.JobDescription,
                    Department = result.Department,
                    Organization = result.Organization,
                    Rubric = result.Rubric,
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("AWR_SEQ_API_ENDPOINT", StringComparison.OrdinalIgnoreCase)
                                                      || ex.Message.Contains("No active extraction instruction", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("base64", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
            catch (Exception)
            {
                return Results.Problem("AWReason extraction request failed.", statusCode: 502);
            }
        });

        group.MapPost("/extract-rubric", async (ExtractDocumentRequest request, ISender mediator, HttpContext httpContext) =>
        {
            try
            {
                var result = await mediator.Send(new ExtractJobSpecCommand(request.FileName, request.Content, request.MimeType));
                return result.IsValid
                    ? Results.Ok(result.Rubric)
                    : Results.Json(new
                    {
                        error = "validation_failed",
                        message = "Extraction response failed contract validation.",
                        result.ExtractionId,
                        result.InstructionVersionId,
                        result.ProtectedContractVersion,
                        result.ValidationStatus,
                        result.ValidationFindings,
                        correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                    }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("AWR_SEQ_API_ENDPOINT", StringComparison.OrdinalIgnoreCase)
                                                      || ex.Message.Contains("No active extraction instruction", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception)
            {
                return Results.Problem("AWReason rubric extraction request failed.", statusCode: 502);
            }
        });

        group.MapGet("/{jobId}", async (string jobId, ISender mediator, HttpContext httpContext) =>
        {
            var job = await mediator.Send(new GetJobDetailQuery(jobId));
            if (job is null)
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            return Results.Ok(new
            {
                job.Id, job.JobCode, job.Title, job.Department,
                job.Organisation, job.PostingDate, job.Status,
                job.CurrentConfigVersionId, job.JobDescription, job.CreatedBy, job.CreatedAt
            });
        });

        group.MapGet("/{jobId}/config", async (string jobId, ISender mediator, HttpContext httpContext) =>
        {
            var config = await mediator.Send(new GetJobConfigQuery(jobId));
            if (config is null)
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            var extraction = await mediator.Send(new GetJobSpecExtractionQuery(jobId));
            return Results.Ok(new
            {
                config.RubricJson,
                config.MustHavesJson,
                config.DesiredCriteriaJson,
                config.ScoringRunCount,
                config.AggregationStrategy,
                config.LonglistThreshold,
                config.ShortlistThreshold,
                config.VarianceThreshold,
                config.RubricApprovalStatus,
                config.RubricSource,
                config.ExtractionId,
                config.ExtractionInstructionVersionId,
                config.Id,
                config.VersionNumber,
                Extraction = extraction is null ? null : new
                {
                    extraction.Id,
                    extraction.InstructionVersionId,
                    extraction.ProtectedContractVersion,
                    extraction.ValidationStatus,
                    extraction.ValidationFindings,
                    extraction.SourceFileName,
                    extraction.SourceMimeType,
                    extraction.CompletedAt,
                    extraction.CorrelationId
                }
            });
        });

        group.MapPut("/{jobId}/config", async (string jobId, UpdateJobConfigRequest request, ISender mediator, HttpContext httpContext) =>
        {
            try
            {
                var config = await mediator.Send(new UpdateJobConfigCommand(
                    jobId, request.RubricJson, request.MustHavesJson, request.DesiredCriteriaJson,
                    request.ScoringRunCount, request.AggregationStrategy, request.LonglistThreshold,
                    request.ShortlistThreshold, request.VarianceThreshold,
                    request.RubricSource ?? "manual", request.RawExtractionResponse,
                    request.ExtractionId, request.ExtractionInstructionVersionId, request.ExpectedConfigVersionId, request.RubricApprovalStatus));
                return Results.Ok(new
                {
                    config.Id,
                    config.JobId,
                    config.VersionNumber,
                    config.RubricJson,
                    config.MustHavesJson,
                    config.DesiredCriteriaJson,
                    config.ScoringRunCount,
                    config.AggregationStrategy,
                    config.LonglistThreshold,
                    config.ShortlistThreshold,
                    config.VarianceThreshold,
                    config.RubricApprovalStatus,
                    config.RubricSource,
                    config.ExtractionId,
                    config.ExtractionInstructionVersionId,
                    config.CreatedAt
                });
            }
            catch (JobConfigVersionConflictException ex)
            {
                return Results.Json(new
                {
                    error = "stale_version",
                    message = ex.Message,
                    correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (InvalidJobConfigException ex)
            {
                return Results.Json(new
                {
                    error = "invalid_job_config",
                    message = ex.Message,
                    correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/{jobId}/rubric/convert", async (
            string jobId,
            LegacyRubricConversionRequest request,
            ISender mediator,
            LegacyRubricAdapter adapter,
            HttpContext httpContext) =>
        {
            var job = await mediator.Send(new GetJobDetailQuery(jobId));
            if (job is null)
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);

            var current = job.ConfigVersions
                .FirstOrDefault(version => version.Id == job.CurrentConfigVersionId)
                ?? job.ConfigVersions.OrderByDescending(version => version.VersionNumber).FirstOrDefault();
            if (current is null)
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);

            if (!string.Equals(current.Id, request.ExpectedConfigVersionId, StringComparison.Ordinal))
                return Results.Json(new
                {
                    error = "stale_version",
                    message = "stale_version: the current configuration has changed and must be reloaded before conversion.",
                    correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                }, statusCode: StatusCodes.Status409Conflict);

            if (adapter.IsRubricV2Json(current.RubricJson))
                return Results.Json(new
                {
                    error = "already_converted",
                    message = "The current rubric already uses rubric-v2.",
                    correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                }, statusCode: StatusCodes.Status400BadRequest);

            var preview = adapter.CreateLegacyConversionProposal(
                current.RubricJson,
                current.MustHavesJson,
                current.DesiredCriteriaJson,
                current.Id);

            if (string.Equals(request.Mode, "preview", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(preview);

            var reviewed = request.ReviewedRubric.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? preview
                : JsonSerializer.Deserialize<RubricEnvelopeModel>(
                    request.ReviewedRubric.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? preview;

            try
            {
                var saved = await mediator.Send(new UpdateJobConfigCommand(
                    jobId,
                    adapter.SerializeEnvelope(reviewed),
                    adapter.ProjectMustHavesJson(reviewed),
                    adapter.ProjectDesiredCriteriaJson(reviewed),
                    current.ScoringRunCount,
                    current.AggregationStrategy,
                    current.LonglistThreshold,
                    current.ShortlistThreshold,
                    current.VarianceThreshold,
                    "manual",
                    current.RawExtractionResponse,
                    current.ExtractionId,
                    current.ExtractionInstructionVersionId,
                    current.Id,
                    current.RubricApprovalStatus));

                return Results.Ok(new
                {
                    saved.Id,
                    saved.VersionNumber,
                    saved.RubricJson,
                    saved.MustHavesJson,
                    saved.DesiredCriteriaJson,
                    saved.ExtractionId,
                    saved.ExtractionInstructionVersionId,
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("stale_version", StringComparison.Ordinal))
            {
                return Results.Json(new
                {
                    error = "stale_version",
                    message = ex.Message,
                    correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        group.MapPost("/{jobId}/process", async (string jobId, ISender mediator, HttpContext httpContext, CancellationToken ct) =>
        {
            // Check for production-approved prompt before allowing scoring pipeline trigger
            var prompts = await mediator.Send(new TalentMatch.Application.Prompts.Queries.GetPromptsQuery(jobId), ct);
            var productionPrompt = prompts.FirstOrDefault(p => p.Status == "production-approved");
            if (productionPrompt == null)
                return Results.Problem("No production-approved prompt exists for this job. Approve a prompt before processing.", statusCode: 400);

            // Load job to get run count from config
            var job = await mediator.Send(new GetJobDetailQuery(jobId), ct);
            if (job == null)
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);

            var config = job.ConfigVersions
                .FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
                ?? job.ConfigVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            var runCount = config?.ScoringRunCount ?? 3;
            if (TalentMatch.Application.Jobs.Commands.ProcessJobCommandHandler.ResolveScoringMode() == "sequential"
                && !string.Equals(config?.RubricApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("Approve the job rubric before processing applications.", statusCode: 400);

            // Sequential processing wakes the in-process pool; the request does not wait for scoring.
            var result = await mediator.Send(new TalentMatch.Application.Jobs.Commands.ProcessJobCommand(
                jobId, productionPrompt.Id, runCount), ct);

            return Results.Accepted($"/api/jobs/{jobId}/applications",
                new { processed = result.Processed, total = result.Total, errors = result.Errors, queued = result.Queued });
        })
        .WithSummary("Request processing of queued job applications")
        .WithDescription("Returns immediately after notifying the sequential scoring pool or enqueueing platform batches. Poll the applications resource for results.")
        .Produces(StatusCodes.Status202Accepted);

        group.MapPost("/{jobId}/reaggregate", async (string jobId, ISender mediator) =>
        {
            var result = await mediator.Send(new TalentMatch.Application.Jobs.Commands.ReAggregateJobCommand(jobId));
            return Results.Ok(new { updated = result.Updated, total = result.Total });
        });

        // Platform-mode cancellation: best-effort flips CancelRequested on all
        // pending/submitted ScoringBatches for the job; the reconciler picks it
        // up on its next tick. Sequential mode currently has no cancel — this
        // endpoint still returns 200 with affectedBatches=0 in that case.
        group.MapPost("/{jobId}/scoring/cancel", async (string jobId, ISender mediator) =>
        {
            var result = await mediator.Send(new TalentMatch.Application.Jobs.Commands.CancelJobScoringCommand(jobId));
            return Results.Ok(new { affectedBatches = result.AffectedBatches });
        });

        // Platform-mode progress rollup. Returns null progress if no platform run
        // is in flight for this job (caller treats that as sequential / idle).
        group.MapGet("/{jobId}/scoring/progress", async (string jobId,
            ISender mediator,
            TalentMatch.Domain.Interfaces.IScoringBatchRepository batchRepo,
            HttpContext httpContext) =>
        {
            var job = await mediator.Send(new GetJobDetailQuery(jobId));
            if (job is null)
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);

            var progress = await batchRepo.GetProgressAsync(jobId);
            return Results.Ok(new { progress });
        });

        group.MapPut("/{jobId}/rubric-approval", async (string jobId, UpdateRubricApprovalRequest request, ISender mediator) =>
        {
            var result = await mediator.Send(new UpdateRubricApprovalCommand(jobId, request.Status));
            return Results.Ok(result);
        });

        group.MapDelete("/{jobId}", async (string jobId, ISender mediator, HttpContext httpContext) =>
        {
            try
            {
                var result = await mediator.Send(new DeleteJobCommand(jobId));
                return result
                    ? Results.NoContent()
                    : AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            }
            catch (InvalidOperationException)
            {
                return AuthorizationErrorResults.Create(
                    httpContext,
                    AuthorizationErrorCodes.InvalidScope);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(
                    httpContext,
                    AuthorizationErrorCodes.Forbidden);
            }
        });
    }

    private static bool IsOrganizationAliasValidation(
        ValidationException exception,
        CreateJobRequest request)
        => !string.IsNullOrWhiteSpace(request.OrganizationId)
            && !string.IsNullOrWhiteSpace(request.DepartmentId)
            && exception.Errors.All(error =>
                error.PropertyName == nameof(CreateJobCommand.Organisation));
}

public record CreateJobRequest(
    string Title, string Department, string Organisation, DateTime PostingDate,
    string? RubricJson, string? MustHavesJson, string? DesiredCriteriaJson,
    int ScoringRunCount, string AggregationStrategy, double LonglistThreshold,
    double ShortlistThreshold, double VarianceThreshold, string? JobDescription,
    string? RubricSource = "manual", string? RawExtractionResponse = null,
    string? ExtractionId = null, string? ExtractionInstructionVersionId = null,
    string? OrganizationId = null, string? DepartmentId = null);

public record UpdateJobConfigRequest(
    string? RubricJson, string? MustHavesJson, string? DesiredCriteriaJson,
    int ScoringRunCount, string AggregationStrategy, double LonglistThreshold,
    double ShortlistThreshold, double VarianceThreshold,
    string? RubricSource = "manual", string? RawExtractionResponse = null,
    string? ExtractionId = null, string? ExtractionInstructionVersionId = null,
    string? ExpectedConfigVersionId = null, string? RubricApprovalStatus = null);

public record UpdateRubricApprovalRequest(string Status);

public record LegacyRubricConversionRequest(
    string Mode,
    string ExpectedConfigVersionId,
    JsonElement ReviewedRubric);

public record ExtractDocumentRequest(
    string FileName, string Content, string MimeType);

internal static class ExtractionPrompts
{
    internal const string ExtractSpec = """
You are an expert information extraction and evaluation assistant. Read a job specification and produce a single JSON object that captures:

Job Title, Job Description, Department (if present), Organization (if present).
All must-have requirements.
All recommended/desired qualifications.
Experience requirements (years, domains, tools).
A rubric with categories and weights that sum to 1.0:

If a rubric is present in the document, extract its categories and weights (convert to normalized weights summing to 1.0).
If no rubric is present, generate a thoughtful draft rubric and assign 60% of the total weight to the "Must-Have Requirements" (distribute the remaining 40% carefully across other relevant categories).
For generated rubrics, include criteria mappings so each category clearly reflects items drawn from the spec.


Important

Think step-by-step privately. Do not reveal chain-of-thought.
Output only the final JSON object—no additional text.
The JSON must be valid, with proper escaping, no trailing commas, and weights that sum to exactly 1.0 (use rounding and final normalization as needed)
Output JSON Schema (contract)

You must adhere to this structure and field naming. If a field is not present in the document, use null or [] as appropriate.

{
  "job_title": "string | null",
  "job_description": "string | null",
  "department": "string | null",
  "organization": "string | null",
  "must_have_requirements": ["string", "..."],
  "recommended_or_desired": ["string", "..."],
  "experience_requirements": ["string", "..."],
  "rubric": {
    "has_rubric_in_doc": "boolean",
    "categories": [
      {
        "name": "string",
        "weight": 0.0,
        "criteria": ["string", "..."],
        "source": "doc|generated"
      }
    ],
    "weights_sum_to_1_0": "boolean"
  }
}

Extraction Rules & Heuristics


Job Title

Prefer explicit title lines/headings; otherwise infer from earliest explicit role labels.
Normalize casing (Title Case) and trim department/org suffixes unless integral to the title.


Job Description

Use the main narrative of responsibilities/role purpose.
Exclude company boilerplate unless tightly coupled to the role.


Department / Organization

Extract when explicitly present (e.g., "Department: Finance", "Reports to: Head of …" is not department unless clearly labeled).
Organization is the hiring entity or brand named as the employer.



Must-Have vs Recommended/Desired

Must-Have indicators: "must", "required", "minimum", "compulsory", "essential", "non-negotiable", "shall", "strictly required".
Recommended/Desired indicators: "nice to have", "preferred", "advantageous", "beneficial", "plus", "bonus", "good to have".
If ambiguous, default to recommended_or_desired unless the doc uses strong mandatory language.



Experience Requirements

Capture explicit experience statements: years, domains, tools, certifications with "required/mandatory/minimum" → also include the phrases in must-have if marked mandatory.
If experience is optional, keep under recommended_or_desired and still mirror relevant items in experience_requirements to preserve visibility.
De-duplicate across arrays; keep one canonical phrasing.



Deduplication & Normalization

Trim whitespace; singularize plurals if natural; remove trailing punctuation; unify acronyms (first use can include long form).

Rubric Logic
If the document contains a rubric

Extract all categories, their weights (percentages/points to be normalized to weights summing to 1.0), and any explicit criteria mapping.
Preserve original category names (normalize casing).
Set source: "doc".
After conversion and rounding to two decimals, ensure final sum equals 1.00 by adjusting the largest category by the minimal residual (±0.01 as needed).

If the document does NOT contain a rubric

Create a draft rubric with thoughtful categories and weights that sum to 1.00, assigning 0.60 (60%) to "Must-Have Requirements".
Distribute the remaining 0.40 (40%) across categories that make sense for this spec. Use these defaults unless the document strongly suggests alternatives:

Must-Have Requirements: 0.60
Recommended/Desired Qualifications: 0.20
Experience Depth & Relevance: 0.15
Role/Context Fit (Responsibilities, Domain, Soft Skills): 0.05


Tailor the category names to match the document's language (e.g., "Core Competencies", "Technical Proficiency", "Domain Knowledge"), but keep Must-Have at 0.60.
For each category, populate criteria with succinct bullet points derived from the extracted items.
Mark source: "generated" for all categories.
Round weights to two decimals and normalize to ensure the final sum equals 1.00 (adjust the "Must-Have Requirements" category by the minimal residual if needed, while staying as close as possible to 0.60).


Before emitting, silently verify:

All required top-level fields exist; use null or [] if not present.
Arrays contain strings only (no nested objects except rubric.categories).
No duplicate items across arrays; if overlaps are inherent, keep the most appropriate placement and remove duplicates.
rubric.categories non-empty; each has name, weight (number), criteria (array), and source ("doc" or "generated").
Sum of weight values equals 1.00 exactly after rounding and final normalization; set weights_sum_to_1_0: true.
Output is valid JSON with no extra commentary.
""";

    internal const string ExtractRubric = """
You are an expert rubric extraction assistant. Read a rubric or scoring criteria document and produce a single JSON object.

Extract the job title (if present) and all rubric categories with their weights.
Weights must be normalized to sum to exactly 1.0.

Output only valid JSON with no additional text:
{
  "title": "string | null",
  "categories": [
    {
      "name": "string",
      "weight": 0.0,
      "description": "string"
    }
  ]
}
""";
}
