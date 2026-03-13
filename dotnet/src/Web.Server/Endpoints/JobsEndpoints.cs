using System.Text.Json;
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
            var jobs = await mediator.Send(new GetJobSummariesQuery());
            return Results.Ok(jobs);
        });

        group.MapPost("/", async (CreateJobRequest request, ISender mediator) =>
        {
            var job = await mediator.Send(new CreateJobCommand(
                request.Title, request.Department, request.Organisation, request.PostingDate,
                request.RubricJson, request.MustHaveCriteriaJson, request.DesiredCriteriaJson,
                request.ScoringRunCount, request.AggregationStrategy, request.LonglistThreshold,
                request.ShortlistThreshold, request.VarianceThreshold, request.JobDescription,
                request.RubricSource ?? "manual", request.RawExtractionResponse));
            return Results.Created($"/api/jobs/{job.Id}", new
            {
                job.Id, job.JobCode, job.Title, job.Department,
                job.Organisation, job.PostingDate, job.Status,
                job.JobDescription, job.CreatedBy, job.CreatedAt, job.UpdatedAt
            });
        });

        group.MapPost("/extract-spec", async (ExtractDocumentRequest request, IHttpClientFactory httpClientFactory) =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
            if (string.IsNullOrEmpty(endpoint))
                return Results.Problem("AWR_SEQ_API_ENDPOINT is not configured", statusCode: 503);

            using var client = httpClientFactory.CreateClient();
            using var formData = new MultipartFormDataContent();

            // Add the extraction prompt as promptFile (required by /assess/passthrough)
            var promptContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(ExtractionPrompts.ExtractSpec));
            promptContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            formData.Add(promptContent, "promptFile", "extract-spec-prompt.md");

            // Add the uploaded document as specFile (decoded from base64)
            var docBytes = Convert.FromBase64String(request.Content);
            var docContent = new ByteArrayContent(docBytes);
            docContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.MimeType ?? "application/octet-stream");
            formData.Add(docContent, "specFile", request.FileName);

            var response = await client.PostAsync($"{endpoint}/assess/passthrough", formData);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                return Results.Problem(errorText, statusCode: (int)response.StatusCode);
            }

            // Passthrough returns the raw output file directly (not wrapped in a response object)
            var responseText = await response.Content.ReadAsStringAsync();

            JsonElement extracted;
            try
            {
                extracted = JsonSerializer.Deserialize<JsonElement>(responseText ?? "{}");
            }
            catch
            {
                return Results.Problem("Failed to parse extraction response as JSON", statusCode: 502);
            }

            // Map the extraction contract to the UI contract
            var jobTitle = extracted.TryGetProperty("job_title", out var jt) ? jt.GetString() : null;
            var jobDesc = extracted.TryGetProperty("job_description", out var jd) ? jd.GetString() : null;
            var dept = extracted.TryGetProperty("department", out var dp) ? dp.GetString() : null;
            var org = extracted.TryGetProperty("organization", out var o) ? o.GetString() : null;

            var mustHaves = new List<object>();
            if (extracted.TryGetProperty("must_have_requirements", out var mhr) && mhr.ValueKind == JsonValueKind.Array)
                foreach (var item in mhr.EnumerateArray())
                    mustHaves.Add(new { Criterion = item.GetString() ?? "", Description = "" });

            var desiredCriteria = new List<object>();
            if (extracted.TryGetProperty("recommended_or_desired", out var rd) && rd.ValueKind == JsonValueKind.Array)
                foreach (var item in rd.EnumerateArray())
                    desiredCriteria.Add(new { Qualification = item.GetString() ?? "", Description = "" });

            var rubric = new List<object>();
            if (extracted.TryGetProperty("rubric", out var rub) && rub.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                foreach (var cat in cats.EnumerateArray())
                {
                    var criteria = new List<string>();
                    if (cat.TryGetProperty("criteria", out var cr) && cr.ValueKind == JsonValueKind.Array)
                        foreach (var c in cr.EnumerateArray())
                            criteria.Add(c.GetString() ?? "");
                    rubric.Add(new
                    {
                        Name = cat.TryGetProperty("name", out var n) ? n.GetString() : "",
                        Weight = cat.TryGetProperty("weight", out var w) ? w.GetDouble() : 0.0,
                        Description = string.Join("; ", criteria)
                    });
                }

            return Results.Ok(new
            {
                Title = jobTitle,
                JobDescription = jobDesc,
                Department = dept,
                Organisation = org,
                MustHaves = mustHaves,
                DesiredCriteria = desiredCriteria,
                Rubric = rubric
            });
        });

        group.MapPost("/extract-rubric", async (ExtractDocumentRequest request, IHttpClientFactory httpClientFactory) =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
            if (string.IsNullOrEmpty(endpoint))
                return Results.Problem("AWR_SEQ_API_ENDPOINT is not configured", statusCode: 503);

            using var client = httpClientFactory.CreateClient();
            using var formData = new MultipartFormDataContent();

            // Add the rubric extraction prompt as promptFile (required by /assess/passthrough)
            var promptContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(ExtractionPrompts.ExtractRubric));
            promptContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            formData.Add(promptContent, "promptFile", "extract-rubric-prompt.md");

            // Add the uploaded document as specFile (decoded from base64)
            var docBytes = Convert.FromBase64String(request.Content);
            var docContent = new ByteArrayContent(docBytes);
            docContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.MimeType ?? "application/octet-stream");
            formData.Add(docContent, "specFile", request.FileName);

            var response = await client.PostAsync($"{endpoint}/assess/passthrough", formData);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                return Results.Problem(errorText, statusCode: (int)response.StatusCode);
            }

            // Passthrough returns the raw output file directly
            var responseText = await response.Content.ReadAsStringAsync();

            JsonElement extracted;
            try
            {
                extracted = JsonSerializer.Deserialize<JsonElement>(responseText ?? "{}");
            }
            catch
            {
                return Results.Problem("Failed to parse rubric extraction response as JSON", statusCode: 502);
            }

            return Results.Content(extracted.GetRawText(), "application/json");
        });

        group.MapGet("/{jobId}", async (string jobId, ISender mediator) =>
        {
            var job = await mediator.Send(new GetJobDetailQuery(jobId));
            if (job is null) return Results.NotFound();
            return Results.Ok(new
            {
                job.Id, job.JobCode, job.Title, job.Department,
                job.Organisation, job.PostingDate, job.Status,
                job.CurrentConfigVersionId, job.JobDescription, job.CreatedBy, job.CreatedAt
            });
        });

        group.MapGet("/{jobId}/config", async (string jobId, ISender mediator) =>
        {
            var config = await mediator.Send(new GetJobConfigQuery(jobId));
            if (config is null) return Results.NotFound();
            return Results.Ok(new
            {
                config.RubricJson,
                config.MustHaveCriteriaJson,
                config.DesiredCriteriaJson,
                config.ScoringRunCount,
                config.AggregationStrategy,
                config.LonglistThreshold,
                config.ShortlistThreshold,
                config.VarianceThreshold,
                config.RubricApprovalStatus,
                config.RubricSource
            });
        });

        group.MapPut("/{jobId}/config", async (string jobId, UpdateJobConfigRequest request, ISender mediator) =>
        {
            var config = await mediator.Send(new UpdateJobConfigCommand(
                jobId, request.RubricJson, request.MustHaveCriteriaJson, request.DesiredCriteriaJson,
                request.ScoringRunCount, request.AggregationStrategy, request.LonglistThreshold,
                request.ShortlistThreshold, request.VarianceThreshold,
                request.RubricSource ?? "manual", request.RawExtractionResponse));
            return Results.Ok(new
            {
                config.Id,
                config.JobId,
                config.VersionNumber,
                config.RubricJson,
                config.MustHaveCriteriaJson,
                config.DesiredCriteriaJson,
                config.ScoringRunCount,
                config.AggregationStrategy,
                config.LonglistThreshold,
                config.ShortlistThreshold,
                config.VarianceThreshold,
                config.RubricApprovalStatus,
                config.RubricSource,
                config.CreatedAt
            });
        });

        group.MapPost("/{jobId}/process", async (string jobId, ISender mediator) =>
        {
            // Check for production-approved prompt before allowing scoring pipeline trigger
            var prompts = await mediator.Send(new TalentMatch.Application.Prompts.Queries.GetPromptsQuery(jobId));
            var productionPrompt = prompts.FirstOrDefault(p => p.Status == "production-approved");
            if (productionPrompt == null)
                return Results.Problem("No production-approved prompt exists for this job. Approve a prompt before processing.", statusCode: 400);

            // Load job to get run count from config
            var job = await mediator.Send(new GetJobDetailQuery(jobId));
            if (job == null)
                return Results.NotFound();

            var config = job.ConfigVersions
                .FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
                ?? job.ConfigVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            var runCount = config?.ScoringRunCount ?? 3;

            // Load applications for this job
            var applications = await mediator.Send(new TalentMatch.Application.Applications.Queries.GetApplicationsQuery(jobId, null, null, null, null, 1, 10000));
            var toProcess = applications.Where(a => a.Status == "Queued" || a.Status == "Scored").ToList();

            int processed = 0;
            var errors = new List<string>();

            foreach (var app in toProcess)
            {
                try
                {
                    app.Status = "Scoring";
                    var scoreCommand = new TalentMatch.Application.Scoring.Commands.ScoreApplicationCommand(
                        app.Id, jobId, runCount, productionPrompt.Id);
                    await mediator.Send(scoreCommand);
                    app.Status = "Completed";
                    processed++;
                }
                catch (Exception ex)
                {
                    app.Status = "ScoringFailed";
                    errors.Add($"Application {app.Id}: {ex.Message}");
                }
            }

            return Results.Accepted(null, new { processed, total = toProcess.Count, errors });
        });

        group.MapPut("/{jobId}/rubric-approval", async (string jobId, UpdateRubricApprovalRequest request, ISender mediator) =>
        {
            var result = await mediator.Send(new UpdateRubricApprovalCommand(jobId, request.Status));
            return Results.Ok(result);
        });

        group.MapDelete("/{jobId}", async (string jobId, ISender mediator) =>
        {
            var result = await mediator.Send(new DeleteJobCommand(jobId));
            return result ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("AdminOnly");
    }
}

public record CreateJobRequest(
    string Title, string Department, string Organisation, DateTime PostingDate,
    string? RubricJson, string? MustHaveCriteriaJson, string? DesiredCriteriaJson,
    int ScoringRunCount, string AggregationStrategy, double LonglistThreshold,
    double ShortlistThreshold, double VarianceThreshold, string? JobDescription,
    string? RubricSource = "manual", string? RawExtractionResponse = null);

public record UpdateJobConfigRequest(
    string? RubricJson, string? MustHaveCriteriaJson, string? DesiredCriteriaJson,
    int ScoringRunCount, string AggregationStrategy, double LonglistThreshold,
    double ShortlistThreshold, double VarianceThreshold,
    string? RubricSource = "manual", string? RawExtractionResponse = null);

public record UpdateRubricApprovalRequest(string Status);

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
