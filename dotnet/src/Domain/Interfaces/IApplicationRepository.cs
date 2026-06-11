namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IApplicationRepository
{
    Task<IReadOnlyList<Application>> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default);
    Task<Application?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(Application application, CancellationToken cancellationToken = default);
    Task UpdateAsync(Application application, CancellationToken cancellationToken = default);
    Task AddDocumentAsync(ApplicationDocument document, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationDocument>> GetDocumentsAsync(string applicationId, CancellationToken cancellationToken = default);
    Task SetDocumentBlobReferenceAsync(string documentId, string blobUri, string contentSha256, CancellationToken cancellationToken = default);
    Task AddScoringRunAsync(ScoringRun run, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScoringRun>> GetScoringRunsAsync(string applicationId, CancellationToken cancellationToken = default);
    Task<ScoringRun?> GetScoringRunByIdAsync(string scoringRunId, CancellationToken cancellationToken = default);
    Task UpdateScoringRunAsync(ScoringRun run, CancellationToken cancellationToken = default);
    Task SetAggregatedResultAsync(AggregatedResult result, CancellationToken cancellationToken = default);
    Task<AggregatedResult?> GetAggregatedResultAsync(string applicationId, CancellationToken cancellationToken = default);
    Task SetExtractionAsync(ExtractionArtifact extraction, CancellationToken cancellationToken = default);
    Task<ExtractionArtifact?> GetExtractionAsync(string applicationId, CancellationToken cancellationToken = default);
    Task SetManualReviewAsync(ManualReviewData review, CancellationToken cancellationToken = default);
    Task<ManualReviewData?> GetManualReviewAsync(string applicationId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
