using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Web.Tests;

public sealed class AuthorizationParityTests
{
    private static readonly string[] RequiredAreas =
    [
        "auth",
        "access-management",
        "organization-admin",
        "jobs",
        "revocation",
        "mode",
    ];

    [Fact]
    public async Task StackB_MatchesSharedAuthorizationParityFixture()
    {
        var fixturePath = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "authorization-parity-cases.json");
        File.Exists(fixturePath).Should().BeTrue(
            "T075 must provide the shared parity fixture at {0}", fixturePath);

        await using var stream = File.OpenRead(fixturePath);
        using var fixture = await JsonDocument.ParseAsync(stream);
        var cases = ReadCases(fixture.RootElement).ToArray();
        cases.Should().NotBeEmpty("the shared parity fixture must contain executable cases");

        var coveredAreas = cases
            .Select(testCase => CanonicalArea(GetRequiredString(testCase, "area", "surface", "category")))
            .ToHashSet(StringComparer.Ordinal);
        coveredAreas.Should().Contain(RequiredAreas,
            "T077 requires auth, access management, organization admin, jobs, revocation, and mode outcomes");

        foreach (var testCase in cases)
        {
            var caseId = GetRequiredString(testCase, "id", "name");
            try
            {
                using var response = await ExecuteAsync(testCase);
                await AssertResponseAsync(caseId, response, GetRequiredProperty(testCase, "expected"));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Authorization parity case '{caseId}' failed.", exception);
            }
        }
    }

    private static IEnumerable<JsonElement> ReadCases(JsonElement root)
    {
        var cases = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when TryGetProperty(root, out var value, "cases") => value,
            _ => throw new JsonException("The parity fixture must be an array or an object with a cases array."),
        };

        if (cases.ValueKind != JsonValueKind.Array)
            throw new JsonException("The parity fixture cases value must be an array.");

        foreach (var testCase in cases.EnumerateArray())
        {
            if (testCase.ValueKind != JsonValueKind.Object)
                throw new JsonException("Every parity case must be a JSON object.");
            yield return testCase;
        }
    }

    private static async Task<HttpResponseMessage> ExecuteAsync(JsonElement testCase)
    {
        var area = CanonicalArea(GetRequiredString(testCase, "area", "surface", "category"));
        var host = TryGetString(testCase, "host", "stackBHost") ?? DefaultHost(area, testCase);
        var scenario = TryGetString(testCase, "authScenario", "actor", "identity");
        var request = TryGetProperty(testCase, out var requestValue, "request") ? requestValue : testCase;

        return Normalize(host) switch
        {
            "auth" => await ExecuteAuthAsync(request, scenario),
            "access" or "accessmanagement" => await ExecuteAccessAsync(testCase, request, scenario),
            "organization" or "organizationadmin" or "jobs" =>
                await ExecuteOrganizationAsync(request, scenario),
            "simple" or "simplemode" => await ExecuteSimpleAsync(request),
            _ => throw new JsonException($"Unsupported Stack B parity host '{host}'."),
        };
    }

    private static async Task<HttpResponseMessage> ExecuteAuthAsync(JsonElement request, string? scenario)
    {
        using var factory = new EntraAuthEndpointsTests.EntraAuthFactory();
        using var client = factory.CreateClient(ClientOptions());
        if (!string.IsNullOrWhiteSpace(scenario))
            client.DefaultRequestHeaders.Add("X-Test-Auth", scenario);
        return await SendAsync(client, request);
    }

    private static async Task<HttpResponseMessage> ExecuteAccessAsync(
        JsonElement testCase,
        JsonElement request,
        string? scenario)
    {
        using var factory = new EntraAccessManagementEndpointsTests.EntraFactory();
        factory.Repository.Reset();
        if (TryGetProperty(testCase, out var repositoryError, "repositoryError"))
        {
            factory.Repository.NextException = new EntraAccessManagementException(
                GetRequiredString(repositoryError, "code", "error"),
                TryGetString(repositoryError, "message") ?? "Configured parity failure.");
        }

        using var client = factory.CreateClient(ClientOptions());
        if (!string.IsNullOrWhiteSpace(scenario))
            client.DefaultRequestHeaders.Add("X-Test-Auth", scenario);
        return await SendAsync(client, request);
    }

    private static async Task<HttpResponseMessage> ExecuteOrganizationAsync(
        JsonElement request,
        string? scenario)
    {
        using var factory = new OrganizationEndpointsTests.OrganizationFactory();
        await factory.ResetAsync();
        using var client = factory.CreateClient(ClientOptions());
        if (!string.IsNullOrWhiteSpace(scenario))
            client.DefaultRequestHeaders.Add("X-Test-Auth", scenario);
        return await SendAsync(client, request);
    }

    private static async Task<HttpResponseMessage> ExecuteSimpleAsync(JsonElement request)
    {
        using var factory = new EntraAccessManagementEndpointsTests.SimpleFactory();
        using var client = factory.CreateAuthenticatedClient();
        return await SendAsync(client, request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, JsonElement requestDefinition)
    {
        var method = GetRequiredString(requestDefinition, "method");
        var path = GetRequiredString(requestDefinition, "path", "url");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (TryGetProperty(requestDefinition, out var headers, "headers"))
        {
            if (headers.ValueKind != JsonValueKind.Object)
                throw new JsonException("Request headers must be a JSON object.");
            foreach (var header in headers.EnumerateObject())
                request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
        }

        if (TryGetProperty(requestDefinition, out var body, "body", "json")
            && body.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertResponseAsync(
        string caseId,
        HttpResponseMessage response,
        JsonElement expected)
    {
        var expectedStatus = GetRequiredInt32(expected, "status", "statusCode");
        ((int)response.StatusCode).Should().Be(expectedStatus, "case {0} must preserve status parity", caseId);

        var content = await response.Content.ReadAsStringAsync();
        var hasExpectedBody = TryGetProperty(expected, out var expectedBody, "body", "json", "publicBody");
        var hasExpectedError = TryGetProperty(expected, out var expectedError, "error");
        var hasPublicFields = TryGetProperty(expected, out var publicFields, "publicFields", "fields");
        var hasAbsentFields = TryGetProperty(expected, out var absentFields, "absentFields");

        if (string.IsNullOrWhiteSpace(content))
        {
            hasExpectedBody.Should().BeFalse("case {0} expected a JSON body but Stack B returned none", caseId);
            hasExpectedError.Should().BeFalse("case {0} expected an error body but Stack B returned none", caseId);
            return;
        }

        JsonDocument actualDocument;
        try
        {
            actualDocument = JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new JsonException($"Case '{caseId}' returned a non-JSON public body: {content}", exception);
        }

        using (actualDocument)
        {
            var actual = actualDocument.RootElement;
            (hasExpectedBody || hasExpectedError || hasPublicFields || hasAbsentFields).Should().BeTrue(
                "case {0} returned JSON, so the fixture must declare its public body or fields", caseId);

            if (hasExpectedBody)
                AssertJsonParity(caseId, "$", actual, expectedBody);

            if (hasExpectedError)
            {
                actual.ValueKind.Should().Be(JsonValueKind.Object, "case {0} error response must be an object", caseId);
                actual.TryGetProperty("error", out var actualError).Should().BeTrue(
                    "case {0} error response must expose error", caseId);
                AssertJsonParity(caseId, "$.error", actualError, expectedError);
            }

            if (hasPublicFields)
                AssertPublicFields(caseId, actual, publicFields, shouldExist: true);
            if (hasAbsentFields)
                AssertPublicFields(caseId, actual, absentFields, shouldExist: false);

            if (actual.ValueKind == JsonValueKind.Object
                && actual.TryGetProperty("correlationId", out var correlationId)
                && response.Headers.TryGetValues("X-Correlation-ID", out var values))
            {
                correlationId.GetString().Should().Be(values.Single(),
                    "case {0} body and header correlation IDs must match", caseId);
            }
        }

        if (TryGetProperty(expected, out var expectedHeaders, "headers"))
        {
            foreach (var header in expectedHeaders.EnumerateObject())
            {
                response.Headers.TryGetValues(header.Name, out var values).Should().BeTrue(
                    "case {0} must return public header {1}", caseId, header.Name);
                AssertStringParity(caseId, $"header {header.Name}", values!.Single(), header.Value.GetString());
            }
        }
    }

    private static void AssertJsonParity(
        string caseId,
        string path,
        JsonElement actual,
        JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.String
            && IsMatcher(expected.GetString()))
        {
            AssertMatcher(caseId, path, actual, expected.GetString()!);
            return;
        }

        actual.ValueKind.Should().Be(expected.ValueKind,
            "case {0} field {1} must preserve its public JSON type", caseId, path);

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProperties = expected.EnumerateObject().ToArray();
                var actualProperties = actual.EnumerateObject().ToArray();
                actualProperties.Select(property => property.Name).Should().BeEquivalentTo(
                    expectedProperties.Select(property => property.Name),
                    "case {0} field {1} must expose exactly the same public fields", caseId, path);
                foreach (var property in expectedProperties)
                {
                    actual.TryGetProperty(property.Name, out var actualProperty).Should().BeTrue();
                    AssertJsonParity(caseId, $"{path}.{property.Name}", actualProperty, property.Value);
                }
                break;
            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                actualItems.Should().HaveCount(expectedItems.Length,
                    "case {0} field {1} must preserve public array cardinality", caseId, path);
                for (var index = 0; index < expectedItems.Length; index++)
                    AssertJsonParity(caseId, $"{path}[{index}]", actualItems[index], expectedItems[index]);
                break;
            case JsonValueKind.String:
                actual.GetString().Should().Be(expected.GetString(),
                    "case {0} field {1} must preserve its public value", caseId, path);
                break;
            case JsonValueKind.Number:
                actual.GetRawText().Should().Be(expected.GetRawText(),
                    "case {0} field {1} must preserve its public numeric value", caseId, path);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                actual.GetBoolean().Should().Be(expected.GetBoolean(),
                    "case {0} field {1} must preserve its public boolean value", caseId, path);
                break;
            case JsonValueKind.Null:
                break;
            default:
                throw new JsonException($"Unsupported expected JSON kind at {path}: {expected.ValueKind}.");
        }
    }

    private static void AssertPublicFields(
        string caseId,
        JsonElement actual,
        JsonElement fields,
        bool shouldExist)
    {
        actual.ValueKind.Should().Be(JsonValueKind.Object,
            "case {0} public field assertions require an object response", caseId);
        fields.ValueKind.Should().Be(JsonValueKind.Array,
            "case {0} publicFields and absentFields must be arrays", caseId);

        foreach (var field in fields.EnumerateArray())
        {
            var name = field.GetString() ?? throw new JsonException("Public field names must be strings.");
            actual.TryGetProperty(name, out _).Should().Be(shouldExist,
                "case {0} field {1} public presence must match the shared fixture", caseId, name);
        }
    }

    private static void AssertMatcher(string caseId, string path, JsonElement actual, string matcher)
    {
        switch (Normalize(matcher.Trim('<', '>', '$', '-', '_')))
        {
            case "any":
                return;
            case "uuid":
                actual.ValueKind.Should().Be(JsonValueKind.String);
                Guid.TryParse(actual.GetString(), out _).Should().BeTrue(
                    "case {0} field {1} must be a UUID", caseId, path);
                return;
            case "nonempty":
                actual.ValueKind.Should().Be(JsonValueKind.String);
                actual.GetString().Should().NotBeNullOrWhiteSpace(
                    "case {0} field {1} must be non-empty", caseId, path);
                return;
            case "datetime" or "isodatetime":
                actual.ValueKind.Should().Be(JsonValueKind.String);
                DateTimeOffset.TryParse(actual.GetString(), out _).Should().BeTrue(
                    "case {0} field {1} must be an ISO date-time", caseId, path);
                return;
            default:
                throw new JsonException($"Unsupported parity matcher '{matcher}' at {path}.");
        }
    }

    private static void AssertStringParity(
        string caseId,
        string path,
        string actual,
        string? expected)
    {
        if (expected is not null && IsMatcher(expected))
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(actual));
            AssertMatcher(caseId, path, document.RootElement, expected);
            return;
        }
        actual.Should().Be(expected, "case {0} {1} must preserve its public value", caseId, path);
    }

    private static bool IsMatcher(string? value) => value is not null
        && (value.StartsWith('<') && value.EndsWith('>') || value.StartsWith('$'));

    private static string DefaultHost(string area, JsonElement testCase)
    {
        if (area == "mode" && string.Equals(
                TryGetString(testCase, "mode", "authMode"),
                "simple",
                StringComparison.OrdinalIgnoreCase))
            return "simple";

        return area switch
        {
            "auth" => "auth",
            "access-management" or "revocation" => "access",
            "organization-admin" or "jobs" => "organization",
            "mode" => "auth",
            _ => throw new JsonException($"Unsupported parity area '{area}'."),
        };
    }

    private static string CanonicalArea(string value) => Normalize(value) switch
    {
        "auth" or "authentication" => "auth",
        "access" or "accessmanagement" => "access-management",
        "organization" or "organizationadmin" or "organizationadministration" => "organization-admin",
        "job" or "jobs" => "jobs",
        "revoke" or "revocation" => "revocation",
        "mode" or "modes" or "authmode" or "simplemode" or "simplemodeisolation" => "mode",
        _ => throw new JsonException($"Unsupported parity area '{value}'."),
    };

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        AllowAutoRedirect = false,
    };

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                    && Directory.Exists(Path.Combine(directory.FullName, "dotnet"))
                    && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the shared parity fixture.");
    }

    private static JsonElement GetRequiredProperty(JsonElement value, params string[] names)
    {
        if (TryGetProperty(value, out var property, names))
            return property;
        throw new JsonException($"Missing required property: {string.Join(" or ", names)}.");
    }

    private static string GetRequiredString(JsonElement value, params string[] names) =>
        TryGetString(value, names)
        ?? throw new JsonException($"Missing required string property: {string.Join(" or ", names)}.");

    private static string? TryGetString(JsonElement value, params string[] names)
    {
        if (!TryGetProperty(value, out var property, names)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new JsonException($"Property {string.Join(" or ", names)} must be a string.");
        return property.GetString();
    }

    private static int GetRequiredInt32(JsonElement value, params string[] names)
    {
        var property = GetRequiredProperty(value, names);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result))
            throw new JsonException($"Property {string.Join(" or ", names)} must be an integer.");
        return result;
    }

    private static bool TryGetProperty(
        JsonElement value,
        out JsonElement property,
        params string[] names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (value.TryGetProperty(name, out property))
                    return true;
            }
        }

        property = default;
        return false;
    }

    private static string Normalize(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}