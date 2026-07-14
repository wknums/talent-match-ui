using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Services;

public class PlatformScoringService : IPlatformScoringService
{
    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBlobStore _blobStore;
    private readonly ILogger<PlatformScoringService> _logger;

    private static readonly string PlatformEndpoint =
        Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT") ?? string.Empty;
    private static readonly int SubmitTimeoutS =
        int.TryParse(Environment.GetEnvironmentVariable("AWR_PLATFORM_SUBMIT_TIMEOUT_S"), out var s) ? s : 60;
    private static readonly int PollTimeoutS =
        int.TryParse(Environment.GetEnvironmentVariable("AWR_PLATFORM_POLL_TIMEOUT_S"), out var s) ? s : 30;
    private static readonly int DefaultPollDelayMs =
        int.TryParse(Environment.GetEnvironmentVariable("AWR_PLATFORM_DEFAULT_POLL_DELAY_MS"), out var s) ? Math.Max(5000, s) : 10000;

    private async Task<byte[]?> ResolveDocumentBytesAsync(ApplicationDocument doc, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(doc.BlobUri))
        {
            var fromBlob = await _blobStore.ReadByUriAsync(doc.BlobUri, ct);
            if (fromBlob is { Length: > 0 }) return fromBlob;
        }

        if (string.IsNullOrEmpty(doc.ContentBase64)) return null;

