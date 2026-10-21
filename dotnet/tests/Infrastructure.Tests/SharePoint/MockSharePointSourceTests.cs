using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.SharePoint;

namespace TalentMatch.Infrastructure.Tests.SharePoint;

public sealed class MockSharePointSourceTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";
    private const string Large = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Large";
    private const string OneDriveCandidates = "https://tenant-a-my.sharepoint.com/personal/alex_tenant_a/Documents/Candidates";
    private const string OneDriveShared = "https://tenant-a-my.sharepoint.com/personal/sam_tenant_a/Documents/SharedWithRecruiter";
    private const string OneDrivePrivate = "https://tenant-a-my.sharepoint.com/personal/sam_tenant_a/Documents/Private";

    private static readonly SourceCallContext Recruiter = new(SourcePhase.Interactive, "fixture-account-recruiter");

    private sealed class StubAccess : ISharePointAccessContext
    {
        public string SourceTenantId => "t";
        public Task<string> GetGraphTokenAsync(SourceCallContext context, CancellationToken cancellationToken = default) => Task.FromResult("token");
    }

    private static MockSharePointSource Create() => new(new StubAccess());

    private static async Task<(List<SourceFile> Files, int Scanned, int Inaccessible, int Pages)> ReadAll(
        MockSharePointSource source, SourceFolder folder, SourceCallContext? context = null)
    {
        var files = new List<SourceFile>();
        string? cursor = null;
        int scanned = 0, inaccessible = 0, pages = 0;
        while (true)
        {
            var page = await source.ReadNextPageAsync(folder, cursor, context ?? Recruiter);
            files.AddRange(page.Files);
            scanned += page.EntriesScanned;
            inaccessible += page.InaccessibleCount;
            pages++;
            if (page.Exhausted) break;
            cursor = page.NextCursor;
        }
        return (files, scanned, inaccessible, pages);
    }

    [Fact]
    public async Task Resolves_sharepoint_and_onedrive_folders_with_their_source_kind()
    {
        var source = Create();

        (await source.ResolveFolderAsync(Candidates, Recruiter)).Kind.Should().Be(BulkSourceKind.SharePoint);
        var onedrive = await source.ResolveFolderAsync(OneDriveCandidates, Recruiter);
        onedrive.Kind.Should().Be(BulkSourceKind.OneDriveBusiness);
        onedrive.Path.Should().Be("/Candidates");
    }

    [Fact]
    public async Task Unknown_folders_are_not_found_and_non_folders_are_rejected()
    {
        var source = Create();

        await source.Invoking(s => s.ResolveFolderAsync(Candidates + "/Nope", Recruiter)).Should().ThrowAsync<SourceNotFoundException>();
        await source.Invoking(s => s.ResolveFolderAsync("https://other.example.com/x", Recruiter)).Should().ThrowAsync<SourceNotFoundException>();
        await source.Invoking(s => s.ResolveFolderAsync("not a url", Recruiter)).Should().ThrowAsync<UnsupportedSourceException>();
    }

    [Fact]
    public async Task Enumeration_lists_nested_files_and_counts_but_never_names_inaccessible_entries()
    {
        var source = Create();
        var folder = await source.ResolveFolderAsync(Candidates, Recruiter);

        var (files, scanned, inaccessible, _) = await ReadAll(source, folder);

        files.Select(f => f.Name).Should().Contain(["alice-1page.pdf", "bob-2page.pdf", "carla.docx", "eli.txt", "corrupt.pdf", "bob-copy.pdf"]);
        files.Count(f => f.Name == "alice-1page.pdf").Should().Be(2, "a nested folder holds a same-named file");
        files.Should().Contain(f => f.Path == "/Archive" && f.Name == "alice-1page.pdf");
        files.Select(f => f.Name).Should().NotContain("secret.pdf");
        files.Select(f => f.Path).Should().NotContain(p => p.Contains("Confidential"));
        inaccessible.Should().Be(1);
        scanned.Should().Be(files.Count + inaccessible + 1, "files + the unreadable folder + the Archive folder");
        files.Single(f => f.Name == "oversized.pdf").SizeBytes.Should().Be(9_437_184);
    }

    [Fact]
    public async Task Onedrive_access_follows_the_signed_in_users_permissions()
    {
        var source = Create();

        var shared = await source.ResolveFolderAsync(OneDriveShared, Recruiter);
        (await ReadAll(source, shared)).Files.Should().ContainSingle(f => f.Name == "ines.pdf");
        await source.Invoking(s => s.ResolveFolderAsync(OneDrivePrivate, Recruiter)).Should().ThrowAsync<SourceAccessDeniedException>();

        var sam = new SourceCallContext(SourcePhase.Interactive, "fixture-account-sam");
        var viaOwner = await source.ResolveFolderAsync(OneDrivePrivate, sam);
        (await ReadAll(source, viaOwner, sam)).Files.Should().ContainSingle(f => f.Name == "jon.pdf");
    }

    [Fact]
    public async Task Revoked_access_blocks_listing_and_download_in_every_phase()
    {
        var source = Create();
        var folder = await source.ResolveFolderAsync(Candidates, Recruiter);
        var file = (await ReadAll(source, folder)).Files.First(f => f.Name == "bob-2page.pdf");
        var background = new SourceCallContext(SourcePhase.Background, "fixture-account-recruiter", "req-1");

        (await source.DownloadAsync(file.DriveId, file.ItemId, 1_000_000, background)).Should().NotBeEmpty();

        source.Revoke("fixture-account-recruiter", file.ItemId);

        await source.Invoking(s => s.DownloadAsync(file.DriveId, file.ItemId, 1_000_000, background))
            .Should().ThrowAsync<SourceAccessDeniedException>();
        await source.Invoking(s => s.GetItemMetadataAsync(file.DriveId, file.ItemId, background))
            .Should().ThrowAsync<SourceAccessDeniedException>();
        (await ReadAll(source, folder)).Files.Should().NotContain(f => f.ItemId == file.ItemId);

        source.Restore("fixture-account-recruiter", file.ItemId);
        (await source.DownloadAsync(file.DriveId, file.ItemId, 1_000_000, background)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Etag_changes_and_deleted_files_are_observable()
    {
        var source = Create();
        var folder = await source.ResolveFolderAsync(Candidates, Recruiter);
        var file = (await ReadAll(source, folder)).Files.First(f => f.Name == "cv.md" || f.Name == "dana.md");

        var before = await source.GetItemMetadataAsync(file.DriveId, file.ItemId, Recruiter);
        before!.ETag.Should().Be(file.ETag);

        source.ChangeItem(file.ItemId);
        (await source.GetItemMetadataAsync(file.DriveId, file.ItemId, Recruiter))!.ETag.Should().NotBe(before.ETag);

        source.DeleteItem(file.ItemId);
        (await source.GetItemMetadataAsync(file.DriveId, file.ItemId, Recruiter)).Should().BeNull();
        await source.Invoking(s => s.DownloadAsync(file.DriveId, file.ItemId, 1_000_000, Recruiter)).Should().ThrowAsync<SourceNotFoundException>();
    }

    [Fact]
    public async Task Download_is_bounded_by_the_limit_and_ancestors_reach_the_drive_root()
    {
        var source = Create();
        var folder = await source.ResolveFolderAsync(Candidates, Recruiter);
        var files = (await ReadAll(source, folder)).Files;
        var oversized = files.Single(f => f.Name == "oversized.pdf");
        var nested = files.Single(f => f.Path == "/Archive");

        await source.Invoking(s => s.DownloadAsync(oversized.DriveId, oversized.ItemId, 8_388_608, Recruiter))
            .Should().ThrowAsync<SourceLimitExceededException>();
        (await source.GetAncestorItemIdsAsync(nested.DriveId, nested.ItemId, Recruiter))
            .Should().Equal(nested.ItemId, "sp-nested", "sp-candidates", "sp-root");
    }

    [Fact]
    public async Task Docx_and_markdown_convert_to_pdf_but_text_does_not_and_the_fallback_can_be_switched_off()
    {
        var source = Create();
        var files = (await ReadAll(source, await source.ResolveFolderAsync(Candidates, Recruiter))).Files;
        var docx = files.Single(f => f.Name == "carla.docx");
        var txt = files.Single(f => f.Name == "eli.txt");

        source.CanConvertToPdf(".docx").Should().BeTrue();
        source.CanConvertToPdf(".txt").Should().BeFalse();
        (await source.ConvertToPdfAsync(docx.DriveId, docx.ItemId, 8_388_608, Recruiter)).Take(4).Should().Equal("%PDF"u8.ToArray());
        await source.Invoking(s => s.ConvertToPdfAsync(txt.DriveId, txt.ItemId, 8_388_608, Recruiter)).Should().ThrowAsync<UnsupportedSourceException>();

        source.ConvertDocxAndMarkdown = false;
        source.CanConvertToPdf(".docx").Should().BeFalse();
    }

    [Fact]
    public async Task Large_tree_is_deterministic_and_resumable_with_the_documented_size()
    {
        var source = Create();
        var folder = await source.ResolveFolderAsync(Large, Recruiter);

        var first = await ReadAll(source, folder);
        var second = await ReadAll(Create(), await Create().ResolveFolderAsync(Large, Recruiter));

        first.Files.Count(f => f.Name.EndsWith(".pdf")).Should().Be(2_500);
        first.Files.Count(f => f.Name.EndsWith(".bin")).Should().Be(20_000);
        first.Scanned.Should().Be(22_525, "22,500 files and 25 subfolders");
        first.Files.Select(f => f.ItemId).Should().OnlyHaveUniqueItems();
        second.Files.Select(f => f.ItemId).Should().Equal(first.Files.Select(f => f.ItemId));
    }

    [Fact]
    public async Task An_expired_skip_token_restarts_the_folder_once()
    {
        var source = Create();
        var folder = await source.ResolveFolderAsync(Large, Recruiter);

        var page = await source.ReadNextPageAsync(folder, null, Recruiter);          // Large -> enqueues 25 subfolders
        page = await source.ReadNextPageAsync(folder, page.NextCursor, Recruiter);   // sub-00, first 200 of 900 entries
        page.FolderKey.Should().Be("gen-folder:00");
        page.FolderRestarted.Should().BeFalse();

        source.ExpireSkipTokenOnce("gen-folder:00");
        var resumed = await source.ReadNextPageAsync(folder, page.NextCursor, Recruiter);
        resumed.FolderRestarted.Should().BeTrue();
        resumed.FolderKey.Should().Be("gen-folder:00");
        resumed.Files.Select(f => f.ItemId).Should().Equal(page.Files.Select(f => f.ItemId), "the folder is re-listed from its start");

        var next = await source.ReadNextPageAsync(folder, resumed.NextCursor, Recruiter);
        next.FolderRestarted.Should().BeFalse();
    }
}
