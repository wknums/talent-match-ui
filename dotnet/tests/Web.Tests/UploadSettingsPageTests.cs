using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class UploadSettingsPageTests : BunitContext
{
    private const long OneMb = 1_048_576;

    [Fact]
    public void Page_ShowsTheLimitsInMebibytesWithTheDocumentedDefaultsAndTheBulkMaximum()
    {
        var handler = new SettingsHandler();
        var cut = Open(handler);

        cut.WaitForAssertion(() =>
        {
            cut.Find("h3#upload-settings-title").TextContent.Should().Be("Optional upload settings");
            cut.FindAll("h1").Should().BeEmpty();
            cut.Find("label[for=individual-mb]").TextContent.Should().Be("Maximum individual file size (MiB)");
            cut.Find("label[for=inflight-mb]").TextContent.Should().Be("Maximum in-flight size (MiB)");
            cut.Find("label[for=bulk-max-files]").TextContent.Should().Be("Maximum files per bulk scoring request");
            cut.FindAll(".upload-settings-field").Should().HaveCount(6);
            cut.Find("#individual-mb").GetAttribute("value").Should().Be("8");
            cut.Find("#inflight-mb").GetAttribute("value").Should().Be("100");
            cut.Find("#bulk-max-files").GetAttribute("value").Should().Be("30000");
            cut.Find("#throughput-refresh-seconds").GetAttribute("value").Should().Be("60");
            cut.Find("#status-polling-seconds").GetAttribute("value").Should().Be("60");
            cut.Markup.Should().Contain("Default 30,000");
            cut.Markup.ToLowerInvariant().Should().NotContain("runner");
            cut.Markup.ToLowerInvariant().Should().NotContain("scoring concurrency");
            cut.Markup.Should().NotContain("bytes");
        });
    }

    [Fact]
    public void A_saved_value_that_is_not_a_whole_number_of_megabytes_displays_exactly()
    {
        var cut = Open(new SettingsHandler { Upload = Upload(individual: 5_000_000, inFlight: 100_000_000) });

        cut.WaitForAssertion(() =>
        {
            cut.Find("#individual-mb").GetAttribute("value").Should().Be("4.76837158203125");
            cut.Find("#inflight-mb").GetAttribute("value").Should().Be("95.367431640625");
        });
    }

    [Fact]
    public void Saving_without_touching_the_sizes_sends_the_exact_loaded_bytes()
    {
        var handler = new SettingsHandler { Upload = Upload(individual: 5_000_000, inFlight: 100_000_000, version: 7) };
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#file-concurrency").Should().NotBeNull());

        cut.Find("#file-concurrency").Change(3);
        cut.Find("#throughput-refresh-seconds").Change(90);
        cut.Find("#status-polling-seconds").Change(120);
        cut.FindAll("form")[0].Submit();

        cut.WaitForAssertion(() => handler.UploadPut.Should().NotBeNull());
        handler.UploadPut!.Value.GetProperty("fileConcurrency").GetInt32().Should().Be(3);
        handler.UploadPut.Value.GetProperty("maxIndividualFileBytes").GetInt64().Should().Be(5_000_000);
        handler.UploadPut.Value.GetProperty("maxInFlightBytes").GetInt64().Should().Be(100_000_000);
        handler.UploadPut.Value.GetProperty("expectedConcurrencyVersion").GetInt32().Should().Be(7);
        handler.UploadPut.Value.GetProperty("throughputRefreshIntervalSeconds").GetInt32().Should().Be(90);
        handler.UploadPut.Value.GetProperty("statusPollingIntervalSeconds").GetInt32().Should().Be(120);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Upload settings saved."));
    }

    [Fact]
    public void Changing_a_size_in_MiB_sends_whole_bytes_at_1048576_per_MiB()
    {
        var handler = new SettingsHandler();
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#individual-mb").Should().NotBeNull());

        cut.Find("#individual-mb").Change(10);
        cut.FindAll("form")[0].Submit();

        cut.WaitForAssertion(() => handler.UploadPut.Should().NotBeNull());
        handler.UploadPut!.Value.GetProperty("maxIndividualFileBytes").GetInt64().Should().Be(10 * OneMb);
        handler.UploadPut.Value.GetProperty("maxInFlightBytes").GetInt64().Should().Be(100 * OneMb, "an unchanged value keeps its exact bytes");
    }

    [Fact]
    public void A_fractional_size_is_converted_to_whole_bytes()
    {
        var handler = new SettingsHandler();
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#individual-mb").Should().NotBeNull());

        cut.Find("#individual-mb").Change("2.5"); // a browser number input always reports an invariant "2.5"
        cut.FindAll("form")[0].Submit();

        cut.WaitForAssertion(() => handler.UploadPut.Should().NotBeNull());
        handler.UploadPut!.Value.GetProperty("maxIndividualFileBytes").GetInt64().Should().Be(2_621_440);
    }

    [Fact]
    public void A_MiB_value_that_would_create_a_fractional_byte_is_rejected()
    {
        var handler = new SettingsHandler();
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#individual-mb").Should().NotBeNull());

        cut.Find("#individual-mb").Change("1.0000001");
        cut.FindAll("form")[0].Submit();

        cut.WaitForAssertion(() => cut.Find("p[role=alert]").TextContent.Should().Contain("whole bytes"));
        handler.UploadPut.Should().BeNull();
    }

    [Fact]
    public void Validation_messages_speak_in_megabytes()
    {
        var handler = new SettingsHandler();
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#individual-mb").Should().NotBeNull());

        cut.Find("#inflight-mb").Change(4);
        cut.FindAll("form")[0].Submit();

        cut.WaitForAssertion(() => cut.Find("p[role=alert]").TextContent.Should()
            .Contain("maximum in-flight size (MiB)").And.Contain("maximum individual file size (MiB)"));
        cut.Find("p[role=alert]").TextContent.ToLowerInvariant().Should().NotContain("bytes");
        handler.UploadPut.Should().BeNull("nothing is saved");

        cut.Find("#individual-mb").Change(0);
        cut.FindAll("form")[0].Submit();
        cut.WaitForAssertion(() => cut.Find("p[role=alert]").TextContent.Should().Contain("positive").And.Contain("(MiB)"));
    }

    [Fact]
    public void A_stale_version_is_reported_clearly()
    {
        var handler = new SettingsHandler
        {
            UploadPutResponse = () => Error(HttpStatusCode.Conflict, "stale_version", "These settings were changed by another administrator. Reload the page and try again."),
        };
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#file-concurrency").Should().NotBeNull());

        cut.Find("#file-concurrency").Change(2);
        cut.FindAll("form")[0].Submit();

        cut.WaitForAssertion(() => cut.Find("p[role=alert]").TextContent.Should().Contain("changed by another administrator"));
    }

    [Fact]
    public void The_bulk_maximum_saves_with_the_loaded_version_and_reports_the_outcome()
    {
        var handler = new SettingsHandler { Bulk = new BulkSettingsDto(30_000, 3, null, null) };
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#bulk-max-files").Should().NotBeNull());

        cut.Find("#bulk-max-files").Change(1500);
        cut.FindAll("form")[1].Submit();

        cut.WaitForAssertion(() => handler.BulkPut.Should().NotBeNull());
        handler.BulkPut!.Value.GetProperty("maxFilesPerRequest").GetInt32().Should().Be(1500);
        handler.BulkPut.Value.GetProperty("expectedConcurrencyVersion").GetInt32().Should().Be(3);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Bulk scoring settings saved."));
        cut.Find("#bulk-max-files").GetAttribute("value").Should().Be("1500");
    }

    [Fact]
    public void A_bulk_stale_version_and_a_non_administrator_are_reported_without_disturbing_the_upload_settings()
    {
        var handler = new SettingsHandler
        {
            BulkPutResponse = () => Error(HttpStatusCode.Forbidden, "forbidden", "Access to this resource is not permitted."),
        };
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#bulk-max-files").Should().NotBeNull());

        cut.Find("#bulk-max-files").Change(10);
        cut.FindAll("form")[1].Submit();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Access to this resource is not permitted."));
        cut.Find("#individual-mb").GetAttribute("value").Should().Be("8", "the upload settings are untouched");

        handler.BulkPutResponse = () => Error(HttpStatusCode.Conflict, "settings_stale", "The bulk scoring settings were changed by another administrator.");
        cut.FindAll("form")[1].Submit();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("changed by another administrator"));
    }

    [Fact]
    public void A_non_positive_bulk_maximum_is_refused_before_anything_is_sent()
    {
        var handler = new SettingsHandler();
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#bulk-max-files").Should().NotBeNull());

        cut.Find("#bulk-max-files").Change(0);
        cut.FindAll("form")[1].Submit();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1 through 100,000"));
        handler.BulkPut.Should().BeNull();
    }

    [Fact]
    public void A_bulk_maximum_above_100000_is_refused_before_anything_is_sent()
    {
        var handler = new SettingsHandler();
        var cut = Open(handler);
        cut.WaitForAssertion(() => cut.Find("#bulk-max-files").Should().NotBeNull());

        cut.Find("#bulk-max-files").Change(100_001);
        cut.FindAll("form")[1].Submit();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1 through 100,000"));
        handler.BulkPut.Should().BeNull();
    }

    private IRenderedComponent<UploadSettings> Open(SettingsHandler handler)
    {
        Services.AddSingleton(new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return Render<UploadSettings>();
    }

    private static UploadSettingsDto Upload(long individual = 8 * OneMb, long inFlight = 100 * OneMb, int version = 0) =>
        new(4, individual, inFlight, version, false, null, null);

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        new(status) { Content = JsonContent.Create(new { code, message, correlationId = "corr-1" }) };

    private sealed class SettingsHandler : HttpMessageHandler
    {
        public UploadSettingsDto Upload { get; init; } = new(4, 8 * OneMb, 100 * OneMb, 0, false, null, null);
        public BulkSettingsDto Bulk { get; init; } = new(30_000, 0, null, null);
        public Func<HttpResponseMessage>? UploadPutResponse { get; set; }
        public Func<HttpResponseMessage>? BulkPutResponse { get; set; }
        public JsonElement? UploadPut { get; private set; }
        public JsonElement? BulkPut { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/admin/upload-settings")
            {
                if (request.Method == HttpMethod.Put)
                {
                    UploadPut = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                    return UploadPutResponse?.Invoke() ?? Ok(Upload with
                    {
                        FileConcurrency = UploadPut.Value.GetProperty("fileConcurrency").GetInt32(),
                        MaxIndividualFileBytes = UploadPut.Value.GetProperty("maxIndividualFileBytes").GetInt64(),
                        MaxInFlightBytes = UploadPut.Value.GetProperty("maxInFlightBytes").GetInt64(),
                        ThroughputRefreshIntervalSeconds = UploadPut.Value.GetProperty("throughputRefreshIntervalSeconds").GetInt32(),
                        StatusPollingIntervalSeconds = UploadPut.Value.GetProperty("statusPollingIntervalSeconds").GetInt32(),
                        ConcurrencyVersion = Upload.ConcurrencyVersion + 1,
                    });
                }
                return Ok(Upload);
            }

            if (path == "/api/bulk-scoring-settings")
            {
                if (request.Method == HttpMethod.Put)
                {
                    BulkPut = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                    return BulkPutResponse?.Invoke() ?? Ok(new BulkSettingsDto(
                        BulkPut.Value.GetProperty("maxFilesPerRequest").GetInt32(), Bulk.ConcurrencyVersion + 1, DateTime.UtcNow, "admin"));
                }
                return Ok(Bulk);
            }

            return new(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
