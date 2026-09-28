using System.Net;
using System.Net.Http.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class UploadStatusSurfaceTests : BunitContext
{
    [Fact]
    public async Task Surface_IsCollapsedAndExpandsTheRequestedSession()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var session = CreateSession();
        var api = new ApiClient(new HttpClient(new SessionHandler(session))
        {
            BaseAddress = new Uri("http://localhost"),
        });
        var coordinator = new UploadCoordinator(api);
        Services.AddSingleton(api);
        Services.AddSingleton(coordinator);

        var cut = Render<UploadStatusSurface>();

        cut.WaitForAssertion(() => cut.Find("summary").TextContent.Should().Contain("Upload activity (1)"));
        cut.Find("details#upload-activity").HasAttribute("open").Should().BeFalse();
        cut.Find("[aria-live='polite']").Should().NotBeNull();

        await cut.InvokeAsync(() => coordinator.RequestDetails(session.Id));

        cut.WaitForAssertion(() =>
        {
            cut.Find("details#upload-activity").HasAttribute("open").Should().BeTrue();
            cut.Find($"details[data-session-id='{session.Id}']")
                .HasAttribute("open").Should().BeTrue();
            JSInterop.Invocations.Should().Contain(invocation =>
                invocation.Identifier == "Blazor._internal.domWrapper.focus");
        });
    }

    [Fact]
    public async Task PipelineUploads_ShowsLatestSubmissionProgressAndRequestsDrillDown()
    {
        var session = CreateSession();
        var olderSession = session with
        {
            Id = "session-older",
            Counts = new(9, 9, 0, 0, 0, 0, 0, 0),
            ProgressPercent = 0,
            CreatedAt = session.CreatedAt.AddMinutes(-5),
            Items = [],
        };
        var api = new ApiClient(new HttpClient(new SessionHandler(olderSession, session))
        {
            BaseAddress = new Uri("http://localhost"),
        });
        var coordinator = new UploadCoordinator(api);
        Services.AddSingleton(api);
        Services.AddSingleton(coordinator);
        await coordinator.RefreshAsync();
        string? requestedSessionId = null;
        coordinator.DetailsRequested += sessionId => requestedSessionId = sessionId;

        var cut = Render<PipelineVisualizer>(parameters => parameters
            .Add(component => component.JobId, session.JobId)
            .Add(component => component.Applications, []));

        cut.Find(".pipeline-upload-stage").TextContent.Should().Contain("Uploads (3 remaining)");
        var progress = cut.Find("[role='progressbar']");
        progress.GetAttribute("aria-valuenow").Should().Be("3");
        progress.GetAttribute("aria-valuetext")
            .Should().Be("1 waiting or uploading; 2 failed or retrying");
        cut.Find(".upload-progress-pending").GetAttribute("style").Should().Contain("width: 7.692%");
        cut.Find(".upload-progress-attention").GetAttribute("style").Should().Contain("width: 15.385%");

        var uploadAction = cut.Find(".pipeline-upload-link");
        uploadAction.TagName.Should().Be("BUTTON");
        uploadAction.Click();

        requestedSessionId.Should().Be(session.Id);
        uploadAction.HasAttribute("href").Should().BeFalse();
    }

    private static UploadSessionDetailDto CreateSession()
    {
        var createdAt = DateTime.UtcNow;
        var items = new[]
        {
            CreateItem("item-waiting", 0, "waiting", createdAt),
            CreateItem("item-retrying", 1, "retrying", createdAt),
            CreateItem("item-failed", 2, "failed", createdAt),
            CreateItem("item-succeeded", 3, "succeeded", createdAt),
        };
        return new UploadSessionDetailDto(
            "session-1",
            "job-1",
            "active",
            false,
            new(4, 4_194_304, 104_857_600),
            new(4, 1, 1, 1, 0, 1, 0, 2),
            50,
            "correlation-1",
            createdAt,
            createdAt,
            createdAt,
            null,
            3,
            items);
    }

    private static UploadItemDto CreateItem(
        string id,
        int ordinal,
        string status,
        DateTime createdAt) =>
        new(
            id,
            "session-1",
            Guid.NewGuid(),
            ordinal,
            $"{id}.pdf",
            "application/pdf",
            1024,
            status,
            status is "retrying" or "failed" or "succeeded" ? 1 : 0,
            null,
            status == "succeeded" ? "application-1" : null,
            status == "failed" ? "retry_exhausted" : null,
            status == "failed" ? "Upload failed after retrying." : null,
            status == "retrying" ? createdAt.AddSeconds(1) : null,
            createdAt,
            createdAt,
            status is "failed" or "succeeded" ? createdAt : null,
            1);

    private sealed class SessionHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<UploadSessionDetailDto> sessions;

        public SessionHandler(params UploadSessionDetailDto[] sessions) =>
            this.sessions = sessions;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = path switch
            {
                "/api/upload-sessions" => JsonResponse(sessions.Select(ToSummary).ToArray()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            var detail = sessions.FirstOrDefault(session =>
                path == $"/api/upload-sessions/{session.Id}");
            if (detail is not null)
                response = JsonResponse(detail);
            return Task.FromResult(response);
        }

        private static UploadSessionSummaryDto ToSummary(UploadSessionDetailDto session) =>
            new(
                session.Id,
                session.JobId,
                session.Status,
                session.AllowDuplicates,
                session.Limits,
                session.Counts,
                session.ProgressPercent,
                session.CorrelationId,
                session.CreatedAt,
                session.StartedAt,
                session.LastHeartbeatAt,
                session.CompletedAt,
                session.ConcurrencyVersion);

        private static HttpResponseMessage JsonResponse<T>(T value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
