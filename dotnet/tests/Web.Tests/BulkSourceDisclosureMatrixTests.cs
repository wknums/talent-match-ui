using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Web.Tests;

public sealed class BulkSourceDisclosureMatrixTests
{
    public static TheoryData<string, string> ProhibitedAuditFacts => new()
    {
        { "folderUrl", "https://contoso.sharepoint.com/sites/hr/Candidates" },
        { "folderName", "Executive Candidates" },
        { "itemName", "person.pdf" },
        { "sourceAccountEmail", "recruiter@contoso.example" },
        { "authorizationCode", "code-value" },
        { "accessToken", "token-value" },
        { "pkceVerifier", "verifier-value" },
        { "tokenCache", "cache-value" },
        { "wrappedKey", "key-value" },
        { "fingerprint", new string('a', 64) },
        { "contentBase64", "JVBERi0xLjQ=" },
        { "providerPayload", """{"error":"raw-provider-error"}""" },
        { "environmentResourceId", "/subscriptions/sub/resourceGroups/rg" },
    };

    [Theory]
    [MemberData(nameof(ProhibitedAuditFacts))]
    public void Audit_safe_facts_reject_every_prohibited_disclosure_category(
        string property,
        string value)
    {
        var audit = SafeAudit(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [property] = value,
        }));

        var act = () => audit.Validate();

        act.Should().Throw<InvalidOperationException>(
            $"the immutable audit ledger must reject prohibited {property} payloads");
    }

    [Fact]
    public async Task Bulk_problem_response_contains_only_Rfc9457_safe_fields()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/bulk-scoring-requests/{Guid.NewGuid()}")
        {
            Headers = { { "X-Correlation-ID", correlationId } },
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.EnumerateObject().Select(property => property.Name).Should().BeSubsetOf(
            ["type", "title", "status", "detail", "code", "correlationId", "adminConsentUrl"]);
        payload.GetProperty("correlationId").GetString().Should().Be(correlationId);
    }

    private static BulkAuditEvent SafeAudit(string facts) => new()
    {
        EventType = BulkAuditEventTypes.BulkItemDiscovered,
        CorrelationId = Guid.NewGuid().ToString(),
        ActorType = BulkAuditActorType.Service,
        ActorId = "bulk-discovery-worker",
        InitiatedByActorId = "product-user-1",
        OccurredAt = DateTime.UtcNow,
        SubjectType = "bulk_item",
        SubjectId = Guid.NewGuid().ToString(),
        SafeFactsJson = facts,
        SchemaVersion = 1,
    };
}
