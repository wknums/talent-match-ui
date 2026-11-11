using System.Net;
using System.Text;
using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.SharePoint;

namespace TalentMatch.Infrastructure.Tests.SharePoint;

public sealed class GraphSharePointSourceTests
{
    private const string SourceTenant = "11111111-2222-3333-4444-555555555555";
    private static readonly SourceCallContext Interactive =
        new(SourcePhase.Interactive, "actor", ConnectionId: "connection-1");
    private static readonly SourceCallContext Background =
        new(SourcePhase.Background, "actor", RequestId: "request-1");
    private static readonly SourceFolder Root =
        new(BulkSourceKind.SharePoint, "drv", "root", "Candidates", "/Candidates", "https://x", "tenant-1");

    private sealed class Access : ISharePointAccessContext
    {
        public int TokenCalls { get; private set; }
        public string SourceTenantId => SourceTenant;

        public Task<string> GetGraphTokenAsync(SourceCallContext context, CancellationToken cancellationToken = default)
        {
            TokenCalls++;
            return Task.FromResult(
                "eyJhbGciOiJub25lIn0.eyJ0aWQiOiIxMTExMTExMS0yMjIyLTMzMzMtNDQ0NC01NTU1NTU1NTU1NTUifQ.");
        }
    }

    private sealed class Script(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _active;
        public int MaxActive { get; private set; }
        public List<(Uri Uri, string? Auth)> Requests { get; } = [];

        public Script(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!,
                request.Headers.Authorization is null ? null : "Bearer delegated-token"));
            var active = Interlocked.Increment(ref _active);
            MaxActive = Math.Max(MaxActive, active);
            try { return await respond(request, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GraphSharePointSource Source, Script Script, Access Access) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        int maxConcurrency = 4)
    {
        var script = new Script(respond);
        return Create(script, maxConcurrency);
    }

    private static (GraphSharePointSource Source, Script Script, Access Access) Create(
        Script script,
        int maxConcurrency = 4)
    {
        var access = new Access();
        var options = new BulkScoringOptions { SourceMaxConcurrency = maxConcurrency };
        return (new GraphSharePointSource(new HttpClient(script), access, options), script, access);
    }

    [Theory]
    [InlineData("documentLibrary", BulkSourceKind.SharePoint)]
    [InlineData("business", BulkSourceKind.OneDriveBusiness)]
    public async Task Delegated_shares_resolution_records_the_exact_organizational_folder(
        string driveType,
        BulkSourceKind expected)
    {
        var (source, script, access) = Create(_ => Json("""
            {"id":"item-1","name":"Candidates","webUrl":"https://tenant.example/folder","folder":{},
             "parentReference":{"driveId":"drv-9","driveType":"DRIVETYPE","path":"/drives/drv-9/root:/Docs"}}
            """.Replace("DRIVETYPE", driveType)));

        var folder = await source.ResolveFolderAsync(
            "https://contoso.sharepoint.com/sites/hr/Docs/Candidates",
            Interactive);

        folder.Should().Be(new SourceFolder(
            expected, "drv-9", "item-1", "Candidates", "/Docs/Candidates",
            "https://tenant.example/folder", SourceTenant));
        access.TokenCalls.Should().Be(1);
        script.Requests.Should().ContainSingle();
        script.Requests[0].Auth.Should().Be("Bearer delegated-token");
        script.Requests[0].Uri.Host.Should().Be("graph.microsoft.com");
        script.Requests[0].Uri.AbsolutePath.Should().StartWith(
            "/v1.0/shares/u!aHR0cHM6Ly9jb250b3NvLnNoYXJlcG9pbnQuY29t");
        script.Requests[0].Uri.AbsolutePath.Should().NotContain("=").And.NotContain("contoso");
    }

