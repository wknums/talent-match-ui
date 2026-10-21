using System.Text.Json;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Common.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Services;

public enum BulkItemOutcome { HandedOff, Skipped, Failed, Cancelled, Retrying }

public sealed class BulkHandoffPersistenceException(Exception innerException)
    : Exception("The scoring handoff could not be persisted.", innerException);

public sealed class BulkWorkerPersistenceException(Exception innerException)
    : Exception("The bulk worker state boundary could not be persisted.", innerException);

// Runs one claimed item through download -> stage -> fingerprint/duplicate -> optional convert ->
// create application -> publish Queued (research R5, R6, R8, R10). Every step persists a marker so a
// crash re-enters at the right step, and application/document identifiers are deterministic so a
// repeated create is a no-op. It never reads or changes scoring capacity.
public sealed class BulkItemProcessor(
    IBulkScoringRepository repository,
    IApplicationRepository applications,
    ISharePointSource source,
    IBulkContentStore contentStore,
    IDocumentRasterizer rasterizer,
    BulkScoringOptions options,
    TimeProvider clock,
    ILogger<BulkItemProcessor> logger,
    IScoringQueueSignal? queueSignal = null,
    BulkTelemetry? telemetry = null)
{
    private const string Pdf = "application/pdf";
    private const string Txt = "text/plain";

    // Throws SourceAuthorizationLapsedException (the worker lapses the request) and OperationCanceledException
    // (shutdown); every other failure is classified onto the item.
    public async Task<BulkItemOutcome> ProcessAsync(BulkScoringItem item, BulkScoringRequest request, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var outcome = await ClassifyAsync(item, request, cancellationToken);
        if (telemetry is not null && outcome != BulkItemOutcome.Retrying)
        {
            var name = outcome switch
            {
                BulkItemOutcome.HandedOff => "handed_off",
                BulkItemOutcome.Skipped => "skipped",
                BulkItemOutcome.Cancelled => "cancelled",
                _ => "failed",
            };
            await telemetry.ItemAsync(request, item, name, (long)clock.GetElapsedTime(started).TotalMilliseconds);
        }
        return outcome;
    }

    private async Task<BulkItemOutcome> ClassifyAsync(BulkScoringItem item, BulkScoringRequest request, CancellationToken cancellationToken)
    {
        var limits = request.MaxBytesPerFile > 0
            ? BulkScoringAccess.FrozenLimits(request)
            : BulkRequestLimits.FromJson(request.LimitsJson);
        var context = new SourceCallContext(SourcePhase.Background, request.OwnerActorId, request.Id);
        try
        {
            return await RunAsync(item, request, limits, context, cancellationToken);
        }
        catch (SourceAuthorizationLapsedException) { throw; }
        catch (BulkHandoffPersistenceException) { throw; }
        catch (BulkWorkerPersistenceException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SourceAccessDeniedException) { return await FailAsync(item, request, BulkReasonCode.AccessDenied, "You no longer have access to this file."); }
        catch (SourceNotFoundException) { return await FailAsync(item, request, BulkReasonCode.SourceMissing, "The file no longer exists in the source folder."); }
        catch (SourceLimitExceededException) { return await FailAsync(item, request, BulkReasonCode.FileTooLarge, "The file is larger than the allowed size."); }
        catch (UnsupportedSourceException) { return await FailAsync(item, request, BulkReasonCode.NotConvertible, "This file type cannot be converted."); }
        catch (BulkConversionException ex) { return await FailConversionAsync(item, request, ex.Reason, ConversionMessage(ex.Reason)); }
        catch (Exception ex)
        {
            // Anything not classified above (network, timeout, transient SQL, an unexpected fault) is retried
            // with backoff and ends as transient_exhausted rather than being silently dropped.
            logger.LogWarning(ex, "Bulk item {ItemId} of request {RequestId} failed transiently on attempt {Attempt}", item.Id, item.RequestId, item.Attempt);
            return await RetryOrFailAsync(item, request, limits, (ex as SourceTransientException)?.RetryAfter);
        }
    }

    private async Task<BulkItemOutcome> RunAsync(
        BulkScoringItem item, BulkScoringRequest request, BulkRequestLimits limits, SourceCallContext context, CancellationToken ct)
    {
        byte[]? original = null;
        byte[]? sourcePdf = null;
        var convert = request.ConvertToRasterPdf && item.MimeType != Txt;
        var sourcePdfRequired = convert && RequiresSourcePdf(item.FileName);
        var sourceFolder = BulkScoringAccess.FolderOf(request);

        if (item.ApplicationCreatedAt is null)
        {
            if (repository is IAtomicBulkScoringItemRepository atomicRepository)
                await atomicRepository.EnsureItemAuditAsync(
                    item,
                    TypedAuditEvent(request, item, BulkAuditEventTypes.BulkItemClaimed, "claimed"),
                    ct);
            else
                await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemClaimed, "claimed", ct);
            await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemImportStarted, "started", ct);
            if (item.SourceStoredAt is null)
            {
                if (await CancelRequestedAsync(request.Id, ct)) return await CancelAsync(item, request);

                var metadata = await source.GetItemMetadataAsync(
                    sourceFolder,
                    item.SourceDriveItemId,
                    context,
                    ct);
                if (metadata is null)
                {
                    await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemSourceMetadataRecheckFailed, "failed", ct);
                    return await FailAsync(item, request, BulkReasonCode.SourceMissing, "The file no longer exists in the source folder.");
                }
                // A retried item carries no recorded eTag; it adopts the current one, read just before the download.
                if (item.SourceETag.Length == 0)
                    item.SourceETag = metadata.ETag;
                else if (!string.Equals(metadata.ETag, item.SourceETag, StringComparison.Ordinal))
                {
                    await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemSourceMetadataRecheckFailed, "failed", ct);
                    return await FailAsync(item, request, BulkReasonCode.SourceChanged, "The file changed after it was discovered. Start a new request to import it.");
                }
                if (metadata.SizeBytes > limits.MaxBytesPerFile)
                {
                    await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemSourceMetadataRecheckFailed, "failed", ct);
                    return await FailAsync(item, request, BulkReasonCode.FileTooLarge, "The file is larger than the allowed size.");
                }
                await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemSourceMetadataRecheckSucceeded, "succeeded", ct);

                original = await TimedAsync(request, item, BulkTelemetry.Phases.Download, () => source.DownloadAsync(
                    sourceFolder,
                    item.SourceDriveItemId,
                    limits.MaxBytesPerFile,
                    context,
                    ct));
                if (!EligibilityEvaluator.ContentMatchesType(item.MimeType, original))
                    return await FailAsync(item, request, BulkReasonCode.UnsupportedType, "The file content does not match its type.");

                // Graph format conversion is a source read. Complete and durably stage it before
                // SourceStoredAt so everything after that marker is product-owned and source-independent.
                if (sourcePdfRequired)
                    sourcePdf = await TimedAsync(request, item, BulkTelemetry.Phases.SourcePdf, () => source.ConvertToPdfAsync(
                        sourceFolder,
                        item.SourceDriveItemId,
                        limits.MaxBytesPerFile * 4,
                        context,
                        ct));

                item.Fingerprint = ContentFingerprint.Compute(original);
                var stagedContents = new List<BulkScoringItemContent>
                {
                    await contentStore.StageAsync(
                        request.Id,
                        item.Id,
                        BulkContentKind.Original,
                        item.FileName,
                        item.MimeType,
                        original,
                        ct),
                };
                if (sourcePdf is not null)
                    stagedContents.Add(await contentStore.StageAsync(
                        request.Id,
                        item.Id,
                        BulkContentKind.Converted,
                        item.FileName,
                        Pdf,
                        sourcePdf,
                        ct));
                item.SourceStoredAt = Now;
                await SaveStagedAsync(
                    item,
                    request,
                    stagedContents,
                    BulkAuditEventTypes.BulkOriginalContentStaged,
                    ct);
                await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemFingerprintRecorded, "recorded", ct);

                // Do not carry source-owned buffers across the boundary. All downstream work must
                // reload the durable product copy (and therefore pass its integrity checks).
                original = null;
                sourcePdf = null;
            }

            original ??= await ReadStagedAsync(item, BulkContentKind.Original, ct);
            if (sourcePdfRequired)
                sourcePdf ??= await ReadStagedAsync(item, BulkContentKind.Converted, ct);

            if (!request.AllowDuplicates && !item.FingerprintClaimed)
            {
                var claimed = repository is IAtomicBulkScoringItemRepository atomicFingerprintRepository
                    ? await atomicFingerprintRepository.TryClaimFingerprintWithAuditAsync(
                        item.Id,
                        item.Fingerprint!,
                        TypedAuditEvent(request, item, BulkAuditEventTypes.BulkItemDuplicateClaimed, "claimed"),
                        ct)
                    : await repository.TryClaimFingerprintAsync(
                        item.Id,
                        item.Fingerprint!,
                        AuditEvent(request, item, BulkAuditEventTypes.BulkItemDuplicateClaimed, "claimed"),
                        ct);
                if (!claimed)
                    return await SkipDuplicateAsync(
                        item, request, BulkReasonCode.DuplicateInRequest,
                        "The same file appears more than once in this request.", ct);
                item.FingerprintClaimed = true;

                foreach (var candidate in ContentFingerprint.LookupCandidates(original))
                    if (await applications.FindDocumentByFingerprintAsync(request.JobId, candidate, ct) is not null)
                        return await SkipDuplicateAsync(
                            item, request, BulkReasonCode.DuplicateInJob,
                            "The same file was already imported for this job.", ct);
            }

            var scored = original;
            var scoredMime = item.MimeType;
            var scoredName = item.FileName;

            if (convert)
            {
                if (item.ConvertedStoredAt is null)
                {
                    if (await CancelRequestedAsync(request.Id, ct)) return await CancelAsync(item, request);
                    if (item.Status == BulkItemStatus.Importing)
                    {
                        item.TransitionTo(BulkItemStatus.Converting, Now);
                        await SaveAuditAsync(
                            item,
                            request,
                            BulkAuditEventTypes.BulkItemConversionStarted,
                            "started",
                            ct);
                    }

                    var converted = await ConvertAsync(
                        item,
                        request,
                        sourcePdf ?? original,
                        sourcePdfRequired ? Pdf : item.MimeType,
                        limits,
                        ct);
                    if (!converted.IsAdmitted)
                        return await RejectConvertedAsync(
                            item,
                            request,
                            converted.RejectionReason ?? BulkReasonCode.ConvertedDocumentInvalid,
                            ct);
                    if (converted.OriginalPageCount <= 0
                        || converted.PageCount <= 0
                        || converted.OriginalPageCount != converted.PageCount)
                        return await RejectConvertedAsync(
                            item,
                            request,
                            BulkReasonCode.ConversionPageMismatch,
                            ct);
                    if (converted.PdfBytes.Length > limits.MaxBytesPerFile)
                        return await RejectConvertedAsync(item, request, BulkReasonCode.ConvertedTooLarge, ct);

                    var convertedContent = await contentStore.StageAsync(
                        request.Id,
                        item.Id,
                        BulkContentKind.Converted,
                        item.FileName,
                        Pdf,
                        converted.PdfBytes,
                        ct);
                    item.OriginalPageCount = converted.OriginalPageCount;
                    item.ConvertedPageCount = converted.PageCount;
                    item.RasterDpiUsed = converted.RasterDpiUsed;
                    item.MinimumPageSsim = (decimal)converted.MinimumPageSsim;
                    item.RasterFidelityValidatedAt = converted.FidelityValidatedAt;
                    item.ConvertedSizeBytes = converted.PdfBytes.Length;
                    item.ConvertedStoredAt = Now;
                    item.ScoredVersion = BulkScoredVersion.Converted;
                    await SaveStagedAsync(
                        item,
                        request,
                        [convertedContent],
                        BulkAuditEventTypes.BulkConvertedContentStaged,
                        ct);
                    await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemConversionCompleted, "completed", ct);
                    scored = converted.PdfBytes;
                }
                else
                {
                    scored = await ReadStagedAsync(item, BulkContentKind.Converted, ct);
                }

                scoredMime = Pdf;
                scoredName = Path.ChangeExtension(item.FileName, ".pdf");
            }
            else
            {
                item.ScoredVersion = BulkScoredVersion.Original;
            }

            await CreateApplicationAsync(item, request, scored, scoredMime, scoredName, ct);
            await RemoveStagedAsync(item, request, convert, ct);
        }

        await HandoffToScoringAsync(item, request, ct);
        queueSignal?.Pulse();
        return BulkItemOutcome.HandedOff;
    }

    private async Task HandoffToScoringAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        CancellationToken ct)
    {
        var originalStatus = item.Status;
        var originalHandedOffAt = item.HandedOffAt;
        var originalVersionId = item.ScoringConfigVersionId;
        var originalUpdatedAt = item.UpdatedAt;

        try
        {
            if (repository is IAtomicBulkScoringItemRepository atomicItemRepository)
            {
                await atomicItemRepository.HandoffToScoringAsync(
                    item,
                    TypedAuditEvent(
                        request,
                        item,
                        BulkAuditEventTypes.BulkItemScoringHandoffQueued,
                        "queued",
                        BulkAuditBoundary.DurableOutbox),
                    Now,
                    ct);
                return;
            }

            if (repository is IAtomicBulkScoringHandoffRepository atomicRepository)
            {
                await atomicRepository.HandoffToScoringAsync(
                    item,
                    AuditEvent(request, item, BulkAuditEventTypes.BulkItemScoringHandoffQueued, "queued"),
                    Now,
                    ct);
                return;
            }

            var currentVersionId = request.Job?.CurrentConfigVersionId;
            if (string.IsNullOrWhiteSpace(currentVersionId))
                throw new InvalidOperationException("The job has no current scoring configuration.");

            item.ScoringConfigVersionId = currentVersionId;
            item.TransitionTo(BulkItemStatus.Scoring, Now);
            await repository.SaveItemWithAuditAsync(
                item,
                AuditEvent(request, item, BulkAuditEventTypes.BulkItemScoringHandoffQueued, "queued"),
                ct);

            try
            {
                await applications.PublishUploadedAsync([item.ApplicationId!], ct);
            }
            catch
            {
                RestoreHandoff(item, originalStatus, originalHandedOffAt, originalVersionId, originalUpdatedAt);
                await repository.SaveItemAsync(item, CancellationToken.None);
                throw;
            }
        }
        catch (Exception ex) when (ex is not BulkHandoffPersistenceException)
        {
            RestoreHandoff(item, originalStatus, originalHandedOffAt, originalVersionId, originalUpdatedAt);
            throw new BulkHandoffPersistenceException(ex);
        }
    }

    private static void RestoreHandoff(
        BulkScoringItem item,
        BulkItemStatus status,
        DateTime? handedOffAt,
        string? scoringConfigVersionId,
        DateTime updatedAt)
    {
        item.Status = status;
        item.HandedOffAt = handedOffAt;
        item.ScoringConfigVersionId = scoringConfigVersionId;
        item.UpdatedAt = updatedAt;
    }

    // A document whose output exceeds the per-file limit is converted once more at a lower resolution before it fails
    // (spike S3: 120 DPI / quality 70 reads as well as 150 / 80 and is about 35% smaller).
    internal const int StepDownDpi = 120;
    internal const int StepDownQuality = 70;

    private async Task<RasterizationResult> ConvertAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        byte[] conversionSource,
        string conversionMimeType,
        BulkRequestLimits limits,
        CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        var kind = BulkTelemetry.KindOf(item.MimeType);
        var rasterOptions = new RasterizationOptions(limits.RasterDpi, limits.RasterJpegQuality, limits.MaxPagesPerDocument, options.RasterTimeout);
        var dpi = rasterOptions.Dpi;
        var steppedDown = false;
        RasterizationResult result;
        try
        {
            result = await rasterizer.RasterizeAsync(conversionSource, conversionMimeType, rasterOptions, ct);
            if (result.PdfBytes.Length > limits.MaxBytesPerFile && (rasterOptions.Dpi > StepDownDpi || rasterOptions.JpegQuality > StepDownQuality))
            {
                var smaller = rasterOptions with { Dpi = Math.Min(rasterOptions.Dpi, StepDownDpi), JpegQuality = Math.Min(rasterOptions.JpegQuality, StepDownQuality) };
                logger.LogInformation("Converted output of bulk item {ItemId} was {Bytes} bytes; converting again at {Dpi} DPI / quality {Quality}", item.Id, result.PdfBytes.Length, smaller.Dpi, smaller.JpegQuality);
                steppedDown = true;
                dpi = smaller.Dpi;
                result = await rasterizer.RasterizeAsync(conversionSource, conversionMimeType, smaller, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            telemetry?.Rasterized(request, item, kind, "failed", ElapsedMs(started), 0, 0, dpi, steppedDown);
            throw;
        }

        telemetry?.Rasterized(request, item, kind, result.IsAdmitted ? "ok" : "rejected", ElapsedMs(started),
            result.PageCount, result.PdfBytes.Length, dpi, steppedDown);
        return result;
    }

    private long ElapsedMs(long started) => (long)clock.GetElapsedTime(started).TotalMilliseconds;

    // Times one source phase of an item. Cancellation (shutdown) is not a phase outcome and is not recorded.
    private async Task<T> TimedAsync<T>(BulkScoringRequest request, BulkScoringItem item, string phase, Func<Task<T>> action)
    {
        var started = clock.GetTimestamp();
        var kind = BulkTelemetry.KindOf(item.MimeType);
        try
        {
            var result = await action();
            telemetry?.Phase(request, item, phase, kind, "ok", ElapsedMs(started));
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            telemetry?.Phase(request, item, phase, kind, "failed", ElapsedMs(started));
            throw;
        }
    }

    private static bool RequiresSourcePdf(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".docx" or ".md" or ".markdown";

    private async Task CreateApplicationAsync(
        BulkScoringItem item, BulkScoringRequest request, byte[] scored, string mimeType, string fileName, CancellationToken ct)
    {
        var applicationId = DeterministicIds.Application(item.Id);
        var documentId = DeterministicIds.Document(item.Id);
        var application = new TalentMatch.Domain.Entities.Application
        {
            Id = applicationId,
            JobId = request.JobId,
            CandidateRef = $"candidate-{applicationId.Replace("-", "")[..8]}",
            CandidateName = DeriveCandidateName(item.FileName),
            Status = "Uploading",
        };
        var document = new ApplicationDocument
        {
            Id = documentId,
            ApplicationId = applicationId,
            FileName = fileName,
            FileType = mimeType,
            FileSize = scored.LongLength,
            Fingerprint = item.Fingerprint!,
            ContentBase64 = Convert.ToBase64String(scored),
            ContentSha256 = ContentFingerprint.Compute(scored),
        };

        item.LinkApplication(applicationId, documentId, Now);
        if (repository is IAtomicBulkScoringItemRepository atomicRepository)
        {
            await atomicRepository.CreateApplicationAndDocumentAsync(
                item,
                application,
                document,
                [
                    TypedAuditEvent(request, item, BulkAuditEventTypes.BulkItemApplicationCreated, "created"),
                    TypedAuditEvent(request, item, BulkAuditEventTypes.BulkItemDocumentCreated, "created"),
                ],
                ct);
            return;
        }

        if (await applications.GetByIdAsync(applicationId, ct) is null)
            await applications.AddAsync(application, ct);
        if (!await applications.DocumentExistsAsync(documentId, ct))
            await applications.AddDocumentAsync(document, ct);
        await repository.SaveItemAsync(item, ct);
        await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemApplicationCreated, "created", ct);
        await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemDocumentCreated, "created", ct);
    }

    private async Task RemoveStagedAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        bool converted,
        CancellationToken ct)
    {
        if (!converted)
        {
            await DeleteStagedAsync(item, request, BulkContentKind.Converted, ct);
            await DeleteStagedAsync(item, request, BulkContentKind.Original, ct);
        }
    }

    private async Task DeleteStagedAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        BulkContentKind kind,
        CancellationToken ct)
    {
        var row = await repository.GetContentAsync(item.Id, kind, ct);
        if (row is null) return;
        await contentStore.DeleteAsync(row, ct);
        var eventType = kind switch
        {
            BulkContentKind.Original => BulkAuditEventTypes.BulkOriginalContentDeleted,
            BulkContentKind.Converted => BulkAuditEventTypes.BulkConvertedContentDeleted,
            _ => BulkAuditEventTypes.BulkTransientContentDeleted,
        };
        if (repository is IAtomicBulkScoringItemRepository atomicRepository)
            await atomicRepository.RemoveContentWithAuditAsync(
                item.Id,
                kind,
                TypedAuditEvent(
                    request,
                    item,
                    eventType,
                    "deleted",
                    BulkAuditBoundary.Reconciliation),
                ct);
        else
            await repository.RemoveContentAsync(item.Id, kind, ct);
    }

    private async Task<byte[]> ReadStagedAsync(BulkScoringItem item, BulkContentKind kind, CancellationToken ct)
    {
        var row = await repository.GetContentAsync(item.Id, kind, ct)
            ?? throw new InvalidOperationException($"The staged {kind.ToWire()} content for item {item.Id} is missing.");
        return await contentStore.ReadAsync(row, ct);
    }

    private async Task<bool> CancelRequestedAsync(string requestId, CancellationToken ct) =>
        (await repository.GetAsync(requestId, ct))?.CancelRequested ?? false;

    private async Task<BulkItemOutcome> CancelAsync(BulkScoringItem item, BulkScoringRequest request)
    {
        item.TransitionTo(BulkItemStatus.Cancelled, Now);
        await SaveAuditAsync(
            item,
            request,
            BulkAuditEventTypes.BulkItemCancelled,
            "cancelled",
            CancellationToken.None);
        return BulkItemOutcome.Cancelled;
    }

    private async Task<BulkItemOutcome> SkipDuplicateAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        BulkReasonCode reason,
        string message,
        CancellationToken ct)
    {
        item.TransitionTo(BulkItemStatus.Skipped, Now, reason, message);
        await SaveAuditAsync(
            item,
            request,
            BulkAuditEventTypes.BulkItemDuplicateSkipped,
            "skipped",
            ct);
        await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemSkipped, "skipped", ct);
        await DeleteStagedAsync(item, request, BulkContentKind.Original, CancellationToken.None);
        return BulkItemOutcome.Skipped;
    }

    private async Task<BulkItemOutcome> RejectConvertedAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        BulkReasonCode reason,
        CancellationToken ct)
    {
        await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkConvertedOutputQuarantined, "quarantined", ct);
        await DeleteStagedAsync(item, request, BulkContentKind.Converted, ct);
        await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkConvertedOutputRemoved, "removed", ct);
        return await FailConversionAsync(item, request, reason, ConversionMessage(reason), outputHandled: true);
    }

    private async Task<BulkItemOutcome> FailConversionAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        BulkReasonCode reason,
        string message,
        bool outputHandled = false)
    {
        await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkItemConversionFailed, "failed", CancellationToken.None);
        if (!outputHandled)
        {
            await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkConvertedOutputQuarantined, "quarantined", CancellationToken.None);
            await DeleteStagedAsync(item, request, BulkContentKind.Converted, CancellationToken.None);
            await SaveAuditAsync(item, request, BulkAuditEventTypes.BulkConvertedOutputRemoved, "removed", CancellationToken.None);
        }
        return await FailAsync(item, request, reason, message);
    }

    private async Task<BulkItemOutcome> FailAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        BulkReasonCode reason,
        string message)
    {
        // A file that failed before hand-off holds no application, so the item can simply become failed.
        if (item.Status is BulkItemStatus.Waiting or BulkItemStatus.Scoring)
            throw new InvalidOperationException($"Item {item.Id} cannot fail from {item.Status.ToWire()}.");
        item.TransitionTo(BulkItemStatus.Failed, Now, reason, message);
        await SaveAuditAsync(
            item,
            request,
            BulkAuditEventTypes.BulkItemFailed,
            "failed",
            CancellationToken.None);
        return BulkItemOutcome.Failed;
    }

    private async Task<BulkItemOutcome> RetryOrFailAsync(BulkScoringItem item, BulkScoringRequest request, BulkRequestLimits limits, TimeSpan? retryAfter)
    {
        if (BulkRetryPolicy.IsExhausted(item.Attempt, limits.MaxAttempts))
            return await FailAsync(item, request, BulkReasonCode.TransientExhausted, "The file could not be imported after several attempts.");

        item.NextAttemptAt = Now + BulkRetryPolicy.NextDelay(item.Attempt, retryAfter);
        item.TransitionTo(BulkItemStatus.Waiting, Now);
        await repository.SaveItemAsync(item);
        telemetry?.ItemRetrying(request, item, retryAfter, 0);
        return BulkItemOutcome.Retrying;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task SaveStagedAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        IReadOnlyCollection<BulkScoringItemContent> contents,
        string eventType,
        CancellationToken ct)
    {
        if (repository is IAtomicBulkScoringItemRepository atomicRepository)
        {
            try
            {
                await atomicRepository.SaveItemAndContentsWithAuditsAsync(
                    item,
                    contents,
                    [TypedAuditEvent(request, item, eventType, "staged")],
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BulkWorkerPersistenceException(ex);
            }
            return;
        }

        foreach (var content in contents)
            await repository.AddContentAsync(content, ct);
        await SaveAuditAsync(item, request, eventType, "staged", ct);
    }

    private Task SaveAuditAsync(
        BulkScoringItem item,
        BulkScoringRequest request,
        string eventType,
        string state,
        CancellationToken ct) =>
        repository is IAtomicBulkScoringItemRepository atomicRepository
            ? atomicRepository.SaveItemWithAuditAsync(
                item,
                TypedAuditEvent(request, item, eventType, state),
                ct)
            : repository.SaveItemWithAuditAsync(
                item,
                AuditEvent(request, item, eventType, state),
                ct);

    private BulkAuditEvent TypedAuditEvent(
        BulkScoringRequest request,
        BulkScoringItem item,
        string eventType,
        string state,
        BulkAuditBoundary boundary = BulkAuditBoundary.Transactional) =>
        BulkAuditWriter.Service(
            eventType,
            "bulk-scoring-worker",
            request.OwnerActorId,
            request.CorrelationId,
            Now,
            "bulk_item",
            item.Id,
            new { state, attempt = item.Attempt },
            boundary);

    private ProcessingEvent AuditEvent(
        BulkScoringRequest request,
        BulkScoringItem item,
        string eventType,
        string state) => new()
        {
            Actor = "bulk-scoring-worker",
            EventType = eventType,
            EntityType = nameof(BulkScoringItem),
            EntityId = item.Id,
            CorrelationId = request.CorrelationId,
            Timestamp = Now,
            PayloadJson = JsonSerializer.Serialize(new { factVersion = 1, state }),
        };

    private static string ConversionMessage(BulkReasonCode reason) => reason switch
    {
        BulkReasonCode.ConversionTimeout => "Converting the file took too long.",
        BulkReasonCode.LimitExceeded => "The file has more pages than the allowed maximum.",
        BulkReasonCode.ConvertedTypeInvalid => "The converted document type is invalid.",
        BulkReasonCode.ConvertedMagicInvalid => "The converted document does not have a valid PDF signature.",
        BulkReasonCode.ConvertedDocumentInvalid => "The converted PDF is structurally invalid.",
        BulkReasonCode.ConvertedTooLarge => "The converted document is larger than the allowed size.",
        BulkReasonCode.ConversionPageMismatch => "The converted document page count or order does not match the source.",
        BulkReasonCode.ConversionPageInvalid => "The converted document contains an invalid page.",
        BulkReasonCode.ConversionDimensionMismatch => "The converted document page dimensions do not match the source.",
        BulkReasonCode.ConversionSsimBelowThreshold => "The converted document does not meet the fidelity threshold.",
        BulkReasonCode.ConversionBlankPage => "The converted document contains a blank page.",
        BulkReasonCode.ConversionCroppedPage => "The converted document contains a cropped page.",
        _ => "The file could not be converted to an image-based PDF.",
    };

    private static string DeriveCandidateName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(name) ? "Unknown Applicant" : name;
    }
}
