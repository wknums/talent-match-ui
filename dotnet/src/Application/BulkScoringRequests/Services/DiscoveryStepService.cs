using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Services;

public enum DiscoveryStopReason { StepFull, ScanGuard, TreeExhausted, LimitReached }

public sealed record DiscoveryStepInput(
    string RequestId,
    SourceFolder Root,
    string? CursorJson,
    SourceCallContext Context,
    long PerFileLimitBytes,
    // Capacity left under the request's cumulative limits (snapshotted maximum minus what is included).
    int FilesRemaining,
    long BytesRemaining,
    int NextOrdinal,
    // Lookup used after a folder is re-listed: item identifiers already stored for this request.
    Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? FindExistingItemIds = null,
    // Frozen per-request guards. Null is retained for compatibility with callers that predate the snapshot columns.
    int? StepFileLimit = null,
    int? StepEntryLimit = null,
    TimeSpan? StepDurationLimit = null);

public sealed record DiscoveryStepOutcome(
    IReadOnlyList<BulkScoringItem> NewItems,
    // Signed: a re-listed folder first reverses its earlier contribution.
    IReadOnlyDictionary<BulkReasonCode, int> ExcludedTallies,
    int InaccessibleDelta,
    int EntriesScannedDelta,
    BulkDiscoveryStatus Status,
    string? NextCursorJson,
    DiscoveryStopReason StoppedBy)
{
    public BulkDiscoveryStepResult ToStepResult(DateTime expiresAt) =>
        new(NewItems, ExcludedTallies, InaccessibleDelta, EntriesScannedDelta, Status, NextCursorJson, expiresAt);
}

