using FluentAssertions;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkSourceConnectionRepositoryTests
{
    [Fact]
    public void Repository_contract_exposes_bound_load_expiry_consume_and_atomic_transfer()
    {
        var type = typeof(AppDbContext).Assembly.GetType(
            "TalentMatch.Infrastructure.Persistence.Repositories.BulkSourceConnectionRepository");
        type.Should().NotBeNull();
        string[] methods =
        [
            "AddAsync", "GetBoundAsync", "ExpireAsync", "AbandonAsync",
            "TransferToRequestAsync",
        ];
        methods.Should().AllSatisfy(name =>
            type!.GetMethods().Select(method => method.Name).Should().Contain(name));
    }
}
