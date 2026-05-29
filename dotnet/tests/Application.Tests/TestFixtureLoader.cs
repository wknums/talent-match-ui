using System.Text.Json;

namespace TalentMatch.Application.Tests;

public static class TestFixtureLoader
{
    private static readonly Lazy<JsonElement> _fixtures = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "scoring-parser-fixtures.json");
        if (!File.Exists(path))
        {
            // Try relative path from test project directory
            path = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "scoring-parser-fixtures.json");
        }
        var json = File.ReadAllText(path);
        return JsonDocument.Parse(json).RootElement.Clone();
    });

    public static JsonElement LoadFixture(string name)
    {
        if (_fixtures.Value.TryGetProperty(name, out var fixture))
            return fixture;
        throw new ArgumentException($"Fixture '{name}' not found in scoring-parser-fixtures.json");
    }
}
