using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class BulkRequestActorDetailTests
{
    private static readonly DateTime SubmittedAt =
        new(2026, 10, 14, 10, 11, 12, DateTimeKind.Utc);

    private static readonly DateTime CancelRequestedAt =
        new(2026, 10, 14, 12, 13, 14, DateTimeKind.Utc);

    [Fact]
    public async Task Authorized_detail_exposes_recorded_submit_and_cancel_actors_with_their_utc_times()
    {
        const string submitActorId = "recorded-submit-actor";
        const string cancelActorId = "recorded-cancel-actor";
        var request = RecordedRequest(submitActorId, cancelActorId);
        var users = Users(
            new User { Id = submitActorId, FullName = "Submit Display", Username = "submit.username" },
            new User { Id = cancelActorId, FullName = "Cancel Display", Username = "cancel.username" });

        var detail = await BulkHandlerKit.Access(users: users.Object)
            .DetailAsync(request, new Job { Id = "job-1" }, null, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(detail);

        using (new AssertionScope())
        {
            request.OwnerActorId.Should().Be(submitActorId);
            request.ConfirmedAt.Should().Be(SubmittedAt);
            request.ConfirmedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
            request.CancelledByActorId.Should().Be(cancelActorId);
            request.CancelRequestedAt.Should().Be(CancelRequestedAt);
            request.CancelRequestedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

            json.GetProperty("SubmittedBy").GetString().Should().Be("Submit Display");
            json.GetProperty("SubmittedAt").GetDateTime().Should().Be(SubmittedAt);
            json.GetProperty("CancelledBy").GetString().Should().Be("Cancel Display");
            json.GetProperty("CancelRequestedAt").GetDateTime().Should().Be(CancelRequestedAt);
        }

        users.Verify(
            repository => repository.GetByIdAsync(
                It.Is<string>(id => id == submitActorId || id == cancelActorId),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        users.Verify(
            repository => repository.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actor_display_falls_back_only_to_the_recorded_id_when_the_user_or_display_name_is_missing(
        bool userExistsWithBlankDisplayName)
    {
        const string actorId = "recorded-actor-id";
        const string username = "username-must-never-be-returned";
        var request = RecordedRequest(actorId);
        var users = new Mock<IUserRepository>();
        users.Setup(repository => repository.GetByIdAsync(actorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(userExistsWithBlankDisplayName
                ? new User { Id = actorId, FullName = "  ", Username = username }
                : null);

        var detail = await BulkHandlerKit.Access(users: users.Object)
            .DetailAsync(request, new Job { Id = "job-1" }, null, CancellationToken.None);

        detail.SubmittedBy.Should().Be(actorId);
        detail.SubmittedBy.Should().NotBe(username);
        users.Verify(
            repository => repository.GetByIdAsync(actorId, It.IsAny<CancellationToken>()),
            Times.Once);
        users.Verify(
            repository => repository.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task List_and_notification_projections_never_resolve_or_expose_detail_only_actor_fields()
    {
        var request = RecordedRequest(BulkHandlerKit.UserId, "recorded-cancel-actor");
        var users = new Mock<IUserRepository>();
        users.Setup(repository => repository.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                Id = BulkHandlerKit.UserId,
                FullName = "Detail Only Display",
                Username = "detail-only.username",
            });
        var repository = new Mock<IBulkScoringRepository>();
        repository.Setup(store => store.ListByJobAsync("job-1", 1, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkPage<BulkScoringRequest>([request], 1, 50, 1));
        repository.Setup(store => store.ListUnacknowledgedAsync(
                BulkHandlerKit.UserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([request]);
        var access = BulkHandlerKit.Access(users: users.Object);

        var page = await new ListBulkScoringRequestsHandler(access, repository.Object)
            .Handle(new ListBulkScoringRequestsQuery("job-1", 1, 50), CancellationToken.None);
        var notifications = await new ListBulkScoringNotificationsHandler(access, repository.Object)
            .Handle(new ListBulkScoringNotificationsQuery(), CancellationToken.None);

        using (new AssertionScope())
        {
            AssertNoActorDetail(JsonSerializer.SerializeToElement(page));
            AssertNoActorDetail(JsonSerializer.SerializeToElement(notifications));
            users.Verify(
                store => store.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            users.Verify(
                store => store.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }

    private static BulkScoringRequest RecordedRequest(string submitActorId, string? cancelActorId = null)
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Draft);
        request.OwnerActorId = submitActorId;
        request.DiscoveredCount = 1;
        request.Confirm(false, false, "config-at-submit", SubmittedAt);
        if (cancelActorId is not null)
            request.RequestCancel(cancelActorId, CancelRequestedAt);
        return request;
    }

    private static Mock<IUserRepository> Users(params User[] records)
    {
        var byId = records.ToDictionary(user => user.Id, StringComparer.Ordinal);
        var users = new Mock<IUserRepository>();
        users.Setup(repository => repository.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => byId.GetValueOrDefault(id));
        return users;
    }

    private static void AssertNoActorDetail(JsonElement value)
    {
        var text = value.GetRawText();
        text.Should().NotContain("\"SubmittedBy\"");
        text.Should().NotContain("\"SubmittedAt\"");
        text.Should().NotContain("\"CancelledBy\"");
        text.Should().NotContain("\"CancelRequestedAt\"");
    }
}
