namespace TalentMatch.Domain.Entities;

public sealed class UploadSettings
{
    public const string SingletonId = "optional-file-upload";
    public const int DefaultFileConcurrency = 4;
    public const long DefaultMaxIndividualFileBytes = 4_194_304;
    public const long DefaultMaxInFlightBytes = 104_857_600;

    public string Id { get; set; } = SingletonId;
    public int FileConcurrency { get; set; } = DefaultFileConcurrency;
    public long MaxIndividualFileBytes { get; set; } = DefaultMaxIndividualFileBytes;
    public long MaxInFlightBytes { get; set; } = DefaultMaxInFlightBytes;
    public int ConcurrencyVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = string.Empty;

    public static UploadSettings Defaults() => new()
    {
        ConcurrencyVersion = 0,
        CreatedAt = default,
        UpdatedAt = default,
    };

    public static IReadOnlyDictionary<string, string[]> Validate(
        int fileConcurrency,
        long maxIndividualFileBytes,
        long maxInFlightBytes)
    {
        var errors = new Dictionary<string, string[]>();
        if (fileConcurrency <= 0)
            errors[nameof(FileConcurrency)] = ["File concurrency must be a positive whole number."];
        if (maxIndividualFileBytes <= 0)
            errors[nameof(MaxIndividualFileBytes)] = ["Maximum individual file bytes must be positive."];
        if (maxInFlightBytes <= 0)
            errors[nameof(MaxInFlightBytes)] = ["Maximum in-flight bytes must be positive."];
        else if (maxIndividualFileBytes > 0 && maxInFlightBytes < maxIndividualFileBytes)
            errors[nameof(MaxInFlightBytes)] = ["Maximum in-flight bytes must be at least the individual file limit."];
        return errors;
    }
}
