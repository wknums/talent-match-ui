using MediatR;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Application.Applications.Queries;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Web.Server.Endpoints;

public static class ApplicationsEndpoints
{
    public static void MapApplicationsEndpoints(this WebApplication app)
    {
        var jobAppsGroup = app.MapGroup("/api/jobs/{jobId}/applications").WithTags("Applications").RequireAuthorization();

        jobAppsGroup.MapPost("/upload", async (string jobId, bool? allowDuplicates, HttpRequest request, ISender mediator, CancellationToken ct) =>
        {
            var files = new List<UploadedFile>();
            var form = await request.ReadFormAsync(ct);
            foreach (var file in form.Files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();
                var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                files.Add(new UploadedFile(file.FileName, file.ContentType, file.Length, Convert.ToBase64String(bytes), fingerprint));
            }
            var result = await mediator.Send(new UploadApplicationsCommand(
                jobId,
                files,
                AllowDuplicates: allowDuplicates == true), ct);
            return Results.Ok(result);
        }).DisableAntiforgery();

        jobAppsGroup.MapGet("/", async (string jobId, string? list, string? applicantName, string? sortField, string? sortOrder,
            double? varianceMin, int? page, int? pageSize, ISender mediator) =>
        {
            var apps = await mediator.Send(new GetApplicationsQuery(
                jobId, list, applicantName, sortField, sortOrder, varianceMin, page ?? 1, pageSize ?? 50));
            return Results.Ok(apps);
        });

        var appGroup = app.MapGroup("/api/applications/{applicationId}").WithTags("Applications").RequireAuthorization();
        appGroup.AddEndpointFilter(async (context, next) =>
        {
            var applicationId = context.HttpContext.Request.RouteValues["applicationId"]?.ToString();
            if (string.IsNullOrWhiteSpace(applicationId))
                return Results.NotFound();

            var services = context.HttpContext.RequestServices;
            var applications = services.GetRequiredService<IApplicationRepository>();
            var application = await applications.GetByIdAsync(
                applicationId, context.HttpContext.RequestAborted);

            if (application is null)
                return Results.NotFound();

            var jobs = services.GetRequiredService<IJobRepository>();
            var job = await jobs.GetByIdAsync(application.JobId, context.HttpContext.RequestAborted);
            if (job is null)
                return Results.NotFound();

            try
            {
                var currentUser = services.GetService<ICurrentUserService>();
                var organizations = services.GetService<IOrganizationRepository>();
                if (HttpMethods.IsGet(context.HttpContext.Request.Method)
                    || HttpMethods.IsHead(context.HttpContext.Request.Method))
                {
                    await JobAuthorization.EnsureCanReadAsync(
                        job, currentUser, organizations, context.HttpContext.RequestAborted);
                }
                else
                {
                    await JobAuthorization.EnsureCanMutateAsync(
                        job, currentUser, organizations, context.HttpContext.RequestAborted);
                }
            }
            catch (InvalidJobScopeException)
            {
                return AuthorizationErrorResults.Create(
                    context.HttpContext, AuthorizationErrorCodes.InvalidJobScope);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(
                    context.HttpContext, AuthorizationErrorCodes.Forbidden);
            }

            return await next(context);
        });

        appGroup.MapGet("/", async (string applicationId, IApplicationRepository repo) =>
        {
            var application = await repo.GetByIdAsync(applicationId);
            return application != null ? Results.Ok(application) : Results.NotFound();
        });

        appGroup.MapGet("/runs", async (string applicationId, ISender mediator) =>
        {
            var runs = await mediator.Send(new GetScoringRunsQuery(applicationId));
            return Results.Ok(runs);
        });

        appGroup.MapPost("/runs/{scoringRunId}/reparse", async (string applicationId, string scoringRunId, ReparseScoringRunRequest? request, ISender mediator) =>
        {
            try
            {
                var result = await mediator.Send(new ReparseScoringRunCommand(applicationId, scoringRunId, request?.RawJson));
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        appGroup.MapGet("/result", async (string applicationId, ISender mediator) =>
        {
            var result = await mediator.Send(new GetAggregatedResultQuery(applicationId));
            // No aggregate exists yet for queued/in-flight applications.
            // Return 200 with null payload to avoid unnecessary client-side 404 noise.
            return Results.Ok(result);
        });

        appGroup.MapGet("/documents", async (string applicationId, IApplicationRepository repo) =>
        {
            var documents = await repo.GetDocumentsAsync(applicationId);
            return Results.Ok(documents.Select(d => new { d.Id, d.FileName, d.FileType, d.FileSize, d.ContentBase64 }));
        });

        // GET /api/applications/:applicationId/documents/:documentId/content - raw document bytes (FR-057)
        appGroup.MapGet("/documents/{documentId}/content", async (string applicationId, string documentId, IApplicationRepository repo) =>
        {
            var documents = await repo.GetDocumentsAsync(applicationId);
            var doc = documents.FirstOrDefault(d => d.Id == documentId);
            if (doc == null)
                return Results.NotFound();

            if (string.IsNullOrEmpty(doc.ContentBase64))
                return Results.NotFound("Document content not available");

            var bytes = Convert.FromBase64String(doc.ContentBase64);
            return Results.File(bytes, doc.FileType);
        });

        appGroup.MapGet("/extraction", async (string applicationId, IApplicationRepository repo) =>
        {
            var extraction = await repo.GetExtractionAsync(applicationId);
            return extraction != null ? Results.Ok(extraction) : Results.NotFound();
        });

        appGroup.MapGet("/manual-review", async (string applicationId, IApplicationRepository repo) =>
        {
            var review = await repo.GetManualReviewAsync(applicationId);
            return review != null
                ? Results.Ok(review)
                : Results.Text("null", "application/json");
        });

        appGroup.MapPost("/manual-review", async (string applicationId, SaveManualReviewRequest request, ISender mediator) =>
        {
            var review = await mediator.Send(new SaveManualReviewCommand(
                applicationId, request.RubricScoresJson, request.OverallComment,
                request.AdjustedFinalScore, request.AuditTrailJson, request.HumanEdited, request.FinalDecision));
            return Results.Ok(review);
        });
    }
}

public record SaveManualReviewRequest(string RubricScoresJson, string OverallComment, double? AdjustedFinalScore, string AuditTrailJson, bool HumanEdited = false, string? FinalDecision = null);
public record ReparseScoringRunRequest(string? RawJson);
