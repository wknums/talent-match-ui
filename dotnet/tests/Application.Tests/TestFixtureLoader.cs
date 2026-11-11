using System.Text.Json;

namespace TalentMatch.Application.Tests;

public static class TestFixtureLoader
{
    private static readonly Lazy<JsonElement> _parserFixtures = new(() =>
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
        if (_parserFixtures.Value.TryGetProperty(name, out var fixture))
            return fixture;
        throw new ArgumentException($"Fixture '{name}' not found in scoring-parser-fixtures.json");
    }

    public static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                    && Directory.Exists(Path.Combine(directory.FullName, "dotnet"))
                    && Directory.Exists(Path.Combine(directory.FullName, "specs")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    public static string LoadFeatureFixtureText(string fileName)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "specs",
            "001-dynamic-rubric-editor",
            "contracts",
            "fixtures",
            fileName);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Feature fixture '{fileName}' was not found at '{path}'.", path);

        return File.ReadAllText(path);
    }

    public static JsonElement LoadFeatureFixtureJson(string fileName)
        => JsonDocument.Parse(LoadFeatureFixtureText(fileName)).RootElement.Clone();

    public static T LoadFeatureFixture<T>(string fileName)
        => JsonSerializer.Deserialize<T>(LoadFeatureFixtureText(fileName), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException($"Fixture '{fileName}' could not be deserialized as {typeof(T).Name}.");
}
