using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public sealed class BulkRequestActorDetailEndpointsTests
{
    private static readonly DateTime SubmittedAt =
        new(2026, 10, 14, 10, 11, 12, DateTimeKind.Utc);

    private static readonly DateTime CancelRequestedAt =
        new(2026, 10, 14, 12, 13, 14, DateTimeKind.Utc);

    [Fact]
    public async Task Submit_and_cancel_recorded_actor_ids_and_utc_times_appear_on_authorized_detail()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var owner = await factory.LoginAsync("recruiter", "test123");
        var requestId = await SeedRequestAsync(factory, cancelled: true);

        string actorId;
        DateTime submittedAt;
        DateTime cancelRequestedAt;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.BulkScoringRequests.SingleAsync(candidate => candidate.Id == requestId);
            var user = await db.Users.SingleAsync(candidate => candidate.Username == "recruiter");
            actorId = user.Id;
            submittedAt = request.ConfirmedAt!.Value;
            cancelRequestedAt = request.CancelRequestedAt!.Value;

            using (new AssertionScope())
            {
                request.OwnerActorId.Should().Be(actorId);
                request.CancelledByActorId.Should().Be(actorId);
                submittedAt.Kind.Should().Be(DateTimeKind.Utc);
                cancelRequestedAt.Kind.Should().Be(DateTimeKind.Utc);
            }
        }

        var detailResponse = await owner.GetAsync($"/api/bulk-scoring-requests/{requestId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();

        using (new AssertionScope())
        {
            detail.GetProperty("submittedBy").GetString().Should().Be("Recruiter");
            detail.GetProperty("submittedAt").GetDateTime().Should().Be(submittedAt);
            detail.GetProperty("submittedAt").GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
            detail.GetProperty("cancelledBy").GetString().Should().Be("Recruiter");
            detail.GetProperty("cancelRequestedAt").GetDateTime().Should().Be(cancelRequestedAt);
            detail.GetProperty("cancelRequestedAt").GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
        }
    }

    [Theory]
    [InlineData("display", "Recorded Display")]
    [InlineData("missing", "recorded-id")]
    [InlineData("blank", "recorded-id")]
    public async Task Authorized_detail_resolves_display_only_from_the_recorded_actor_id(
        string userState,
        string expectedKind)
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var owner = await factory.LoginAsync("recruiter", "test123");
        var requestId = await SeedRequestAsync(factory);

        string recordedActorId;
        const string forbiddenUsername = "username-must-never-be-returned";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.BulkScoringRequests.SingleAsync(candidate => candidate.Id == requestId);
            recordedActorId = request.OwnerActorId;
            var user = await db.Users.SingleAsync(candidate => candidate.Id == recordedActorId);

            if (userState == "display")
            {
                user.FullName = "Recorded Display";
                user.Username = forbiddenUsername;
            }
            else if (userState == "blank")
            {
                user.FullName = "  ";
                user.Username = forbiddenUsername;
            }
            else
            {
                db.Users.Remove(user);
                db.Users.Add(new User
                {
                    FullName = "Wrong Username Match",
                    Username = recordedActorId,
                    Role = "recruiter",
                    PasswordHash = "unused",
                });
            }

            await db.SaveChangesAsync();
        }

        var response = await owner.GetAsync($"/api/bulk-scoring-requests/{requestId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        var expected = expectedKind == "recorded-id" ? recordedActorId : expectedKind;

        detail.GetProperty("submittedBy").GetString().Should().Be(expected);
        detail.GetProperty("submittedBy").GetString().Should().NotBe(forbiddenUsername);
    }

    [Fact]
    public async Task Actor_detail_is_absent_from_list_notification_admin_and_unauthorized_surfaces()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var owner = await factory.LoginAsync("recruiter", "test123");
        using var admin = await factory.LoginAsync("admin", "adm1n99");
        using var unauthorized = factory.CreateClient();
        var requestId = await SeedRequestAsync(factory, cancelled: true, completed: true);

        var ownerList = await JsonAsync(await owner.GetAsync("/api/jobs/job-1/bulk-scoring-requests"));
        var notifications = await JsonAsync(await owner.GetAsync("/api/bulk-scoring-requests/notifications"));
        var adminList = await JsonAsync(await admin.GetAsync("/api/jobs/job-1/bulk-scoring-requests"));
        var adminUsers = await JsonAsync(await admin.GetAsync("/api/users/"));
        var denied = await unauthorized.GetAsync($"/api/bulk-scoring-requests/{requestId}");

        using (new AssertionScope())
        {
            AssertNoActorDetail(ownerList);
            AssertNoActorDetail(notifications);
            AssertNoActorDetail(adminList);
            AssertNoActorDetail(adminUsers);

            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            AssertNoActorDetail(await denied.Content.ReadAsStringAsync());
        }
    }

    private static async Task<string> SeedRequestAsync(
        BulkScoringRequestsEndpointsTests.BulkFactory factory,
        bool cancelled = false,
        bool completed = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actorId = await db.Users
            .Where(user => user.Username == "recruiter")
            .Select(user => user.Id)
            .SingleAsync();
        var request = new BulkScoringRequest
        {
            Id = Guid.NewGuid().ToString(),
            JobId = "job-1",
            OwnerActorId = actorId,
            SourceKind = BulkSourceKind.SharePoint,
            SourceTenantId = "source-tenant",
            SourceAccountBindingHash = "source-account",
            SourceDriveId = "drive",
            SourceFolderItemId = "folder",
            SourceFolderName = "Candidates",
            SourceFolderPath = "/Candidates",
            SourceWebUrl = "https://contoso.sharepoint.com/sites/hr/Shared Documents/Candidates",
            MaxFilesPerRequest = 30_000,
            MaxBytesPerFile = 8_388_608,
            MaxTotalBytes = 53_687_091_200,
            DiscoveryStepMaxFiles = 1_000,
            DiscoveryStepMaxEntries = 2_000,
            DiscoveryStepMaxSeconds = 30,
            RasterDpi = 200,
            DiscoveryStatus = BulkDiscoveryStatus.Complete,
            DiscoveredCount = 1,
            IncludedItemCount = 1,
            CreatedAt = SubmittedAt.AddMinutes(-1),
        };
        request.Confirm(false, false, "config-at-submit", SubmittedAt);
        if (cancelled)
            request.RequestCancel(actorId, CancelRequestedAt);
        if (completed)
        {
            request.ApplyCounts(new Dictionary<BulkItemStatus, int>(), 0);
            request.Complete(CancelRequestedAt.AddMinutes(1));
        }

        db.BulkScoringRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void AssertNoActorDetail(JsonElement value) =>
        AssertNoActorDetail(value.GetRawText());

    private static void AssertNoActorDetail(string value)
    {
        var normalized = value.ToLowerInvariant();
        normalized.Should().NotContain("\"submittedby\"");
        normalized.Should().NotContain("\"submittedat\"");
        normalized.Should().NotContain("\"cancelledby\"");
        normalized.Should().NotContain("\"cancelrequestedat\"");
    }
}
