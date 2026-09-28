using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class UploadCoordinatorTests
{
    [Fact]
    public async Task CoordinatorStagesAndUploadsSixtySevenFilesBeyondConcurrencyWindow()
    {
        var files = Enumerable.Range(0, 67)
            .Select(index => new CountingBrowserFile(
                $"file-{index}.md",
                "text/markdown",
                System.Text.Encoding.UTF8.GetBytes($"content-{index}")))
            .ToArray();
        var items = Enumerable.Range(0, files.Length)
            .Select(index => Item($"item-{index}", index, "waiting"))
            .ToArray();
        UploadSessionDetailDto? session = null;
        var contentRequests = 0;
        var allUploaded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path == "/api/jobs/job-1/upload-sessions")
            {
                var create = JsonSerializer.Deserialize<CreateUploadSessionRequest>(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                items = items.Select((item, index) => item with
                {
                    OccurrenceKey = create.Items[index].OccurrenceKey,
                }).ToArray();
                session = Session("active", items);
                return JsonResponse(session, HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/content"))
            {
                if (Interlocked.Increment(ref contentRequests) == files.Length)
                    allUploaded.TrySetResult();
                var id = path.Split('/')[5];
                var item = items.Single(candidate => candidate.Id == id) with
                {
                    Status = "succeeded",
                    CompletedAt = DateTime.UtcNow,
                };
                return JsonResponse(item);
            }
            if (request.Method == HttpMethod.Get && path == "/api/upload-sessions/session-1")
                return JsonResponse(session!);
            if (path.EndsWith("/heartbeat"))
                return JsonResponse(ToSummary(session!));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        await using var coordinator = new UploadCoordinator(new ApiClient(http));

        await coordinator.StartAsync("job-1", false, files);

        files.Should().OnlyContain(file => file.OpenCount == 1);
        await allUploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        contentRequests.Should().Be(files.Length);
    }

    [Fact]
    public void Scheduler_ThrottlesInSelectionOrderWithoutRejecting()
    {
        var scheduler = new UploadCapacityScheduler(2, 100);
        scheduler.TryReserve("first", 60).Should().BeTrue();
        scheduler.TryReserve("second", 50).Should().BeFalse();
        scheduler.IsThrottled("second").Should().BeTrue();
        scheduler.TryReserve("third", 40).Should().BeFalse("a later item may not bypass an earlier waiting item");

        scheduler.Release("first");
        scheduler.TryReserve("second", 50).Should().BeTrue();
        scheduler.TryReserve("third", 40).Should().BeTrue();
        scheduler.ActiveRawBytes.Should().Be(90);
        scheduler.ActiveCount.Should().Be(2);
    }

    [Fact]
    public void Scheduler_ReleasesBothPermitsAfterFailure()
    {
        var scheduler = new UploadCapacityScheduler(4, 104_857_600);
        scheduler.TryReserve("item", 4_194_304).Should().BeTrue();
        scheduler.Release("item");
        scheduler.ActiveCount.Should().Be(0);
        scheduler.ActiveRawBytes.Should().Be(0);
    }

    [Fact]
    public void Scheduler_BoundsOneHundredFiles()
    {
        var scheduler = new UploadCapacityScheduler(4, 104_857_600);
        Enumerable.Range(0, 100)
            .Count(index => scheduler.TryReserve(index.ToString(), 4_194_304))
            .Should().Be(4);
        scheduler.ActiveRawBytes.Should().Be(16_777_216);
    }

    [Fact]
    public async Task Coordinator_ContinuesAfterOneItemRequestFailsAndReloadsTerminalState()
    {
        var firstWaiting = Item("item-1", 0, "waiting");
        var first = firstWaiting with
        {
            Status = "failed",
            AttemptCount = 1,
            OutcomeCode = "upload_failed",
            OutcomeMessage = "The file could not be uploaded.",
            CompletedAt = DateTime.UtcNow,
        };
        var second = Item("item-2", 1, "waiting");
        UploadSessionDetailDto? active = null;
        var secondAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path == "/api/jobs/job-1/upload-sessions")
            {
                var create = JsonSerializer.Deserialize<CreateUploadSessionRequest>(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                firstWaiting = firstWaiting with { OccurrenceKey = create.Items[0].OccurrenceKey };
                first = first with { OccurrenceKey = create.Items[0].OccurrenceKey };
                second = second with { OccurrenceKey = create.Items[1].OccurrenceKey };
                active = Session("active", [first, second]);
                return JsonResponse(
                    Session("active", [firstWaiting, second]),
                    HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/item-1/content"))
                return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
            if (request.Method == HttpMethod.Post && path.EndsWith("/item-2/content"))
            {
                secondAttempted.TrySetResult();
                return JsonResponse(second with
                {
                    Status = "succeeded",
                    CompletedAt = DateTime.UtcNow,
                });
            }
            if (request.Method == HttpMethod.Get && path == "/api/upload-sessions/session-1")
                return JsonResponse(secondAttempted.Task.IsCompleted
                    ? Session("completed",
                    [
                        first,
                        second with { Status = "succeeded", CompletedAt = DateTime.UtcNow },
                    ])
                    : active!);
            if (path.EndsWith("/heartbeat"))
                return JsonResponse(ToSummary(active!));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        await using var coordinator = new UploadCoordinator(new ApiClient(http));

        await coordinator.StartAsync(
            "job-1",
            allowDuplicates: false,
            [
                new BrowserFile("first.pdf", "application/pdf", "a"u8.ToArray()),
                new BrowserFile("second.pdf", "application/pdf", "b"u8.ToArray()),
            ]);
        await secondAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Sessions.Single().Items
            .Single(item => item.Id == "item-1").Status.Should().Be("failed");
    }

    [Fact]
    public async Task Coordinator_RecordsFourTransportFailuresAsDurableRetryExhaustion()
    {
        var current = Item("item-1", 0, "waiting");
        var contentRequests = 0;
        UpdateUploadItemStatusRequest? terminalRequest = null;
        var terminalRecorded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path == "/api/jobs/job-1/upload-sessions")
            {
                var create = JsonSerializer.Deserialize<CreateUploadSessionRequest>(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                current = current with { OccurrenceKey = create.Items[0].OccurrenceKey };
                return JsonResponse(Session("active", [current]), HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/item-1/content"))
            {
                contentRequests++;
                throw new HttpRequestException("The request never reached the server.");
            }
            if (request.Method == HttpMethod.Patch && path.EndsWith("/item-1/status"))
            {
                var update = JsonSerializer.Deserialize<UpdateUploadItemStatusRequest>(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                current = current with
                {
                    Status = update.Status,
                    OutcomeCode = update.OutcomeCode,
                    OutcomeMessage = update.OutcomeMessage,
                    NextRetryAt = update.NextRetryAt,
                    CompletedAt = update.Status == "failed" ? DateTime.UtcNow : null,
                    ConcurrencyVersion = current.ConcurrencyVersion + 1,
                };
                if (update.Status == "failed")
                {
                    terminalRequest = update;
                    terminalRecorded.TrySetResult();
                }
                return JsonResponse(current);
            }
            if (request.Method == HttpMethod.Get && path == "/api/upload-sessions/session-1")
                return JsonResponse(Session(current.Status == "failed" ? "completed" : "active", [current]));
            if (path.EndsWith("/heartbeat"))
                return JsonResponse(ToSummary(Session("active", [current])));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        await using var coordinator = new UploadCoordinator(new ApiClient(http));

        await coordinator.StartAsync(
            "job-1",
            allowDuplicates: false,
            [new BrowserFile("candidate.pdf", "application/pdf", "a"u8.ToArray())]);
        await terminalRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        contentRequests.Should().Be(4);
        terminalRequest.Should().NotBeNull();
        terminalRequest!.Status.Should().Be("failed");
        terminalRequest.OutcomeCode.Should().Be("retry_exhausted");
        terminalRequest.TransportAttemptCount.Should().Be(4);
        coordinator.Sessions.Single().Items.Single().Status.Should().Be("failed");
    }

    private static UploadItemDto Item(
        string id,
        int ordinal,
        string status,
        string? outcomeCode = null)
    {
        var now = DateTime.UtcNow;
        return new(
            id,
            "session-1",
            Guid.NewGuid(),
            ordinal,
            $"{id}.pdf",
            "application/pdf",
            1,
            status,
            status == "waiting" ? 0 : 1,
            null,
            null,
            outcomeCode,
            outcomeCode is null ? null : "The file could not be uploaded.",
            null,
            now,
            now,
            status is "failed" or "succeeded" ? now : null,
            1);
    }

    private static UploadSessionDetailDto Session(
        string status,
        IReadOnlyList<UploadItemDto> items)
    {
        var terminal = items.Count(item =>
            item.Status is "succeeded" or "skipped_duplicate" or "failed" or "interrupted");
        var now = DateTime.UtcNow;
        return new(
            "session-1",
            "job-1",
            status,
            false,
            new(1, 4_194_304, 4_194_304),
            new(
                items.Count,
                items.Count(item => item.Status is "waiting" or "throttled"),
                items.Count(item => item.Status is "uploading" or "retrying"),
                items.Count(item => item.Status == "succeeded"),
                0,
                items.Count(item => item.Status == "failed"),
                0,
                terminal),
            items.Count == 0 ? 0 : terminal * 100 / items.Count,
            "correlation-1",
            now,
            now,
            now,
            status == "completed" ? now : null,
            1,
            items);
    }

    private static UploadSessionSummaryDto ToSummary(UploadSessionDetailDto detail) =>
        new(
            detail.Id,
            detail.JobId,
            detail.Status,
            detail.AllowDuplicates,
            detail.Limits,
            detail.Counts,
            detail.ProgressPercent,
            detail.CorrelationId,
            detail.CreatedAt,
            detail.StartedAt,
            detail.LastHeartbeatAt,
            detail.CompletedAt,
            detail.ConcurrencyVersion);

    private static HttpResponseMessage JsonResponse<T>(
        T value,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    private sealed class RoutingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private sealed class BrowserFile(
        string name,
        string contentType,
        byte[] content) : IBrowserFile
    {
        public string Name { get; } = name;
        public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow;
        public long Size => content.LongLength;
        public string ContentType { get; } = contentType;
        public Stream OpenReadStream(
            long maxAllowedSize = 512000,
            CancellationToken cancellationToken = default) =>
            content.LongLength > maxAllowedSize
                ? throw new IOException("File exceeds the allowed size.")
                : new MemoryStream(content, writable: false);
    }

    private sealed class CountingBrowserFile(
        string name,
        string contentType,
        byte[] content) : IBrowserFile
    {
        public int OpenCount { get; private set; }
        public string Name { get; } = name;
        public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow;
        public long Size => content.LongLength;
        public string ContentType { get; } = contentType;

        public Stream OpenReadStream(
            long maxAllowedSize = 512000,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return content.LongLength > maxAllowedSize
                ? throw new IOException("File exceeds the allowed size.")
                : new MemoryStream(content, writable: false);
        }
    }
}
