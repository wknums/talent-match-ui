using MediatR;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Application.Applications.Queries;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Web.Server.Endpoints;

public static class ApplicationsEndpoints
{
    public static void MapApplicationsEndpoints(this WebApplication app)
    {
        var jobAppsGroup = app.MapGroup("/api/jobs/{jobId}/applications").WithTags("Applications").RequireAuthorization();

        jobAppsGroup.MapPost("/upload", async (string jobId, HttpRequest request, ISender mediator) =>
        {
            var files = new List<UploadedFile>();
            var form = await request.ReadFormAsync();
            foreach (var file in form.Files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var bytes = ms.ToArray();
                var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                files.Add(new UploadedFile(file.FileName, file.ContentType, file.Length, Convert.ToBase64String(bytes), fingerprint));
            }
            var result = await mediator.Send(new UploadApplicationsCommand(jobId, files));
            return Results.Ok(result);
        }).DisableAntiforgery();

        jobAppsGroup.MapGet("/", async (string jobId, string? list, string? sortField, string? sortOrder,
            double? varianceMin, int? page, int? pageSize, ISender mediator) =>
        {
            var apps = await mediator.Send(new GetApplicationsQuery(
                jobId, list, sortField, sortOrder, varianceMin, page ?? 1, pageSize ?? 50));
            return Results.Ok(apps);
        });

        var appGroup = app.MapGroup("/api/applications/{applicationId}").WithTags("Applications").RequireAuthorization();

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

        appGroup.MapGet("/result", async (string applicationId, ISender mediator) =>
        {
            var result = await mediator.Send(new GetAggregatedResultQuery(applicationId));
            return result != null ? Results.Ok(result) : Results.NotFound();
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
            return review != null ? Results.Ok(review) : Results.NotFound();
        });

        appGroup.MapPost("/manual-review", async (string applicationId, SaveManualReviewRequest request, ISender mediator) =>
        {
            var review = await mediator.Send(new SaveManualReviewCommand(
                applicationId, request.RubricScoresJson, request.OverallComment,
                request.AdjustedFinalScore, request.AuditTrailJson, request.HumanEdited));
            return Results.Ok(review);
        });
    }
}

public record SaveManualReviewRequest(string RubricScoresJson, string OverallComment, double? AdjustedFinalScore, string AuditTrailJson, bool HumanEdited = false);
