using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Infrastructure.Rasterization;

// Placeholder until the PDFium rasterizer lands (T044). Files that need conversion fail with a clear
// reason instead of being scored unconverted; everything else works normally.
public sealed class UnavailableDocumentRasterizer : IDocumentRasterizer
{
    public Task<RasterizationResult> RasterizeAsync(
        byte[] source,
        string mimeType,
        RasterizationOptions options,
        CancellationToken cancellationToken = default) =>
        throw new BulkConversionException(
            BulkReasonCode.ConversionFailed,
            "Image-based PDF conversion is not available in this build.");
}
