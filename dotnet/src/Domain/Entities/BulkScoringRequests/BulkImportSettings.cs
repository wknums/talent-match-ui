namespace TalentMatch.Domain.Entities.BulkScoringRequests;

public sealed class BulkImportSettings
{
    public const int SingletonId = 1;
    public const int DefaultMaxFilesPerRequest = 30_000;
    public const int MaximumMaxFilesPerRequest = 100_000;

    public int Id { get; set; } = SingletonId;
    public int MaxFilesPerRequest { get; set; } = DefaultMaxFilesPerRequest;
    public int ConcurrencyVersion { get; set; } = 1;
    public int ConfigurationVersion { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedByActorId { get; set; } = string.Empty;

    // An absent row is valid and resolves to the default.
    public static BulkImportSettings Defaults() => new()
    {
        ConcurrencyVersion = 0,
        UpdatedAt = default,
    };

    public static IReadOnlyDictionary<string, string[]> Validate(long maxFilesPerRequest)
    {
        var errors = new Dictionary<string, string[]>();
        if (maxFilesPerRequest <= 0 || maxFilesPerRequest > MaximumMaxFilesPerRequest)
            errors[nameof(MaxFilesPerRequest)] =
                [$"Maximum files per bulk scoring request must be between 1 and {MaximumMaxFilesPerRequest:N0}."];
        return errors;
    }
}
