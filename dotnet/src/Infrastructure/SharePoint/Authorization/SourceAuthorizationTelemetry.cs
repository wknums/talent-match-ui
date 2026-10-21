using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

/// <summary>
/// Safe observability boundary for source authorization. Callers provide only opaque correlation
/// references and closed transition/outcome/reason codes. Source URLs, names, accounts, tenants,
/// tokens, provider payloads, and exception text are deliberately absent from this API.
/// </summary>
public sealed class SourceAuthorizationTelemetry(ILogger<SourceAuthorizationTelemetry> logger)
{
    public const string MeterName = "TalentMatch.Microsoft365SourceAuthorization";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>(
        "microsoft365.source.authorization.transitions",
        description: "Delegated source authorization transitions by closed safe codes.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "microsoft365.source.authorization.failures",
        description: "Delegated source authorization failures by closed safe reason code.");

    public void Record(
        string transition,
        string outcome,
        string? reasonCode,
        string correlationId)
    {
        var safeTransition = SafeCode(transition);
        var safeOutcome = SafeCode(outcome);
        var safeReason = SafeCode(reasonCode);
        var tags = new TagList
        {
            { "transition", safeTransition },
            { "outcome", safeOutcome },
            { "reason_code", safeReason },
        };
        Transitions.Add(1, tags);
        if (safeOutcome == "failed")
            Failures.Add(1, tags);

        logger.LogInformation(
            "Microsoft 365 source authorization {Transition} finished with {Outcome}; reason {ReasonCode}; correlation {CorrelationId}",
            safeTransition,
            safeOutcome,
            safeReason,
            correlationId);
    }

    internal static string SafeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var candidate = value.Trim().ToLowerInvariant();
        return Regex.IsMatch(candidate, "^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)
            ? candidate
            : "unspecified";
    }
}
