using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public sealed class BulkScoringContentEndpointsTests
{
    private const string Candidates = "https://contoso.sharepoint.com/sites/hr/Shared Documents/Candidates";
    private const string CredentialUri = "https://storage.invalid/bulk/request/item/original?sv=1&sig=do-not-disclose";
    private static readonly byte[] Original = "exact original source bytes"u8.ToArray();
    private static readonly byte[] Converted = "%PDF-exact-converted-default"u8.ToArray();

    [Fact]
    public async Task Converted_content_is_the_default_view_and_each_original_download_streams_exact_bytes_without_storage_credentials()
    {
        var store = new RecordingContentStore(Original);
        var applicationId = Guid.NewGuid().ToString();
        var documentId = Guid.NewGuid().ToString();
        var application = new TalentMatch.Domain.Entities.Application
        {
            Id = applicationId,
            JobId = "job-1",
            CandidateRef = "candidate-content-version",
            CandidateName = "Candidate",
            Status = "Queued",
        };
        var document = new ApplicationDocument
        {
            Id = documentId,
            ApplicationId = applicationId,
            FileName = "candidate.pdf",
            FileType = "application/pdf",
            FileSize = Converted.LongLength,
            Fingerprint = "original-fingerprint",
            ContentBase64 = Convert.ToBase64String(Converted),
        };
        var applications = new Mock<IApplicationRepository>(MockBehavior.Strict);
        applications.Setup(r => r.GetByIdAsync(applicationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(application);
        applications.Setup(r => r.GetDocumentsAsync(applicationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([document]);
        using var factory = Factory(store, services =>
        {
            services.RemoveAll<IApplicationRepository>();
            services.AddSingleton(applications.Object);
        });
        using var client = await factory.LoginAsync("recruiter", "test123");
        var seeded = await SeedConvertedAsync(factory, applicationId, documentId);

        var pageResponse = await client.GetAsync($"/api/bulk-scoring-requests/{seeded.RequestId}/items?pageSize=100");
        var pageText = await pageResponse.Content.ReadAsStringAsync();
        var page = JsonDocument.Parse(pageText).RootElement;
        var item = page.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("itemId").GetString() == seeded.ItemId);
        item.GetProperty("scoredVersion").GetString().Should().Be("converted");
        item.GetProperty("hasRetainedOriginal").GetBoolean().Should().BeTrue();
        item.GetProperty("pageCount").GetInt32().Should().Be(2);

        var viewed = await client.GetAsync(
            $"/api/applications/{seeded.ApplicationId}/documents/{seeded.DocumentId}/content");
        viewed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewed.Content.ReadAsByteArrayAsync()).Should().Equal(Converted);

        for (var call = 1; call <= 2; call++)
        {
            var original = await client.GetAsync(
                $"/api/bulk-scoring-requests/{seeded.RequestId}/items/{seeded.ItemId}/original");
            original.StatusCode.Should().Be(HttpStatusCode.OK);
            original.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            original.Content.Headers.ContentDisposition.FileNameStar.Should().Be("candidate.docx");
            original.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            original.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
            (await original.Content.ReadAsByteArrayAsync()).Should().Equal(Original);
            original.Headers.ToString().Should().NotContain("sig=").And.NotContain("storage.invalid");
        }

        store.ReadCalls.Should().Be(2, "authorization and content resolution happen for every download call");
        pageText.ToLowerInvariant().Should().NotContain("bloburi")
            .And.NotContain("storage.invalid")
            .And.NotContain("sig=");
    }

    [Fact]
    public async Task Missing_item_content_request_and_lost_job_access_have_one_non_disclosing_outcome_and_never_read_storage()
    {
        var store = new RecordingContentStore(Original);
        var job = new Job { Id = "job-1", Title = "Job" };
        var visible = true;
        var jobs = new Mock<IJobRepository>(MockBehavior.Strict);
        jobs.Setup(r => r.GetByIdWithoutApplicationsAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => visible ? job : null);
        jobs.Setup(r => r.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => visible ? job : null);

        using var factory = Factory(store, services =>
        {
            services.RemoveAll<IJobRepository>();
            services.AddSingleton(jobs.Object);
        });
        using var client = await factory.LoginAsync("recruiter", "test123");
        var seeded = await SeedRequestAsync(factory, includeOriginal: true);

        var authorized = await client.GetAsync(
            $"/api/bulk-scoring-requests/{seeded.RequestId}/items/{seeded.ItemId}/original");
        authorized.StatusCode.Should().Be(HttpStatusCode.OK);
        store.ReadCalls.Should().Be(1);

        var missingItem = await OutcomeAsync(await client.GetAsync(
            $"/api/bulk-scoring-requests/{seeded.RequestId}/items/missing-item/original"));
        var missingContent = await OutcomeAsync(await client.GetAsync(
            $"/api/bulk-scoring-requests/{seeded.RequestId}/items/{seeded.ItemWithoutContentId}/original"));
        var missingRequest = await OutcomeAsync(await client.GetAsync(
            $"/api/bulk-scoring-requests/missing-request/items/{seeded.ItemId}/original"));

        visible = false;
        var unauthorized = await OutcomeAsync(await client.GetAsync(
            $"/api/bulk-scoring-requests/{seeded.RequestId}/items/{seeded.ItemId}/original"));

        new[] { missingItem, missingContent, missingRequest, unauthorized }
            .Should().OnlyContain(o => o.Status == HttpStatusCode.NotFound && o.Code == "not_found");
        new[] { missingContent, missingRequest, unauthorized }
            .Select(o => o.Message)
            .Should().OnlyContain(message => message == missingItem.Message,
                "missing and unauthorized content must not reveal which lookup failed");
        store.ReadCalls.Should().Be(1, "lost job access must be checked before retained storage is read");
        string.Join(' ', missingItem.Raw, missingContent.Raw, missingRequest.Raw, unauthorized.Raw)
            .ToLowerInvariant().Should().NotContain("candidate")
            .And.NotContain("storage.invalid")
            .And.NotContain("sig=");
    }

    private static BulkScoringRequestsEndpointsTests.BulkFactory Factory(
        RecordingContentStore store,
        Action<IServiceCollection>? configure = null) =>
        new("mock", configure: services =>
        {
            services.RemoveAll<IBulkContentStore>();
            services.AddSingleton<IBulkContentStore>(store);
            configure?.Invoke(services);
        });

    private static async Task<SeededContent> SeedConvertedAsync(
        BulkScoringRequestsEndpointsTests.BulkFactory factory,
        string applicationId,
        string documentId)
    {
        var seeded = await SeedRequestAsync(factory, includeOriginal: true);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.BulkScoringItems.SingleAsync(i => i.Id == seeded.ItemId);
        item.ScoredVersion = BulkScoredVersion.Converted;
        item.OriginalPageCount = 2;
        item.ConvertedPageCount = 2;
        item.ConvertedSizeBytes = Converted.LongLength;
        item.SourceStoredAt = DateTime.UtcNow;
        item.ConvertedStoredAt = DateTime.UtcNow;
        item.LinkApplication(applicationId, documentId, DateTime.UtcNow);

        db.BulkScoringItemContents.Add(new BulkScoringItemContent
        {
            ItemId = item.Id,
            Kind = BulkContentKind.Converted,
            ContentBase64 = Convert.ToBase64String(Converted),
            SizeBytes = Converted.LongLength,
            Sha256 = "converted-sha",
        });
        await db.SaveChangesAsync();
        return seeded with { ApplicationId = applicationId, DocumentId = documentId };
    }

    private static async Task<SeededContent> SeedRequestAsync(
        BulkScoringRequestsEndpointsTests.BulkFactory factory,
        bool includeOriginal)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Username == "recruiter");
        var request = new BulkScoringRequest
        {
            Id = Guid.NewGuid().ToString(),
            JobId = "job-1",
            OwnerActorId = owner.Id,
            SourceKind = BulkSourceKind.SharePoint,
            SourceTenantId = "tenant",
            SourceDriveId = "drive",
            SourceFolderItemId = "folder",
            SourceFolderName = "Candidates",
            SourceFolderPath = "/Candidates",
            SourceWebUrl = Candidates,
            DiscoveryStatus = BulkDiscoveryStatus.Complete,
            DiscoveredCount = 2,
            IncludedItemCount = 2,
            WaitingCount = 2,
            MaxFilesPerRequest = 30_000,
            MaxBytesPerFile = 8_388_608,
            MaxTotalBytes = 53_687_091_200,
            DiscoveryStepMaxFiles = 1_000,
            DiscoveryStepMaxEntries = 10_000,
            DiscoveryStepMaxSeconds = 45,
            RasterDpi = 150,
        };
        request.Confirm(true, true, null, DateTime.UtcNow);
        var retained = NewItem(request.Id, "candidate.docx", 0);
        var withoutContent = NewItem(request.Id, "other.docx", 1);
        db.BulkScoringRequests.Add(request);
        db.BulkScoringItems.AddRange(retained, withoutContent);
        if (includeOriginal) db.BulkScoringItemContents.Add(OriginalRow(retained.Id));
        await db.SaveChangesAsync();
        return new(request.Id, retained.Id, withoutContent.Id, string.Empty, string.Empty);
    }

    private static BulkScoringItem NewItem(string requestId, string name, int ordinal) => new()
    {
        RequestId = requestId,
        Ordinal = ordinal,
        SourceDriveId = "drive",
        SourceDriveItemId = $"item-{ordinal}",
        SourceETag = "etag",
        FileName = name,
        SourcePath = "/",
        MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        SizeBytes = Original.LongLength,
    };

    private static BulkScoringItemContent OriginalRow(string itemId) => new()
    {
        ItemId = itemId,
        Kind = BulkContentKind.Original,
        BlobUri = CredentialUri,
        SizeBytes = Original.LongLength,
        Sha256 = "original-sha",
    };

    private static async Task<FailureOutcome> OutcomeAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(raw).RootElement;
        return new(
            response.StatusCode,
            json.GetProperty("code").GetString(),
            json.GetProperty("detail").GetString(),
            raw);
    }

    private sealed record SeededContent(
        string RequestId,
        string ItemId,
        string ItemWithoutContentId,
        string ApplicationId,
        string DocumentId);

    private sealed record FailureOutcome(
        HttpStatusCode Status,
        string? Code,
        string? Message,
        string Raw);

    private sealed class RecordingContentStore(byte[] original) : IBulkContentStore
    {
        public int ReadCalls { get; private set; }

        public Task<byte[]> ReadAsync(
            BulkScoringItemContent content,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(original.ToArray());
        }

        public Task<BulkScoringItemContent> StageAsync(
            string requestId,
            string itemId,
            BulkContentKind kind,
            string fileName,
            string mimeType,
            byte[] bytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            BulkScoringItemContent content,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByRequestAsync(
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
