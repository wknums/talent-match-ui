using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Infrastructure;

namespace TalentMatch.Infrastructure.Tests;

public sealed class GraphAdministrationEvidenceTests
{
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Group = "22222222-2222-4222-8222-222222222222";
    private const string Actor = "33333333-3333-4333-8333-333333333333";
    private const string Resource = "55555555-5555-4555-8555-555555555555";
    private const string Role = "66666666-6666-4666-8666-666666666666";
    private static Dictionary<string, object?> User(string type = "#microsoft.graph.user") =>
        new() { ["id"] = Actor, ["@odata.type"] = type };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T08:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) =>
            new("synthetic-not-reusable", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(context, cancellationToken));
    }
    private sealed class Handler(Func<Uri, object, object>? alter, int status = 200) : HttpMessageHandler
    {
        public List<Uri> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Calls.Add(uri);
            object body = uri.AbsolutePath.EndsWith($"/groups/{Group}")
                ? new { id = Group, securityEnabled = true, groupTypes = Array.Empty<string>() }
                : uri.AbsolutePath.Contains("/users/")
                    ? new { id = uri.Segments.Last(), accountEnabled = true, userType = "Member" }
                    : uri.AbsolutePath.EndsWith("/appRoleAssignedTo")
                        ? new { value = new[] { new { id = "assignment", principalId = Actor, principalType = "User", resourceId = Resource, appRoleId = Role } } }
                        : uri.AbsolutePath.EndsWith($"/servicePrincipals/{Resource}")
                            ? new { id = Resource, accountEnabled = true, appRoles = new[] { new { id = Role, value = "admin", isEnabled = true, allowedMemberTypes = new[] { "User" } } } }
                            : new { value = new[] { User() } };
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(JsonSerializer.Serialize(alter?.Invoke(uri, body) ?? body), Encoding.UTF8, "application/json") });
        }
    }
    private static IDirectoryAdministrationEvidenceProvider Create(Handler handler, Clock clock)
    {
        // Assertion-based intended red before implementation, not a missing-import/compiler failure.
        var type = typeof(DependencyInjection).Assembly.GetType("TalentMatch.Infrastructure.Services.GraphAdministrationEvidenceProvider");
        Assert.NotNull(type);
        return (IDirectoryAdministrationEvidenceProvider)Activator.CreateInstance(type,
            new HttpClient(handler), new Credential(), clock, Tenant, Resource, Role)!;
    }
    [Fact]
    public async Task Paging_time_is_charged_to_first_collection_not_the_last_page()
    {
        var clock = new Clock();
        var start = clock.Now;
        var handler = new Handler((uri, body) =>
        {
            clock.Now += TimeSpan.FromSeconds(10);
            if ((uri.AbsolutePath.EndsWith("/members") || uri.AbsolutePath.EndsWith("/transitiveMembers"))
                && !uri.Query.Contains("page=2"))
                return new Dictionary<string, object> { ["value"] = Array.Empty<object>(),
                    ["@odata.nextLink"] = $"https://graph.microsoft.com{uri.AbsolutePath}?page=2" };
            return body;
        });
        var evidence = await Create(handler, clock).GetGroupEvidenceAsync(new(Tenant, Group, 1, Actor));
        Assert.Equal(start.UtcDateTime, evidence.CollectionStartedAt);
        Assert.Equal(start.AddSeconds(270).UtcDateTime, evidence.ExpiresAt);
        Assert.True(evidence.CollectedAt > evidence.CollectionStartedAt);
        Assert.True(evidence.ExpiresAt - evidence.CollectedAt < TimeSpan.FromSeconds(270));
    }

    [Fact]
    public async Task CompleteGroupUsesFixedEndpointsAndExplicitSelectionWithNonSlidingExpiry()
    {
        var handler = new Handler(null);
        var clock = new Clock();
        var provider = Create(handler, clock);
        var request = new DirectoryAdministrationEvidenceRequest(Tenant, Group, 1, Actor);
        var evidence = await provider.GetGroupEvidenceAsync(request);
        Assert.True(evidence.Complete && evidence.CompositionValidated);
        Assert.Contains(Actor, evidence.EligibleUserObjectIds);
        Assert.InRange((evidence.ExpiresAt - evidence.CollectionStartedAt).TotalSeconds, 1, 300);
        Assert.All(handler.Calls, uri => Assert.Equal("graph.microsoft.com", uri.Host));
        Assert.All(handler.Calls.Where(uri => uri.AbsolutePath.Contains("/users/")), uri =>
            Assert.Contains("$select=id,accountEnabled,userType,userPrincipalName,displayName,mail", Uri.UnescapeDataString(uri.Query)));
        var count = handler.Calls.Count;
        clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(evidence.ExpiresAt, (await provider.GetGroupEvidenceAsync(request)).ExpiresAt);
        Assert.Equal(count, handler.Calls.Count);
        await provider.GetGroupEvidenceAsync(request with { AuthorityVersion = 2 });
        Assert.True(handler.Calls.Count > count);
    }
    [Theory]
    [InlineData("#microsoft.graph.group")]
    [InlineData("#microsoft.graph.device")]
    [InlineData("#microsoft.graph.servicePrincipal")]
    [InlineData("")]
    public async Task ProhibitedDirectOrTransitiveTypeNeverGrants(string type)
    {
        foreach (var collection in new[] { "/members", "/transitiveMembers" })
        {
            var handler = new Handler((uri, body) => uri.AbsolutePath.EndsWith(collection)
                ? new { value = new[] { User(), User(type) } } : body);
            await Assert.ThrowsAnyAsync<Exception>(() => Create(handler, new Clock())
                .GetGroupEvidenceAsync(new(Tenant, Group, 1, Actor)));
        }
    }
    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task UnavailableEvidenceFailsClosed(int status) =>
        await Assert.ThrowsAnyAsync<Exception>(() => Create(new(null, status), new Clock())
            .GetGroupEvidenceAsync(new(Tenant, Group, 1, Actor)));
    [Fact]
    public async Task ForeignPagingIsNeverFetched()
    {
        var handler = new Handler((uri, body) => uri.AbsolutePath.EndsWith("/members")
            ? new Dictionary<string, object> { ["value"] = new[] { User() }, ["@odata.nextLink"] = "https://untrusted.invalid/collect" } : body);
        await Assert.ThrowsAnyAsync<Exception>(() => Create(handler, new Clock()).GetGroupEvidenceAsync(new(Tenant, Group, 1, Actor)));
        Assert.DoesNotContain(handler.Calls, uri => uri.Host == "untrusted.invalid");
    }
    [Fact]
    public async Task ExactIndividualAssignmentAndEnabledRoleAreRequired()
    {
        var handler = new Handler(null);
        var evidence = await Create(handler, new Clock()).GetIndividualEvidenceAsync(new(Tenant, Actor, 1));
        Assert.True(evidence.DirectAdminRoleBinding);
        Assert.Contains(handler.Calls, uri => uri.AbsolutePath.EndsWith("/appRoleAssignedTo"));
        Assert.DoesNotContain(handler.Calls, uri => uri.AbsolutePath.Contains("/users/") && uri.AbsolutePath.EndsWith("/appRoleAssignments"));
        var wrong = new Handler((uri, body) => uri.AbsolutePath.EndsWith("/appRoleAssignedTo")
            ? new { value = new[] { new { principalId = Actor, principalType = "Group", resourceId = Resource, appRoleId = Role } } } : body);
        await Assert.ThrowsAnyAsync<Exception>(() => Create(wrong, new Clock()).GetIndividualEvidenceAsync(new(Tenant, Actor, 1)));
    }

    [Theory]
    [InlineData(false, "Member", "admin_group_invalid")]
    [InlineData(true, "Guest", "admin_group_invalid")]
    [InlineData(null, "Member", "group_evidence_unavailable")]
    [InlineData(true, null, "group_evidence_unavailable")]
    public async Task ExplicitUserEligibilityCannotBeGuessed(bool? enabled, string? type, string code)
    {
        var handler = new Handler((uri, body) => uri.AbsolutePath.Contains("/users/")
            ? new { id = Actor, accountEnabled = enabled, userType = type } : body);
        var ex = await Assert.ThrowsAsync<DirectoryAdministrationEvidenceException>(() =>
            Create(handler, new Clock()).GetGroupEvidenceAsync(new(Tenant, Group, 1, Actor)));
        Assert.Equal(code, ex.Code);
    }
    [Fact]
    public async Task EveryAssignmentPageIsReadEvenAfterPositiveMatch()
    {
        var handler = new Handler((uri, body) => uri.AbsolutePath.EndsWith("/appRoleAssignedTo")
            ? uri.Query.Contains("skiptoken") ? new { value = Array.Empty<object>() } : new Dictionary<string, object> {
                ["value"] = new[] { new { id = "assignment", principalId = Actor, principalType = "User", resourceId = Resource, appRoleId = Role } },
                ["@odata.nextLink"] = $"{RootAssignment()}?$skiptoken=late",
            } : body);
        Assert.True((await Create(handler, new Clock()).GetIndividualEvidenceAsync(new(Tenant, Actor, 1))).DirectAdminRoleBinding);
        Assert.Contains(handler.Calls, uri => uri.Query.Contains("skiptoken"));
    }
    private static string RootAssignment() => $"https://graph.microsoft.com/v1.0/servicePrincipals/{Resource}/appRoleAssignedTo";
    [Fact]
    public async Task RestartExpiryAndFailedRefreshNeverExtendCachedProof()
    {
        var clock = new Clock();
        var healthy = new Handler(null);
        var provider = Create(healthy, clock);
        var request = new DirectoryAdministrationEvidenceRequest(Tenant, Group, 1, Actor);
        var first = await provider.GetGroupEvidenceAsync(request);
        clock.Now += TimeSpan.FromMinutes(6);
        var refreshed = await provider.GetGroupEvidenceAsync(request);
        Assert.True(refreshed.CollectionStartedAt > first.CollectionStartedAt);
        var failed = Create(new Handler(null, 429), clock);
        await Assert.ThrowsAsync<DirectoryAdministrationEvidenceException>(() => failed.GetGroupEvidenceAsync(request));
        await Assert.ThrowsAsync<DirectoryAdministrationEvidenceException>(() => failed.GetIndividualEvidenceAsync(new(Tenant, Actor, 1)));
    }
}
