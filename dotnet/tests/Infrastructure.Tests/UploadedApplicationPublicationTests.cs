using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

public sealed class UploadedApplicationPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishUploadedAsync_PublishesAllOrNone(bool includeMissingApplication)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Jobs.Add(new Job { Id = "job-1", Title = "Test job" });
        await db.SaveChangesAsync();
        var repo = new ApplicationRepository(db);
        foreach (var id in new[] { "app-1", "app-2" })
        {
            await repo.AddAsync(new Domain.Entities.Application { Id = id, JobId = "job-1", Status = "Uploading" });
            await repo.AddDocumentAsync(new ApplicationDocument
            {
                ApplicationId = id,
                FileName = "cv.txt",
                FileType = "text/plain",
                ContentBase64 = Convert.ToBase64String("test cv"u8),
            });
        }
        var ids = new List<string> { "app-1", "app-2" };
        if (includeMissingApplication)
            ids.Add("missing");

        var act = () => repo.PublishUploadedAsync(ids);
        if (includeMissingApplication)
            await act.Should().ThrowAsync<InvalidOperationException>();
        else
            await act();

        var applications = await db.Applications.AsNoTracking().ToListAsync();
        applications.Should().HaveCount(2).And.OnlyContain(application =>
            application.Status == (includeMissingApplication ? "Uploading" : "Queued"));
        (await db.DocumentBlobs.CountAsync()).Should().Be(2);
    }
}
