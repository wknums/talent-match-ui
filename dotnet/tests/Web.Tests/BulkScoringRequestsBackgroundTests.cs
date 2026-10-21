using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace TalentMatch.Web.Tests;

// Once a request is confirmed nothing depends on the recruiter's browser or computer: the real worker runs inside
// the host and the status is readable from any later session (SC-003, SC-012). Host restarts are covered by
// BulkScoringWorkerTests, which restarts the worker over the same database.
public sealed class BulkScoringRequestsBackgroundTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";

    [Fact]
    public async Task After_a_lapse_signing_in_again_and_retrying_imports_the_files_under_a_new_authorization()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bulk-web-{Guid.NewGuid():N}.db");
        try
        {
            using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock", runWorker: true, databasePath: path);
            using var recruiter = await factory.LoginAsync("recruiter", "test123");
            var begin = await (await recruiter.PostAsJsonAsync(
                    "/api/jobs/job-1/source-connections",
                    new { folderUrl = Candidates }))
                .Content.ReadFromJsonAsync<JsonElement>();
            var connectionId = begin.GetProperty("connectionId").GetString();
            var preview = await (await recruiter.PostAsync(
                    $"/api/source-connections/{connectionId}/preview",
                    null))
                .Content.ReadFromJsonAsync<JsonElement>();
            var requestId = preview.GetProperty("requestId").GetString()!;
            // The mock identity provider refuses this request's sign-in from its first background read.
            factory.Services.GetRequiredService<TalentMatch.Infrastructure.SharePoint.Authorization.MockAuthorizationControl>().Lapse(requestId, "session_revoked");
            (await recruiter.PostAsJsonAsync($"/api/bulk-scoring-requests/{requestId}/confirm", new { convertToRasterPdf = false, allowDuplicates = true }))
                .StatusCode.Should().Be(HttpStatusCode.Created);

            JsonElement detail = default;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                detail = await (await recruiter.GetAsync($"/api/bulk-scoring-requests/{requestId}")).Content.ReadFromJsonAsync<JsonElement>();
                if (detail.GetProperty("status").GetString() == "completed") break;
                await Task.Delay(300);
            }
            detail.GetProperty("status").GetString().Should().Be("completed");
            detail.GetProperty("outcome").GetString().Should().Be("failed");
            detail.GetProperty("counts").GetProperty("failed").GetInt32().Should().Be(11);
            detail.GetProperty("authorizationState").GetString().Should().Be("lapsed");

            // The recruiter signs in again (a new session) and uses Retry failed: a new request with its own authorization.
            var retryConnection = await (await recruiter.PostAsJsonAsync(
                    "/api/jobs/job-1/source-connections",
                    new { folderUrl = Candidates }))
                .Content.ReadFromJsonAsync<JsonElement>();
            var retry = await recruiter.PostAsJsonAsync(
                $"/api/bulk-scoring-requests/{requestId}/retry-failed",
                new { connectionId = retryConnection.GetProperty("connectionId").GetString() });
            retry.StatusCode.Should().Be(HttpStatusCode.Created);
            var retryId = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString()!;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                detail = await (await recruiter.GetAsync($"/api/bulk-scoring-requests/{retryId}")).Content.ReadFromJsonAsync<JsonElement>();
                var counts = detail.GetProperty("counts");
                if (counts.GetProperty("scoring").GetInt32()
                    + counts.GetProperty("completed").GetInt32() == 11) break;
                await Task.Delay(300);
            }

            (detail.GetProperty("counts").GetProperty("scoring").GetInt32()
                + detail.GetProperty("counts").GetProperty("completed").GetInt32()).Should().Be(
                    11,
                    "every file the lapse stopped reached scoring or completed it under the new authorization");
            detail.GetProperty("authorizationState").GetString().Should().Be("active");
            detail.GetProperty("retryOfRequestId").GetString().Should().Be(requestId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Work_continues_after_the_client_is_gone_and_a_fresh_session_can_read_the_result()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bulk-web-{Guid.NewGuid():N}.db");
        try
        {
            using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory(
                "mock", runWorker: true, databasePath: path);

            string requestId;
            using (var recruiter = await factory.LoginAsync("recruiter", "test123"))
            {
                var begin = await (await recruiter.PostAsJsonAsync(
                        "/api/jobs/job-1/source-connections",
                        new { folderUrl = Candidates }))
                    .Content.ReadFromJsonAsync<JsonElement>();
                var preview = await (await recruiter.PostAsync(
                        $"/api/source-connections/{begin.GetProperty("connectionId").GetString()}/preview",
                        null))
                    .Content.ReadFromJsonAsync<JsonElement>();
                requestId = preview.GetProperty("requestId").GetString()!;
                var confirm = await recruiter.PostAsJsonAsync($"/api/bulk-scoring-requests/{requestId}/confirm", new { convertToRasterPdf = false, allowDuplicates = true });
                confirm.StatusCode.Should().Be(HttpStatusCode.Created);
            }
            // The first client, its cookies and its HttpContext no longer exist.

            using var later = await factory.LoginAsync("recruiter", "test123");
            JsonElement detail = default;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                detail = await (await later.GetAsync($"/api/bulk-scoring-requests/{requestId}")).Content.ReadFromJsonAsync<JsonElement>();
                var counts = detail.GetProperty("counts");
                if (counts.GetProperty("scoring").GetInt32()
                    + counts.GetProperty("completed").GetInt32() == 11) break;
                await Task.Delay(300);
            }

            var finalCounts = detail.GetProperty("counts");
            (finalCounts.GetProperty("scoring").GetInt32()
                + finalCounts.GetProperty("completed").GetInt32()).Should().Be(
                    11,
                    $"every file was imported and reached scoring or completed it in the background; detail was {detail}");
            detail.GetProperty("counts").GetProperty("waiting").GetInt32().Should().Be(0);

            var items = await (await later.GetAsync($"/api/bulk-scoring-requests/{requestId}/items?pageSize=100")).Content.ReadFromJsonAsync<JsonElement>();
            items.GetProperty("items").EnumerateArray().Should().OnlyContain(i => i.GetProperty("applicationId").GetString() != null);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                try { File.Delete(file); } catch (IOException) { }
        }
    }
}
