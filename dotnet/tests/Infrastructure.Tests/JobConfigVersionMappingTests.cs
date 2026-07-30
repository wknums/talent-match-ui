using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests;

public sealed class JobConfigVersionMappingTests
{
    [Fact]
    public void Model_MapsOnlySharedSchemaMustHavesColumn()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new AppDbContext(options);

        var entity = context.Model.FindEntityType(typeof(JobConfigVersion))
            ?? throw new InvalidOperationException("JobConfigVersion is not part of the EF model.");
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());

        entity.FindProperty(nameof(JobConfigVersion.MustHaveCriteriaJson)).Should().BeNull();
        entity.FindProperty(nameof(JobConfigVersion.MustHavesJson))!
            .GetColumnName(table).Should().Be("MustHavesJson");
    }
}