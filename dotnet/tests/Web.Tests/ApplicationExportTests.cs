using System.IO.Compression;
using System.Text;
using FluentAssertions;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class ApplicationExportTests
{
    private static readonly ApplicationDto Application = new(
        "application-hidden-123",
        "job-1",
        "candidate-ref-1",
        "=HYPERLINK(\"https://example.test\")",
        "candidate@example.test",
        "Completed",
        87.125,
        "Eligible",
        2.5,
        new DateTime(2026, 9, 23, 18, 30, 0, DateTimeKind.Utc));

    [Fact]
    public void CreateCsv_IncludesDisplayedFieldsAndHiddenApplicationId()
    {
        var export = ApplicationExportBuilder.Create(
            [Application],
            "Platform Engineer",
            "Longlist",
            ApplicationExportFormat.Csv);

        var csv = Encoding.UTF8.GetString(export.Content);

        export.FileName.Should().Be("platform-engineer-longlist-applications.csv");
        export.ContentType.Should().Be("text/csv;charset=utf-8");
        csv.Should().Contain("Application ID,Candidate,Email,Status,Score,Variance,Decision,Submitted");
        csv.Should().Contain("application-hidden-123");
        csv.Should().Contain("\"'=HYPERLINK(\"\"https://example.test\"\")\"");
        csv.Should().Contain("candidate@example.test");
        csv.Should().Contain("87.125");
        csv.Should().Contain("2026-09-23T18:30:00.0000000Z");
    }

    [Fact]
    public void CreateXlsx_IncludesDisplayedFieldsAndHiddenApplicationId()
    {
        var export = ApplicationExportBuilder.Create(
            [Application],
            "Platform Engineer",
            "Shortlist",
            ApplicationExportFormat.Xlsx);

        using var stream = new MemoryStream(export.Content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var worksheet = ReadEntry(archive, "xl/worksheets/sheet1.xml");
        var workbook = ReadEntry(archive, "xl/workbook.xml");

        export.FileName.Should().Be("platform-engineer-shortlist-applications.xlsx");
        export.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        archive.GetEntry("[Content_Types].xml").Should().NotBeNull();
        archive.GetEntry("_rels/.rels").Should().NotBeNull();
        workbook.Should().Contain("name=\"Shortlist\"");
        worksheet.Should().Contain("Application ID");
        worksheet.Should().Contain("application-hidden-123");
        worksheet.Should().Contain("candidate@example.test");
        worksheet.Should().Contain("<v>87.125</v>");
    }

    private static string ReadEntry(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(archive.GetEntry(path)!.Open());
        return reader.ReadToEnd();
    }
}
