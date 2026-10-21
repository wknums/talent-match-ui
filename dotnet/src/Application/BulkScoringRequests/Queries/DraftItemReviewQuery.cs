using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediatR;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Queries;

public sealed record DraftItemReviewQuery(
    string RequestId,
    string? ReviewCursor,
    int PageSize = 100) : IRequest<DraftItemReviewPageDto>;

public sealed record DraftItemReviewPageDto(
    IReadOnlyList<BulkPreviewFileDto> Items,
    int TotalItems,
    int ReviewSnapshotVersion,
    string? NextReviewCursor);

public sealed class DraftItemReviewQueryHandler(
    BulkScoringAccess access,
    IBulkScoringRepository repository) : IRequestHandler<DraftItemReviewQuery, DraftItemReviewPageDto>
{
    public async Task<DraftItemReviewPageDto> Handle(DraftItemReviewQuery query, CancellationToken cancellationToken)
    {
        var request = await BulkRequestLoader.LoadAsync(
            access, repository, query.RequestId, mutate: false, cancellationToken);
        if (request.Status != BulkRequestStatus.Draft)
            throw BulkApiException.Conflict("invalid_request_state", "Draft items are available only while reviewing a draft.");

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var cursor = query.ReviewCursor is null ? null : DraftReviewCursorCodec.Decode(query.ReviewCursor);
        if (cursor is not null)
        {
            if (!string.Equals(cursor.RequestId, request.Id, StringComparison.Ordinal)
                || cursor.PageSize != pageSize)
                throw InvalidCursor();
            if (cursor.ReviewSnapshotVersion != request.DraftReviewVersion)
                throw Changed();
        }

        // A draft is capped at 100,000 items. Materializing its frozen item set here lets this
        // application-layer query impose the contract's ordinal tuple independent of provider collation.
        var stored = await repository.GetItemsAsync(
            request.Id, new BulkItemQuery(1, int.MaxValue), cancellationToken);
        if (cursor is not null && cursor.TotalItems != stored.Total)
            throw Changed();

        var ordered = stored.Items
            .OrderBy(item => item.SourcePath, StringComparer.Ordinal)
            .ThenBy(item => item.SourceDriveItemId, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        var start = 0;
        if (cursor is not null)
        {
            var previous = Array.FindIndex(ordered, item =>
                string.Equals(item.SourcePath, cursor.LastPath, StringComparison.Ordinal)
                && string.Equals(item.SourceDriveItemId, cursor.LastSourceItemId, StringComparison.Ordinal)
                && string.Equals(item.Id, cursor.LastItemId, StringComparison.Ordinal));
            if (previous < 0) throw Changed();
            start = previous + 1;
        }

        var selected = ordered.Skip(start).Take(pageSize).ToArray();
        string? next = null;
        if (start + selected.Length < ordered.Length)
        {
            var last = selected[^1];
            next = DraftReviewCursorCodec.Encode(new DraftReviewCursor(
                request.Id,
                request.DraftReviewVersion,
                stored.Total,
                pageSize,
                last.SourcePath,
                last.SourceDriveItemId,
                last.Id));
        }

        return new DraftItemReviewPageDto(
            selected.Select(item => new BulkPreviewFileDto(
                item.Id,
                item.FileName,
                item.SourcePath,
                item.SizeBytes,
                item.MimeType,
                item.ExcludedWhenRasterized)).ToArray(),
            stored.Total,
            request.DraftReviewVersion,
            next);
    }

    private static BulkApiException InvalidCursor() =>
        BulkApiException.Invalid("The draft review cursor is invalid.", "invalid_review_cursor");

    private static BulkApiException Changed() =>
        BulkApiException.Conflict("draft_review_changed", "Discovery changed this draft. Restart review from the first page.");
}

internal sealed record DraftReviewCursor(
    string RequestId,
    int ReviewSnapshotVersion,
    int TotalItems,
    int PageSize,
    string LastPath,
    string LastSourceItemId,
    string LastItemId);

internal static class DraftReviewCursorCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);

    public static string Encode(DraftReviewCursor cursor)
    {
        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(cursor, Json));
        var signature = HMACSHA256.HashData(SigningKey, Encoding.ASCII.GetBytes(payload));
        return payload + "." + Base64Url.Encode(signature);
    }

    public static DraftReviewCursor Decode(string encoded)
    {
        try
        {
            var parts = encoded.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payloadBytes = Base64Url.Decode(parts[0]);
            var signature = Base64Url.Decode(parts[1]);
            var expected = HMACSHA256.HashData(SigningKey, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) throw new CryptographicException();
            return JsonSerializer.Deserialize<DraftReviewCursor>(payloadBytes, Json) ?? throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or JsonException or CryptographicException)
        {
            throw BulkApiException.Invalid("The draft review cursor is invalid.", "invalid_review_cursor");
        }
    }

    private static class Base64Url
    {
        public static string Encode(ReadOnlySpan<byte> bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var decoded = Convert.FromBase64String(normalized);
            if (!string.Equals(Encode(decoded), value, StringComparison.Ordinal)) throw new FormatException();
            return decoded;
        }
    }
}
