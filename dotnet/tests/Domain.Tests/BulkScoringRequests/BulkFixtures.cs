using System.Text.Json;

namespace TalentMatch.Domain.Tests.BulkScoringRequests;

// Locates tests/fixtures/bulk-scoring-requests by walking up from the test binary.
internal static class BulkFixtures
{
    public static string Root { get; } = FindRoot();

    public static JsonElement Contract { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "contract-fixtures.json"))).RootElement;

    public static IReadOnlyList<string> Strings(string property) =>
        Contract.GetProperty(property).EnumerateArray().Select(x => x.GetString()!).ToArray();

    public static IReadOnlyDictionary<string, string[]> Table(string property) =>
        Contract.GetProperty(property).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(x => x.GetString()!).ToArray());

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "bulk-scoring-requests");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("tests/fixtures/bulk-scoring-requests was not found above " + AppContext.BaseDirectory);
    }
}