    [Fact]
    public async Task Resolution_requires_a_connection_binding_before_token_or_network_work()
    {
        var (source, script, access) = Create(_ => Json("{}"));
        var unbound = new SourceCallContext(SourcePhase.Interactive, "actor");

        await source.Invoking(s => s.ResolveFolderAsync("https://contoso.sharepoint.com/x", unbound))
            .Should().ThrowAsync<AuthorizationRequiredException>();

        access.TokenCalls.Should().Be(0);
        script.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Personal_drives_and_files_are_rejected_without_application_only_fallback()
    {
        var (personal, _, personalAccess) = Create(_ => Json(
            """{"id":"i","name":"n","folder":{},"parentReference":{"driveId":"d","driveType":"personal"}}"""));
        await personal.Invoking(s => s.ResolveFolderAsync("https://x", Interactive))
            .Should().ThrowAsync<UnsupportedSourceException>();
        personalAccess.TokenCalls.Should().Be(1);

        var (file, _, fileAccess) = Create(_ => Json(
            """{"id":"i","name":"cv.pdf","file":{},"parentReference":{"driveId":"d","driveType":"business"}}"""));
        await file.Invoking(s => s.ResolveFolderAsync("https://x", Interactive))
            .Should().ThrowAsync<UnsupportedSourceException>();
        fileAccess.TokenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Enumeration_accepts_only_children_with_stable_exact_folder_lineage()
    {
        var (source, script, _) = Create(r =>
        {
            var url = r.RequestUri!.ToString();
            if (url.Contains("items/root/children") && !url.Contains("skip"))
                return Json("""
                    {"value":[
                      {"id":"a","name":"a.pdf","size":10,"eTag":"e1","file":{},"parentReference":{"driveId":"drv","id":"root"}},
                      {"id":"sub","name":"Archive","folder":{},"parentReference":{"driveId":"drv","id":"root"}},
                      {"id":"outside","name":"outside.pdf","size":99,"file":{},"parentReference":{"driveId":"other","id":"root"}}
                    ],"@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/drv/items/root/children?$skiptoken=abc"}
                    """);
            if (url.Contains("skiptoken"))
                return Json("""{"value":[]}""");
            return Json("""
                {"value":[
                  {"id":"b","name":"b.pdf","size":20,"eTag":"e2","file":{},"parentReference":{"driveId":"drv","id":"sub"}}
                ]}
                """);
        });

        var first = await source.ReadNextPageAsync(Root, null, Interactive);
        var second = await source.ReadNextPageAsync(Root, first.NextCursor, Interactive);
        var third = await source.ReadNextPageAsync(Root, second.NextCursor, Interactive);

        first.Files.Select(x => x.Name).Should().Equal("a.pdf");
        first.InaccessibleCount.Should().Be(1, "an entry with unverifiable lineage must not disclose its name");
        second.Files.Should().BeEmpty();
        third.Files.Select(x => (x.Name, x.Path)).Should().Equal(("b.pdf", "/Archive"));
        script.Requests.Should().OnlyContain(x =>
            x.Uri.Host == "graph.microsoft.com" && x.Auth == "Bearer delegated-token");
    }

    [Fact]
    public async Task Next_links_must_remain_on_the_same_Graph_children_lineage()
    {
        foreach (var next in new[]
                 {
                     "https://evil.example/v1.0/drives/drv/items/root/children?$skiptoken=x",
                     "https://graph.microsoft.com/v1.0/drives/other/items/root/children?$skiptoken=x",
                     "https://graph.microsoft.com/v1.0/drives/drv/items/outside/children?$skiptoken=x",
                 })
        {
            var (source, script, _) = Create(_ => Json($$"""{"value":[],"@odata.nextLink":"{{next}}"}"""));
            var first = await source.ReadNextPageAsync(Root, null, Interactive);

            await source.Invoking(s => s.ReadNextPageAsync(Root, first.NextCursor, Interactive))
                .Should().ThrowAsync<SourceTransientException>();

            script.Requests.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Inaccessible_descendants_are_counted_without_returning_their_names()
    {
        var (source, _, _) = Create(r => r.RequestUri!.AbsolutePath.Contains("items/root/children")
            ? Json("""
                {"value":[
                  {"id":"locked","name":"Highly Confidential","folder":{},"parentReference":{"driveId":"drv","id":"root"}},
                  {"id":"ok","name":"ok.pdf","size":1,"eTag":"e","file":{},"parentReference":{"driveId":"drv","id":"root"}}
                ]}
                """)
            : Json("{}", HttpStatusCode.Forbidden));

        var first = await source.ReadNextPageAsync(Root, null, Interactive);
        var second = await source.ReadNextPageAsync(Root, first.NextCursor, Interactive);

        first.Files.Select(x => x.Name).Should().Equal("ok.pdf");
        second.Should().Match<SourcePage>(x =>
            x.InaccessibleCount == 1 && x.Files.Count == 0 && x.Exhausted);
        second.ToString().Should().NotContain("Highly Confidential");
    }

    [Fact]
    public async Task Metadata_and_download_are_rejected_until_the_item_lineage_reaches_the_exact_root()
    {
        var contentCalls = 0;
        var (source, _, _) = Create(r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/content"))
            {
                contentCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent("secret"u8.ToArray()) };
            }

            return r.RequestUri.AbsolutePath.Contains("items/outside")
                ? Json("""{"id":"outside","size":6,"eTag":"e","parentReference":{"driveId":"drv","id":"other-root"}}""")
                : Json("""{"id":"other-root","parentReference":{}}""");
        });

        await source.Invoking(s => s.GetItemMetadataAsync(Root, "outside", Background))
            .Should().ThrowAsync<SourceAccessDeniedException>();
        await source.Invoking(s => s.DownloadAsync(Root, "outside", 100, Background))
            .Should().ThrowAsync<SourceAccessDeniedException>();

        contentCalls.Should().Be(0);
    }

    [Fact]
    public async Task Download_checks_lineage_then_manually_follows_the_Graph_redirect_without_a_token()
    {
        var (source, script, _) = Create(r => r.RequestUri!.Host switch
        {
            "graph.microsoft.com" when !r.RequestUri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                => Json("""{"id":"item","size":10,"eTag":"e","parentReference":{"driveId":"drv","id":"root"}}"""),
            "graph.microsoft.com" when r.RequestUri.AbsolutePath.EndsWith("/content")
                => new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://contoso.sharepoint.com/download?tempauth=x") },
                },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent("%PDF-hello"u8.ToArray()) },
        });

        var bytes = await source.DownloadAsync(Root, "item", 1_000, Background);

        bytes.Should().Equal("%PDF-hello"u8.ToArray());
        script.Requests.Should().HaveCount(3);
        script.Requests.Take(2).Should().OnlyContain(x =>
            x.Uri.Host == "graph.microsoft.com" && x.Auth == "Bearer delegated-token");
        script.Requests[2].Auth.Should().BeNull(
            "the pre-authenticated address must never receive the delegated token");
    }