// Runs one bounded discovery step (research R4): up to BULK_DISCOVERY_STEP_FILES eligible files, a scan
// guard on entries and time, cumulative limits at their exact boundary, and recovery from expired skip
// tokens without double counting. Excluded entries are tallied by reason and never stored.
public sealed class DiscoveryStepService(
    ISharePointSource source,
    EligibilityEvaluator evaluator,
    BulkScoringOptions options,
    TimeProvider clock,
    IAuthorizationKeyWrapper? keyWrapper = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public async Task<DiscoveryStepOutcome> RunStepAsync(DiscoveryStepInput input, CancellationToken cancellationToken = default)
    {
        var state = input.CursorJson is null
            ? new DiscoveryCursor()
            : await ReadCursorAsync(input.RequestId, input.CursorJson, cancellationToken);
        var stepFileLimit = Math.Clamp(input.StepFileLimit ?? options.DiscoveryStepFiles, 1, 1_000);
        var stepEntryLimit = Math.Max(1, input.StepEntryLimit ?? options.DiscoveryStepMaxEntries);
        var stepDurationLimit = input.StepDurationLimit is { } frozenDuration && frozenDuration > TimeSpan.Zero
            ? frozenDuration
            : options.DiscoveryStepMaxDuration;

        var started = clock.GetTimestamp();
        var items = new List<BulkScoringItem>();
        var tallies = new Dictionary<BulkReasonCode, int>();
        var inaccessible = 0;
        var entries = 0;
        var bytes = 0L;
        var stop = DiscoveryStopReason.TreeExhausted;
        var limitReached = false;

        while (true)
        {
            // Files left over from the previous step's last page are processed before reading more.
            if (state.Carry.Count > 0)
            {
                var carried = state.Carry;
                state.Carry = [];
                var rest = ProcessFiles(carried);
                if (rest.Count > 0) { state.Carry = rest; stop = limitReached ? DiscoveryStopReason.LimitReached : DiscoveryStopReason.StepFull; break; }
                if (limitReached) { stop = DiscoveryStopReason.LimitReached; break; }
                if (items.Count >= stepFileLimit) { stop = DiscoveryStopReason.StepFull; break; }
            }

            if (state.SourceExhausted) { stop = DiscoveryStopReason.TreeExhausted; break; }

            var page = await source.ReadNextPageAsync(input.Root, state.SourceCursor, input.Context, cancellationToken);

            if (page.FolderRestarted)
            {
                // The folder is listed again from its start: reverse what it contributed before.
                entries -= state.Contribution.Entries;
                inaccessible -= state.Contribution.Inaccessible;
                foreach (var (reason, count) in state.Contribution.Excluded)
                    tallies[reason] = tallies.GetValueOrDefault(reason) - count;
                state.Contribution = new FolderContribution { FolderKey = page.FolderKey };
            }
            else if (state.Contribution.FolderKey != page.FolderKey)
            {
                state.Contribution = new FolderContribution { FolderKey = page.FolderKey };
            }

            entries += page.EntriesScanned;
            inaccessible += page.InaccessibleCount;
            state.Contribution.Entries += page.EntriesScanned;
            state.Contribution.Inaccessible += page.InaccessibleCount;
            state.SourceCursor = page.NextCursor;
            state.SourceExhausted = page.Exhausted;
            state.Started = true;

            var files = page.Files;
            if (page.FolderRestarted && files.Count > 0 && input.FindExistingItemIds is not null)
            {
                var existing = await input.FindExistingItemIds(files.Select(f => f.ItemId).ToArray(), cancellationToken);
                files = files.Where(f => !existing.Contains(f.ItemId)).ToList();
            }

            var leftover = ProcessFiles(files);
            if (leftover.Count > 0)
            {
                state.Carry = leftover;
                stop = limitReached ? DiscoveryStopReason.LimitReached : DiscoveryStopReason.StepFull;
                break;
            }

            if (limitReached) { stop = DiscoveryStopReason.LimitReached; break; }
            if (state.SourceExhausted) { stop = DiscoveryStopReason.TreeExhausted; break; }
            if (items.Count >= stepFileLimit) { stop = DiscoveryStopReason.StepFull; break; }
            if (entries >= stepEntryLimit
                || clock.GetElapsedTime(started) >= stepDurationLimit)
            {
                stop = DiscoveryStopReason.ScanGuard;
                break;
            }
        }

        var exhausted = state.SourceExhausted && state.Carry.Count == 0;
        var status = limitReached
            ? BulkDiscoveryStatus.LimitReached
            : exhausted ? BulkDiscoveryStatus.Complete : BulkDiscoveryStatus.MoreAvailable;
        var cursorOut = status == BulkDiscoveryStatus.MoreAvailable
            ? await WriteCursorAsync(input.RequestId, state, cancellationToken)
            : null;
        return new DiscoveryStepOutcome(items, tallies, inaccessible, entries, status, cursorOut, stop);

        // Returns the files not processed because the step filled up or a limit was hit.
        List<SourceFile> ProcessFiles(IReadOnlyList<SourceFile> files)
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var result = evaluator.Evaluate(file, input.PerFileLimitBytes, rasterize: false);
                if (!result.Included)
                {
                    var reason = result.Exclusion!.Value;
                    tallies[reason] = tallies.GetValueOrDefault(reason) + 1;
                    state.Contribution.Excluded[reason] = state.Contribution.Excluded.GetValueOrDefault(reason) + 1;
                    continue;
                }

                if (items.Count >= input.FilesRemaining || bytes + file.SizeBytes > input.BytesRemaining)
                {
                    limitReached = true;
                    return [];
                }

                if (items.Count >= stepFileLimit)
                    return files.Skip(i).ToList();

                bytes += file.SizeBytes;
                items.Add(new BulkScoringItem
                {
                    RequestId = input.RequestId,
                    Ordinal = input.NextOrdinal + items.Count,
                    SourceDriveId = file.DriveId,
                    SourceDriveItemId = file.ItemId,
                    SourceETag = file.ETag,
                    FileName = file.Name.Length > 500 ? file.Name[..500] : file.Name,
                    SourcePath = file.Path,
                    MimeType = result.MimeType!,
                    SizeBytes = file.SizeBytes,
                    ExcludedWhenRasterized = result.ExcludedWhenRasterized,
                });
            }
            return [];
        }
    }

    private async Task<DiscoveryCursor> ReadCursorAsync(
        string requestId,
        string cursor,
        CancellationToken cancellationToken)
    {
        if (keyWrapper is null)
            return JsonSerializer.Deserialize<DiscoveryCursor>(cursor, Json)
                ?? throw new InvalidOperationException("The discovery cursor is invalid.");

        try
        {
            var envelope = JsonSerializer.Deserialize<ProtectedDiscoveryCursor>(cursor, Json)
                ?? throw new CryptographicException();
            var dataKey = await keyWrapper.UnwrapAsync(
                envelope.WrappedDataKey, envelope.KeyVersion, cancellationToken);
            var plaintext = new byte[envelope.Ciphertext.Length];
            using var aes = new AesGcm(dataKey, TagSize);
            aes.Decrypt(
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.Tag,
                plaintext,
                Encoding.UTF8.GetBytes(requestId));
            return JsonSerializer.Deserialize<DiscoveryCursor>(plaintext, Json)
                ?? throw new CryptographicException();
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            throw new InvalidOperationException("The discovery cursor is invalid.", exception);
        }
    }

    private async Task<string> WriteCursorAsync(
        string requestId,
        DiscoveryCursor cursor,
        CancellationToken cancellationToken)
    {
        if (keyWrapper is null) return JsonSerializer.Serialize(cursor, Json);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(cursor, Json);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(dataKey, TagSize))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(requestId));
        var wrapped = await keyWrapper.WrapAsync(dataKey, cancellationToken);
        CryptographicOperations.ZeroMemory(dataKey);
        return JsonSerializer.Serialize(
            new ProtectedDiscoveryCursor(nonce, tag, ciphertext, wrapped.WrappedDataKey, wrapped.KeyVersion),
            Json);
    }

    private sealed class DiscoveryCursor
    {
        public bool Started { get; set; }
        public string? SourceCursor { get; set; }
        public bool SourceExhausted { get; set; }
        public List<SourceFile> Carry { get; set; } = [];
        public FolderContribution Contribution { get; set; } = new();
    }

    private sealed class FolderContribution
    {
        public string? FolderKey { get; set; }
        public int Entries { get; set; }
        public int Inaccessible { get; set; }
        public Dictionary<BulkReasonCode, int> Excluded { get; set; } = [];
    }

    private sealed record ProtectedDiscoveryCursor(
        byte[] Nonce,
        byte[] Tag,
        byte[] Ciphertext,
        byte[] WrappedDataKey,
        string KeyVersion);
}
