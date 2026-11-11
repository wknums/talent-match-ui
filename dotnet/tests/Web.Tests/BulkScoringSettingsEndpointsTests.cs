using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

// The administrator's "maximum files per bulk scoring request" setting.
public sealed class BulkScoringSettingsEndpointsTests
{
    private const long MiB = 1_048_576;
    private const string Large = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Large";

    private static BulkScoringRequestsEndpointsTests.BulkFactory Factory() => new("mock");

    private static Task<HttpResponseMessage> Save(HttpClient client, int maxFiles, int version) =>
        client.PutAsJsonAsync("/api/bulk-scoring-settings", new { maxFilesPerRequest = maxFiles, expectedConcurrencyVersion = version });

    private static async Task<JsonElement> Read(HttpClient client) =>
        await (await client.GetAsync("/api/bulk-scoring-settings")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task With_nothing_saved_the_maximum_is_30000()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");

        var settings = await Read(admin);

        settings.GetProperty("maxFilesPerRequest").GetInt32().Should().Be(30_000);
        settings.GetProperty("concurrencyVersion").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30000)]
    [InlineData(100001)]
    public async Task Only_a_value_from_1_through_100000_is_accepted(int value)
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");

        var response = await Save(admin, value, 0);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("validation_failed");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100000)]
    public async Task The_inclusive_file_limit_boundaries_are_accepted(int value)
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");

        var response = await Save(admin, value, 0);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read(admin)).GetProperty("maxFilesPerRequest").GetInt32().Should().Be(value);
    }

    [Fact]
    public async Task Unsaved_upload_byte_limits_are_exactly_8_and_100_MiB()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");

        var settings = await (await admin.GetAsync("/api/admin/upload-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();

        settings.GetProperty("persisted").GetBoolean().Should().BeFalse();
        settings.GetProperty("maxIndividualFileBytes").GetInt64().Should().Be(8 * MiB);
        settings.GetProperty("maxInFlightBytes").GetInt64().Should().Be(100 * MiB);
    }

    [Fact]
    public async Task Saved_upload_limits_remain_byte_authoritative()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");

        var response = await admin.PutAsJsonAsync("/api/admin/upload-settings", new
        {
            fileConcurrency = 3,
            maxIndividualFileBytes = 5_000_001,
            maxInFlightBytes = 100_000_001,
            expectedConcurrencyVersion = 0,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        saved.GetProperty("maxIndividualFileBytes").GetInt64().Should().Be(5_000_001);
        saved.GetProperty("maxInFlightBytes").GetInt64().Should().Be(100_000_001);
        var read = await (await admin.GetAsync("/api/admin/upload-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        read.GetProperty("maxIndividualFileBytes").GetInt64().Should().Be(5_000_001);
        read.GetProperty("maxInFlightBytes").GetInt64().Should().Be(100_000_001);
    }

    [Fact]
    public async Task A_fractional_value_is_not_a_whole_number()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");

        var response = await admin.PutAsJsonAsync("/api/bulk-scoring-settings", new { maxFilesPerRequest = 1.5, expectedConcurrencyVersion = 0 });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        (await Read(admin)).GetProperty("maxFilesPerRequest").GetInt32().Should().Be(30_000, "nothing was saved");
    }

    [Fact]
    public async Task A_stale_version_is_a_409_and_a_non_administrator_is_refused()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");
        using var recruiter = await factory.LoginAsync("recruiter", "test123");
        (await Save(admin, 1_000, 0)).StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await Save(admin, 2_000, 0);

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Save(recruiter, 5, 1)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.Found);
        (await recruiter.GetAsync("/api/bulk-scoring-settings")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    [Fact]
    public async Task Every_save_appends_a_safe_typed_audit_entry_with_the_admin_correlation_and_UTC_time()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");
        var firstCorrelation = Guid.NewGuid().ToString();
        var secondCorrelation = Guid.NewGuid().ToString();
        admin.DefaultRequestHeaders.Add("X-Correlation-ID", firstCorrelation);

        (await Save(admin, 1_500, 0)).StatusCode.Should().Be(HttpStatusCode.OK);
        admin.DefaultRequestHeaders.Remove("X-Correlation-ID");
        admin.DefaultRequestHeaders.Add("X-Correlation-ID", secondCorrelation);
        (await Save(admin, 800, 1)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var administratorId = db.Users.Single(user => user.Username == "admin").Id;
        var entries = db.ProcessingEvents
            .Where(e => e.EventType == "bulk_settings_changed")
            .OrderBy(e => e.Timestamp)
            .ToList();
        entries.Should().HaveCount(2);
        entries.Select(entry => entry.Id).Should().OnlyHaveUniqueItems("the ledger is append-only");
        entries.Select(entry => entry.Actor).Should().OnlyContain(actor => actor == administratorId);
        entries.Select(entry => entry.EntityType).Should().OnlyContain(type => type == "BulkImportSettings");
        entries.Select(entry => entry.EntityId).Should().OnlyContain(id => id == "1");
        entries[0].CorrelationId.Should().Be(firstCorrelation);
        entries[1].CorrelationId.Should().Be(secondCorrelation);
        entries.Should().OnlyContain(entry => entry.Timestamp.Kind == DateTimeKind.Utc);
        using var first = JsonDocument.Parse(entries[0].PayloadJson!);
        first.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["factVersion", "old", "new"]);
        first.RootElement.GetProperty("factVersion").GetInt32().Should().Be(1);
        first.RootElement.GetProperty("old").GetProperty("maxFilesPerRequest").GetInt32().Should().Be(30_000);
        first.RootElement.GetProperty("old").GetProperty("concurrencyVersion").GetInt32().Should().Be(0);
        first.RootElement.GetProperty("new").GetProperty("maxFilesPerRequest").GetInt32().Should().Be(1_500);
        first.RootElement.GetProperty("new").GetProperty("concurrencyVersion").GetInt32().Should().Be(1);
        using var second = JsonDocument.Parse(entries[1].PayloadJson!);
        second.RootElement.GetProperty("old").GetProperty("maxFilesPerRequest").GetInt32().Should().Be(1_500);
        second.RootElement.GetProperty("old").GetProperty("concurrencyVersion").GetInt32().Should().Be(1);
        second.RootElement.GetProperty("new").GetProperty("maxFilesPerRequest").GetInt32().Should().Be(800);
        second.RootElement.GetProperty("new").GetProperty("concurrencyVersion").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_last_valid_value_and_writes_no_audit_entry()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");
        (await Save(admin, 1_500, 0)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Save(admin, 0, 1)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Save(admin, 2_000, 0)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var settings = await Read(admin);
        settings.GetProperty("maxFilesPerRequest").GetInt32().Should().Be(1_500);
        settings.GetProperty("concurrencyVersion").GetInt32().Should().Be(1);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessingEvents
            .Count(e => e.EventType == "bulk_settings_changed").Should().Be(1);
    }

    [Fact]
    public async Task An_audit_insert_failure_rolls_back_the_settings_change_atomically()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER reject_bulk_settings_audit
                BEFORE INSERT ON ProcessingEvents
                WHEN NEW.Action IN ('bulk_settings_changed', 'bulk-scoring-settings.updated')
                BEGIN
                    SELECT RAISE(ABORT, 'audit rejected');
                END;
                """);
        }

        await FluentActions.Awaiting(() => Save(admin, 1_500, 0))
            .Should().ThrowAsync<Exception>();

        var settings = await Read(admin);
        settings.GetProperty("maxFilesPerRequest").GetInt32().Should().Be(30_000);
        settings.GetProperty("concurrencyVersion").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Settings_and_append_only_audit_use_the_same_provider_neutral_model()
    {
        using var sqlite = new AppDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        using var sqlServer = new AppDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=provider-parity;Trusted_Connection=True")
                .Options);

        foreach (var model in new[] { sqlite.Model, sqlServer.Model })
        {
            var settings = model.FindEntityType(typeof(TalentMatch.Domain.Entities.BulkScoringRequests.BulkImportSettings));
            settings.Should().NotBeNull();
            settings!.GetProperties().Select(property => property.Name).Should().Contain(
                ["Id", "MaxFilesPerRequest", "ConcurrencyVersion", "UpdatedAt", "UpdatedByActorId"]);
            var audit = model.FindEntityType(typeof(TalentMatch.Domain.Entities.ProcessingEvent));
            audit.Should().NotBeNull();
            audit!.GetProperties().Select(property => property.Name).Should().Contain(
                ["Id", "Actor", "EventType", "CorrelationId", "Timestamp", "PayloadJson"]);
        }
    }

    [Fact]
    public async Task A_new_request_uses_the_saved_value_but_a_running_one_keeps_the_value_it_started_with()
    {
        using var factory = Factory();
        using var admin = await factory.LoginAsync("admin", "adm1n99");
        using var recruiter = await factory.LoginAsync("recruiter", "test123");
        (await Save(admin, 1_500, 0)).StatusCode.Should().Be(HttpStatusCode.OK);

        var runningResponse = await recruiter.PostAsJsonAsync(
            "/api/jobs/job-1/bulk-scoring-requests/preview",
            new { folderUrl = Large });
        var runningContent = await runningResponse.Content.ReadAsStringAsync();
        runningResponse.StatusCode.Should().Be(HttpStatusCode.Created, runningContent);
        var running = JsonSerializer.Deserialize<JsonElement>(runningContent);
        var runningId = running.GetProperty("requestId").GetString()!;
        running.GetProperty("limits").GetProperty("maxFilesPerRequest").GetInt32().Should().Be(1_500);

        (await Save(admin, 100, 1)).StatusCode.Should().Be(HttpStatusCode.OK);

        var more = await (await recruiter.PostAsync($"/api/bulk-scoring-requests/{runningId}/discover-more", null)).Content.ReadFromJsonAsync<JsonElement>();
        more.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(1_500, "the running request keeps its own maximum");
        more.GetProperty("progress").GetProperty("discoveryStatus").GetString().Should().Be("limit_reached");

        var fresh = await (await recruiter.PostAsJsonAsync("/api/jobs/job-1/bulk-scoring-requests/preview", new { folderUrl = Large })).Content.ReadFromJsonAsync<JsonElement>();
        fresh.GetProperty("limits").GetProperty("maxFilesPerRequest").GetInt32().Should().Be(100);
        fresh.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(100);
        fresh.GetProperty("progress").GetProperty("discoveryStatus").GetString().Should().Be("limit_reached");
    }
}
