using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.Common.Interfaces;

public sealed record RasterizationOptions(int Dpi, int JpegQuality, int MaxPages, TimeSpan Timeout);

public sealed record RasterizationResult(byte[] PdfBytes, int PageCount)
{
    public string MediaType { get; init; } = "application/pdf";
    public long SizeBytes => PdfBytes.LongLength;
    public int OriginalPageCount { get; init; } = PageCount;
    public int RasterDpiUsed { get; init; }
    public double MinimumPageSsim { get; init; }
    public IReadOnlyList<double> PageSsim { get; init; } = [];
    public DateTime? FidelityValidatedAt { get; init; }
    public BulkReasonCode? RejectionReason { get; init; }
    // RasterDpiUsed=0 is retained for existing in-process test doubles; the production
    // PdfiumRasterizer always returns a non-zero DPI and a validation timestamp.
    public bool IsAdmitted => RejectionReason is null && (FidelityValidatedAt is not null || RasterDpiUsed == 0);

    public static RasterizationResult Rejected(byte[] awaitableBytes, BulkReasonCode reason) =>
        new(awaitableBytes, 0) { RejectionReason = reason };
}

public interface IDocumentRasterizer
{
    // Produces an image-only PDF with the same page count and order. Throws BulkConversionException
    // (conversion_failed, conversion_timeout, limit_exceeded) rather than falling back to the original.
    Task<RasterizationResult> RasterizeAsync(
        byte[] source,
        string mimeType,
        RasterizationOptions options,
        CancellationToken cancellationToken = default);
}

public interface IBulkContentStore
{
    // Stages bytes under bulk/{requestId}/{itemId}/{kind} (blob via managed identity when configured,
    // otherwise inline). Returns the unsaved content row; the repository owns persistence.
    Task<BulkScoringItemContent> StageAsync(
        string requestId,
        string itemId,
        BulkContentKind kind,
        string fileName,
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default);

    Task DeleteAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default);

    // Deletes every staged or retained object for the request (job deletion, FR-021).
    Task DeleteByRequestAsync(string requestId, CancellationToken cancellationToken = default);
}