        try { return Convert.FromBase64String(doc.ContentBase64); }
        catch { return Encoding.UTF8.GetBytes(doc.ContentBase64); }
    }

    public PlatformScoringService(
        HttpClient http,
        IServiceScopeFactory scopeFactory,
        IBlobStore blobStore,
        ILogger<PlatformScoringService> logger)
    {
        _http = http;
        _scopeFactory = scopeFactory;
        _blobStore = blobStore;
        _logger = logger;
    }

    private static Uri PlatformUrl(string path)
    {
        if (string.IsNullOrEmpty(PlatformEndpoint))
            throw new InvalidOperationException("AWR_PLATFORM_API_ENDPOINT is not set");
        if (path.StartsWith("http://") || path.StartsWith("https://"))
            return new Uri(path);
        return new Uri($"{PlatformEndpoint}{(path.StartsWith("/") ? string.Empty : "/")}{path}");
    }

    public async Task<PlatformSubmitOutcome> SubmitBatchAsync(ScoringBatch batch, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(PlatformEndpoint))
            return new PlatformSubmitOutcome(PlatformSubmitStatus.PermanentFailure, Error: "AWR_PLATFORM_API_ENDPOINT is not set");

        using var scope = _scopeFactory.CreateScope();
        var batchRepo = scope.ServiceProvider.GetRequiredService<IScoringBatchRepository>();

        if (batch.CancelRequested)
        {
            var ids = DeserializeIds(batch.ApplicationIdsJson);
            await batchRepo.MarkCancelledAsync(batch.Id, ct);
            await batchRepo.ApplyTransitionAsync(batch.JobId, "pending", null, 0, ids.Count, ct);
            return new PlatformSubmitOutcome(PlatformSubmitStatus.Cancelled);
        }

        if (!_blobStore.IsRemote)
        {
            return new PlatformSubmitOutcome(PlatformSubmitStatus.PermanentFailure,
                Error: "Platform mode requires AWR_BLOB_STORAGE_ACCOUNT (contract §2.1).");
        }

        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var promptRepo = scope.ServiceProvider.GetRequiredService<IScoringPromptRepository>();
        var appRepo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();

        var job = await jobRepo.GetByIdAsync(batch.JobId, ct);
        if (job is null) return new PlatformSubmitOutcome(PlatformSubmitStatus.PermanentFailure, Error: $"Job {batch.JobId} not found");
        var prompt = await promptRepo.GetByIdAsync(batch.PromptVersionId, ct);
        if (prompt is null) return new PlatformSubmitOutcome(PlatformSubmitStatus.PermanentFailure, Error: $"Prompt {batch.PromptVersionId} not found");

        var jobDescriptionText = job.JobDescription ?? job.Title;
        var resolvedPrompt = prompt.PromptText.Replace("{{JOB_SPEC_TEXT}}", jobDescriptionText);

        var ids2 = DeserializeIds(batch.ApplicationIdsJson);
        var cvs = new List<object>(ids2.Count);
        foreach (var applicationId in ids2)
        {
            var documents = await appRepo.GetDocumentsAsync(applicationId, ct);
            if (documents.Count == 0)
                return new PlatformSubmitOutcome(PlatformSubmitStatus.PermanentFailure, Error: $"No documents for application {applicationId}");
            var doc = documents[0];
            var bytes = await ResolveDocumentBytesAsync(doc, ct);
            if (bytes is null || bytes.Length == 0)
                return new PlatformSubmitOutcome(PlatformSubmitStatus.PermanentFailure, Error: $"No document blob for application {applicationId}");

            var upload = await _blobStore.PutAsync(batch.JobId, applicationId, doc.Id, doc.FileName, doc.FileType ?? "application/octet-stream", bytes, ct);
            if (string.IsNullOrEmpty(upload.BlobUri))
                return new PlatformSubmitOutcome(PlatformSubmitStatus.PermanentFailure, Error: "Blob store did not return a blobUri in platform mode");

            await appRepo.SetDocumentBlobReferenceAsync(doc.Id, upload.BlobUri!, upload.Sha256, ct);

            cvs.Add(new
            {
                applicationId,
                documentId = doc.Id,
                fileName = doc.FileName,
                mimeType = doc.FileType,
                blobUri = upload.BlobUri,
                sha256 = upload.Sha256,
            });
        }

        var body = new
        {
            batchId = batch.Id,
            jobId = batch.JobId,
            promptVersionId = batch.PromptVersionId,
            runCount = batch.RunCount,
            prompt = new { kind = "inline", text = resolvedPrompt },
            cvs,
            callbackUrl = (string?)null,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, PlatformUrl("/assess/batch"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Idempotency-Key", batch.Id);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(SubmitTimeoutS));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new PlatformSubmitOutcome(PlatformSubmitStatus.Transient, Error: ex.Message);
        }

        try
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalMilliseconds ?? 0;
                return new PlatformSubmitOutcome(PlatformSubmitStatus.Transient, RetryAfterMs: (int)Math.Max(retryAfter, 5000));
            }
            if ((int)response.StatusCode >= 500)
                return new PlatformSubmitOutcome(PlatformSubmitStatus.Transient, Error: $"Platform {(int)response.StatusCode}");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return new PlatformSubmitOutcome(PlatformSubmitStatus.PermanentFailure, Error: $"Platform {(int)response.StatusCode}: {err}");
            }
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return new PlatformSubmitOutcome(PlatformSubmitStatus.Transient, Error: $"Platform {(int)response.StatusCode}: {err}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var submissionId = root.GetProperty("submissionId").GetString() ?? string.Empty;
            string? pollUrl = root.TryGetProperty("pollUrl", out var pu) ? pu.GetString() : null;
            pollUrl ??= $"/assess/batch/{submissionId}/status";
            var ecs = root.TryGetProperty("estimatedCompletionSeconds", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 30;
            var nextDelayMs = Math.Max(5000, (ecs / 2) * 1000);
            var nextPollAt = DateTime.UtcNow.AddMilliseconds(nextDelayMs);

            await batchRepo.MarkSubmittedAsync(batch.Id, submissionId, pollUrl, nextPollAt, ct);
            await batchRepo.ApplyTransitionAsync(batch.JobId, "pending", "submitted", 0, 0, ct);

            var status = response.StatusCode == HttpStatusCode.OK
                ? PlatformSubmitStatus.IdempotentHit
                : PlatformSubmitStatus.Submitted;
            return new PlatformSubmitOutcome(status, submissionId, pollUrl);
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<PlatformPollOutcome> PollBatchAsync(ScoringBatch batch, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(batch.PollUrl))
            return new PlatformPollOutcome(PlatformPollStatus.Failed, Error: "Batch has no pollUrl");

        using var scope = _scopeFactory.CreateScope();
        var batchRepo = scope.ServiceProvider.GetRequiredService<IScoringBatchRepository>();
        var appRepo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var promptRepo = scope.ServiceProvider.GetRequiredService<IScoringPromptRepository>();
        var finalizer = scope.ServiceProvider.GetRequiredService<IApplicationScoringFinalizer>();

        var ids = DeserializeIds(batch.ApplicationIdsJson);

        using var req = new HttpRequestMessage(HttpMethod.Get, PlatformUrl(batch.PollUrl));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(PollTimeoutS));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new PlatformPollOutcome(PlatformPollStatus.StillRunning, RetryAfterMs: DefaultPollDelayMs, Error: ex.Message);
        }

        try
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalMilliseconds ?? 0;
                return new PlatformPollOutcome(PlatformPollStatus.StillRunning,
                    RetryAfterMs: (int)Math.Max(retryAfter, DefaultPollDelayMs));
            }
            if (!response.IsSuccessStatusCode)
                return new PlatformPollOutcome(PlatformPollStatus.Failed, Error: $"Poll {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();

            if (status is "queued" or "running")
            {
                var retrySec = root.TryGetProperty("retryAfterSeconds", out var rA) && rA.ValueKind == JsonValueKind.Number ? rA.GetDouble() : 0;
                var retryMs = retrySec > 0 ? (int)(retrySec * 1000) : DefaultPollDelayMs;
                return new PlatformPollOutcome(PlatformPollStatus.StillRunning, RetryAfterMs: retryMs);
            }

            if (status == "cancelled")
            {
                await batchRepo.MarkCancelledAsync(batch.Id, ct);
                await batchRepo.ApplyTransitionAsync(batch.JobId, "submitted", null, 0, ids.Count, ct);
                return new PlatformPollOutcome(PlatformPollStatus.Cancelled);
            }

            if (status == "failed")
            {
                var msg = "Unknown platform failure";
                if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                {
                    msg = (errEl.TryGetProperty("message", out var m) ? m.GetString() : null)
                          ?? (errEl.TryGetProperty("code", out var c) ? c.GetString() : null)
                          ?? msg;
                }
                await batchRepo.MarkFailedAsync(batch.Id, msg, ct);
                await batchRepo.ApplyTransitionAsync(batch.JobId, "submitted", "failed", 0, ids.Count, ct);
                foreach (var aId in ids)
                {
                    try
                    {
                        var a = await appRepo.GetByIdAsync(aId, ct);
                        if (a != null) { a.Status = "ScoringFailed"; a.LastError = msg; await appRepo.UpdateAsync(a, ct); }
                    }
                    catch { /* best effort */ }
                }
                return new PlatformPollOutcome(PlatformPollStatus.Failed, Error: msg);
            }

            // status == "completed"
            await batchRepo.MarkCompletedAsync(batch.Id, json, ct);

            var job = await jobRepo.GetByIdAsync(batch.JobId, ct);
            var config = job?.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
                         ?? job?.ConfigVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            var variance = config?.VarianceThreshold ?? 15;
            var longlist = config?.LonglistThreshold ?? 70;
            var rubric = config?.RubricJson;
            var prompt = await promptRepo.GetByIdAsync(batch.PromptVersionId, ct);

            var cvsById = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object
                && resultEl.TryGetProperty("cvs", out var cvsArr) && cvsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var cv in cvsArr.EnumerateArray())
                {
                    if (cv.TryGetProperty("applicationId", out var aIdEl) && aIdEl.ValueKind == JsonValueKind.String)
                        cvsById[aIdEl.GetString()!] = cv;
                }
            }

            int completed = 0, failed = 0;
            foreach (var applicationId in ids)
            {
                if (!cvsById.TryGetValue(applicationId, out var cv))
                {
                    failed++;
                    try
                    {
                        var a = await appRepo.GetByIdAsync(applicationId, ct);
                        if (a != null) { a.Status = "ScoringFailed"; await appRepo.UpdateAsync(a, ct); }
                    }
                    catch { /* noop */ }
                    continue;
                }
                try
                {
                    var extractedCandidateName = ExtractCandidateName(cv);
                    var runs = new List<ScoringRun>();
                    if (cv.TryGetProperty("runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var r in runsEl.EnumerateArray())
                        {
                            idx++;
                            extractedCandidateName ??= ExtractCandidateName(r);
                            var parsedRunJson = r.GetRawText();
                            var run = ScoreApplicationCommandHandler.ParseSingleRunStatic(r, applicationId, prompt?.Id ?? batch.PromptVersionId, idx);
                            run.RawResponseText = parsedRunJson;
                            run.RawParsedResponseJson = parsedRunJson;
                            ScoreApplicationCommandHandler.RemapToRubricStatic(run, rubric);
                            await appRepo.AddScoringRunAsync(run, ct);
                            runs.Add(run);
                        }
                    }
                    await finalizer.FinalizeAsync(applicationId, batch.JobId, runs, batch.RunCount, variance, longlist, ct);

                    if (!string.IsNullOrWhiteSpace(extractedCandidateName))
                    {
                        var app = await appRepo.GetByIdAsync(applicationId, ct);
                        if (app != null
                            && !string.Equals(app.CandidateName?.Trim(), extractedCandidateName, StringComparison.Ordinal))
                        {
                            app.CandidateName = extractedCandidateName;
                            await appRepo.UpdateAsync(app, ct);
                        }
                    }

                    completed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Platform finalize failed for application {AppId}", applicationId);
                    try
                    {
                        var a = await appRepo.GetByIdAsync(applicationId, ct);
                        if (a != null) { a.Status = "ScoringFailed"; a.LastError = ex.Message; await appRepo.UpdateAsync(a, ct); }
                    }
                    catch { /* noop */ }
                }
            }

            await batchRepo.ApplyTransitionAsync(batch.JobId, "submitted", "completed", completed, failed, ct);
            return new PlatformPollOutcome(PlatformPollStatus.Completed);
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task CancelSubmissionAsync(ScoringBatch batch, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(batch.SubmissionId)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                PlatformUrl($"/assess/batch/{batch.SubmissionId}/cancel"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var _ = await _http.SendAsync(req, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Platform cancel HTTP failed for submissionId {Sid}", batch.SubmissionId);
        }
    }

    private static List<string> DeserializeIds(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static string? ExtractCandidateName(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null || node.ValueKind == JsonValueKind.Undefined)
            return null;

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "candidate_name", "candidateName", "candidate_full_name", "candidateFullName", "full_name", "fullName" })
            {
                if (node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var normalized = NormalizeCandidateName(value.GetString());
                    if (!string.IsNullOrWhiteSpace(normalized))
                        return normalized;
                }
            }

            foreach (var containerKey in new[] { "candidate", "candidate_info", "candidateInfo", "applicant", "person", "profile" })
            {
                if (!node.TryGetProperty(containerKey, out var container) || container.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var nestedKey in new[] { "name", "full_name", "fullName", "candidate_name", "candidateName" })
                {
                    if (container.TryGetProperty(nestedKey, out var nestedValue) && nestedValue.ValueKind == JsonValueKind.String)
                    {
                        var normalized = NormalizeCandidateName(nestedValue.GetString());
                        if (!string.IsNullOrWhiteSpace(normalized))
                            return normalized;
                    }
                }
            }

            foreach (var prop in node.EnumerateObject())
            {
                var nested = ExtractCandidateName(prop.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var nested = ExtractCandidateName(item);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static string? NormalizeCandidateName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        var collapsed = string.Join(' ', rawName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(collapsed)) return null;

        var lower = collapsed.ToLowerInvariant();
        if (lower is "unknown" or "n/a" or "na" or "none" or "null" or "undefined" or "not provided" or "not available" or "candidate")
            return null;

        return collapsed;
    }
}
