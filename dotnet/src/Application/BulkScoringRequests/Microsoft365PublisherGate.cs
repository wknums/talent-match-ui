namespace TalentMatch.Application.BulkScoringRequests;

/// <summary>
/// Product deployment activation decision for the Microsoft 365 connector. Source tenant,
/// account, host, site, drive, folder, and consent data are deliberately not inputs.
/// </summary>
public static class Microsoft365PublisherGate
{
    public static bool CanActivate(string productEnvironment, bool? publisherVerified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productEnvironment);

        if (string.Equals(productEnvironment, "Production", StringComparison.OrdinalIgnoreCase))
            return publisherVerified is true;

        if (string.Equals(productEnvironment, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(productEnvironment, "QA", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return publisherVerified is true;
    }

    public static void RequireActivation(string productEnvironment, bool? publisherVerified)
    {
        if (!CanActivate(productEnvironment, publisherVerified))
        {
            throw new InvalidOperationException(
                "The Microsoft 365 source connector is unavailable in this product environment.");
        }
    }
}
