using System.Text.Json;

namespace TalentMatch.Application.BulkScoringRequests;

// Snapshot taken when discovery starts and stored on the request, so a running request keeps the
// limits it started with even if an administrator changes the settings later.
public sealed record BulkRequestLimits(
    int MaxFilesPerRequest,
    long MaxBytesPerFile,
    long MaxTotalBytes,
    int MaxPagesPerDocument,
    int DiscoveryStepFiles,
    int DiscoveryStepMaxEntries,
    int DiscoveryStepMaxSeconds,
    int MaxAttempts,
    int RasterDpi,
    int RasterJpegQuality)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BulkRequestLimits Snapshot(BulkScoringOptions options, long maxBytesPerFile, int maxFilesPerRequest) => new(
        maxFilesPerRequest,
        maxBytesPerFile,
        options.MaxTotalBytes,
        options.RasterMaxPages,
        options.DiscoveryStepFiles,
        options.DiscoveryStepMaxEntries,
        (int)options.DiscoveryStepMaxDuration.TotalSeconds,
        options.MaxAttempts,
        options.RasterDpi,
        options.RasterJpegQuality);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static BulkRequestLimits FromJson(string json) =>
        JsonSerializer.Deserialize<BulkRequestLimits>(json, Json)
        ?? throw new InvalidOperationException("The request's limits snapshot is missing.");
}
