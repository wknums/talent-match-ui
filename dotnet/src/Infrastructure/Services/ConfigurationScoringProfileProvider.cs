using Microsoft.Extensions.Configuration;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.Services;

public sealed class ConfigurationScoringProfileProvider : IScoringProfileProvider
{
    public const string DefaultModelId = "passthrough-llm";
    public const string DefaultReasoningLevel = "medium";

    public ConfigurationScoringProfileProvider(IConfiguration configuration)
    {
        Current = new ScoringProfile(
            Resolve(configuration, "AWR_MODEL_ID", "AWR_MODEL", DefaultModelId),
            Resolve(configuration, "AWR_REASONING_LEVEL", "AWR_REASONING", DefaultReasoningLevel));
    }

    public ScoringProfile Current { get; }

    private static string Resolve(
        IConfiguration configuration,
        string primaryName,
        string compatibilityName,
        string fallback)
        => (configuration[primaryName]
            ?? configuration[compatibilityName]
            ?? Environment.GetEnvironmentVariable(primaryName)
            ?? Environment.GetEnvironmentVariable(compatibilityName)
            ?? fallback).Trim();
}
