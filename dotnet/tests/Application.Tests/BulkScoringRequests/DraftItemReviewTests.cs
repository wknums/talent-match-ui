using FluentAssertions;
using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Queries;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using Xunit;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class DraftItemReviewTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(30_000)]
    [InlineData(100_000)]
    public async Task Signed_pages_are_stable_and_complete_without_source_calls_or_discovery_mutation(int itemCount)
    {
        var request = Draft(itemCount);
        var items = Enumerable.Range(0, itemCount)
            .Select(index => BulkHandlerKit.Item(index, BulkItemStatus.Waiting))
            .OrderByDescending(item => item.SourcePath, StringComparer.Ordinal)
            .ToArray();
        var repository = Repository(request, items);
        var source = new Mock<ISharePointSource>(MockBehavior.Strict);
        var handler = new DraftItemReviewQueryHandler(BulkHandlerKit.Access(source.Object), repository.Object);
        var originalDiscoveryCursor = request.DiscoveryCursorJson;
        var first = await handler.Handle(new DraftItemReviewQuery(request.Id, null, 100), CancellationToken.None);
        var repeated = await handler.Handle(new DraftItemReviewQuery(request.Id, null, 100), CancellationToken.None);

        repeated.Should().BeEquivalentTo(first);

        first.TotalItems.Should().Be(itemCount);
        first.ReviewSnapshotVersion.Should().Be(7);
        var expected = items
            .OrderBy(item => item.SourcePath, StringComparer.Ordinal)
            .ThenBy(item => item.SourceDriveItemId, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id)
            .ToArray();
        first.Items.Select(item => item.ItemId).Should().Equal(expected.Take(100));

        if (itemCount > 100)
        {
            var second = await handler.Handle(
                new DraftItemReviewQuery(request.Id, first.NextReviewCursor, 100),
                CancellationToken.None);
            second.TotalItems.Should().Be(itemCount);
            second.ReviewSnapshotVersion.Should().Be(7);
            second.Items.Select(item => item.ItemId).Should().Equal(expected.Skip(100).Take(100));
            first.Items.Concat(second.Items).Select(item => item.ItemId).Should().OnlyHaveUniqueItems();
        }

        request.DiscoveryCursorJson.Should().Be(originalDiscoveryCursor);
        request.DraftReviewVersion.Should().Be(7);
        repository.Verify(r => r.ApplyDiscoveryStepAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<BulkDiscoveryStepResult>(), It.IsAny<CancellationToken>()), Times.Never);
        source.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Cursor_is_invalidated_after_an_explicit_discovery_snapshot_advance()
    {
        var request = Draft(101);
        var items = Enumerable.Range(0, 101).Select(index => BulkHandlerKit.Item(index, BulkItemStatus.Waiting)).ToArray();
        var repository = Repository(request, items);
        var source = new Mock<ISharePointSource>(MockBehavior.Strict);
        var handler = new DraftItemReviewQueryHandler(BulkHandlerKit.Access(source.Object), repository.Object);

        var first = await handler.Handle(new DraftItemReviewQuery(request.Id, null, 100), CancellationToken.None);
        first.NextReviewCursor.Should().NotBeNull();
        request.DraftReviewVersion++;
        request.IncludedItemCount++;

        var act = () => handler.Handle(
            new DraftItemReviewQuery(request.Id, first.NextReviewCursor, 100),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<BulkApiException>();
        error.Which.StatusCode.Should().Be(409);
        error.Which.Code.Should().Be("draft_review_changed");
        source.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Tampered_cursor_is_rejected_without_reading_items_or_source()
    {
        var request = Draft(101);
        var items = Enumerable.Range(0, 101).Select(index => BulkHandlerKit.Item(index, BulkItemStatus.Waiting)).ToArray();
        var repository = Repository(request, items);
        var source = new Mock<ISharePointSource>(MockBehavior.Strict);
        var handler = new DraftItemReviewQueryHandler(BulkHandlerKit.Access(source.Object), repository.Object);
        var first = await handler.Handle(new DraftItemReviewQuery(request.Id, null, 100), CancellationToken.None);
        var cursor = first.NextReviewCursor!;
        var replacement = cursor[^1] == 'A' ? 'B' : 'A';

        var act = () => handler.Handle(
            new DraftItemReviewQuery(request.Id, cursor[..^1] + replacement, 100),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<BulkApiException>();
        error.Which.StatusCode.Should().Be(422);
        repository.Verify(r => r.GetItemsAsync(
            It.IsAny<string>(), It.IsAny<BulkItemQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        source.VerifyNoOtherCalls();
    }

    private static BulkScoringRequest Draft(int itemCount)
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Draft);
        request.DraftReviewVersion = 7;
        request.IncludedItemCount = itemCount;
        request.DiscoveredCount = itemCount;
        request.DiscoveryCursorJson = """{"protected":"discovery-state"}""";
        return request;
    }

    private static Mock<IBulkScoringRepository> Repository(
        BulkScoringRequest request,
        IReadOnlyList<BulkScoringItem> items)
    {
        var repository = new Mock<IBulkScoringRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetAsync(request.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => request);
        repository.Setup(r => r.GetItemsAsync(
                request.Id, It.IsAny<BulkItemQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, BulkItemQuery query, CancellationToken _) =>
            {
                var page = items
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .ToArray();
                return new BulkPage<BulkScoringItem>(page, query.Page, query.PageSize, items.Count);
            });
        return repository;
    }
}
