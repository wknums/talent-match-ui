using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace TalentMatch.Domain.Entities.BulkScoringRequests;

[AttributeUsage(AttributeTargets.Field)]
public sealed class WireNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

// The contract (OpenAPI, shared fixtures, database check constraints) uses snake_case literals.
public static class WireEnum
{
    private static readonly ConcurrentDictionary<Type, (IReadOnlyDictionary<object, string> ToWire, IReadOnlyDictionary<string, object> FromWire)> Cache = new();

    public static string ToWire<T>(this T value) where T : struct, Enum => Map(typeof(T)).ToWire[value];

    public static T Parse<T>(string wire) where T : struct, Enum =>
        TryParse<T>(wire, out var value) ? value : throw new ArgumentException($"'{wire}' is not a valid {typeof(T).Name}.", nameof(wire));

    public static bool TryParse<T>(string? wire, out T value) where T : struct, Enum
    {
        value = default;
        if (wire is null || !Map(typeof(T)).FromWire.TryGetValue(wire, out var boxed)) return false;
        value = (T)boxed;
        return true;
    }

    public static IReadOnlyList<string> AllWireValues<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(x => x.ToWire()).ToArray();

    private static (IReadOnlyDictionary<object, string> ToWire, IReadOnlyDictionary<string, object> FromWire) Map(Type type) =>
        Cache.GetOrAdd(type, static t =>
        {
            var toWire = new Dictionary<object, string>();
            var fromWire = new Dictionary<string, object>();
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var value = field.GetValue(null)!;
                var name = field.GetCustomAttribute<WireNameAttribute>()?.Name ?? SnakeCase(field.Name);
                toWire[value] = name;
                fromWire[name] = value;
            }
            return (toWire, fromWire);
        });

    private static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}

public enum BulkRequestStatus { Draft, Active, Completed }

public enum BulkRequestOutcome { AllSucceeded, Partial, Failed, Cancelled }

public enum BulkDiscoveryStatus { MoreAvailable, Complete, LimitReached }

public enum BulkItemStatus { Waiting, Importing, Converting, Scoring, Completed, Failed, Skipped, Cancelled }

public enum BulkReasonCode
{
    UnsupportedType,
    FileTooLarge,
    LimitExceeded,
    EmptyFile,
    NotConvertible,
    AccessDenied,
    AuthorizationLapsed,
    SourceMissing,
    SourceChanged,
    DuplicateInRequest,
    DuplicateInJob,
    ConversionFailed,
    ConversionTimeout,
    ConvertedTypeInvalid,
    ConvertedMagicInvalid,
    ConvertedDocumentInvalid,
    ConvertedTooLarge,
    ConversionPageMismatch,
    ConversionPageInvalid,
    ConversionDimensionMismatch,
    ConversionSsimBelowThreshold,
    ConversionBlankPage,
    ConversionCroppedPage,
    ScoringFailed,
    CancelledByUser,
    TransientExhausted,
}

[Obsolete("The v3 connector has one delegated access model; retained only to read historical test data.")]
public enum SharePointAccessModel { SameTenant, CrossTenant }

public enum BulkSourceKind
{
    [WireName("sharepoint")] SharePoint,
    [WireName("onedrive_business")] OneDriveBusiness,
}

public enum BulkSourceConnectionStatus
{
    Authorizing,
    Authorized,
    Verified,
    Consumed,
    Expired,
    Abandoned,
    Failed,
}

public enum BulkAuthorizationStatus
{
    Active,
    Lapsed,
    Revoked,
    [WireName("accessLost")] AccessLost,
    Discarded,
    [WireName("unavailableHistorical")] UnavailableHistorical,
}

public enum BulkContentKind { Original, Converted, Transient }

public enum BulkScoredVersion { Original, Converted }

public enum BulkSourceAdmissionKind { ConnectionActor, ConnectionIp, CallbackIp }

public enum BulkAuditActorType { User, Service }

public enum BulkAuditBoundary { Transactional, DurableOutbox, Reconciliation }

// Exclusion categories are the subset of reason codes that can be tallied at discovery.
public static class BulkExclusionCategories
{
    public static readonly IReadOnlyList<BulkReasonCode> All =
    [
        BulkReasonCode.UnsupportedType,
        BulkReasonCode.FileTooLarge,
        BulkReasonCode.EmptyFile,
        BulkReasonCode.NotConvertible,
    ];

    public static bool IsExclusionCategory(BulkReasonCode code) => All.Contains(code);
}