    [Fact]
    public async Task Unbound_legacy_download_is_not_an_application_only_fallback()
    {
        var (source, script, access) = Create(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });

#pragma warning disable CS0618
        await source.Invoking(s => s.DownloadAsync("drv", "item", 100, Background))
            .Should().ThrowAsync<SourceAccessDeniedException>();
#pragma warning restore CS0618

        access.TokenCalls.Should().Be(0);
        script.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_source_concurrency_gate_is_bounded_and_released_safely()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var script = new Script(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) entered.SetResult();
            await release.Task.WaitAsync(ct);
            return Json("""{"value":[]}""");
        });
        var (source, _, _) = Create(script, maxConcurrency: 1);

        var first = source.ReadNextPageAsync(Root, null, Interactive);
        await entered.Task;
        var second = source.ReadNextPageAsync(Root, null, Interactive);
        await Task.Delay(25);

        script.MaxActive.Should().Be(1);
        calls.Should().Be(1);
        release.SetResult();
        await Task.WhenAll(first, second);
        script.MaxActive.Should().Be(1);
    }

    [Fact]
    public async Task Throttling_is_transient_and_preserves_a_safe_retry_after()
    {
        var (source, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Headers = { RetryAfter = new(TimeSpan.FromSeconds(42)) } });

        var ex = (await source.Invoking(s => s.ReadNextPageAsync(Root, null, Background))
            .Should().ThrowAsync<SourceTransientException>()).Which;

        ex.RetryAfter.Should().Be(TimeSpan.FromSeconds(42));
        ex.Message.Should().NotContain("Candidates").And.NotContain("https://x");
    }

    [Fact]
    public async Task Background_unauthorized_is_a_delegated_authorization_lapse_without_model_fallback()
    {
        var (source, _, access) = Create(_ => Json("{}", HttpStatusCode.Unauthorized));

        await source.Invoking(s => s.ReadNextPageAsync(Root, null, Background))
            .Should().ThrowAsync<SourceAuthorizationLapsedException>();
        await source.Invoking(s => s.ReadNextPageAsync(Root, null, Interactive))
            .Should().ThrowAsync<SourceAccessDeniedException>();

        access.TokenCalls.Should().Be(2);
    }
}
