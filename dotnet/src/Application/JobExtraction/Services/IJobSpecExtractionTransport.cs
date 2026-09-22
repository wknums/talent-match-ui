namespace TalentMatch.Application.JobExtraction.Services;

public interface IJobSpecExtractionTransport
{
    Task<string> ExtractAsync(
        byte[] documentBytes,
        string fileName,
        string mimeType,
        string prompt,
        CancellationToken cancellationToken = default);

    Task<string> ExtractAsync(
        byte[] documentBytes,
        string fileName,
        string mimeType,
        string prompt,
        string modelId,
        string reasoningLevel,
        CancellationToken cancellationToken = default)
        => ExtractAsync(
            documentBytes,
            fileName,
            mimeType,
            prompt,
            cancellationToken);
}
