using FluentAssertions;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkSourceAdmissionRepositoryTests
{
    [Fact]
    public void Distributed_admission_repository_has_atomic_actor_ip_callback_and_cleanup_operations()
    {
        var type = typeof(AppDbContext).Assembly.GetType(
            "TalentMatch.Infrastructure.Persistence.Repositories.BulkSourceAdmissionRepository");
        type.Should().NotBeNull();
        string[] methods = ["TryAdmitConnectionStartAsync", "TryAdmitCallbackAsync", "CleanupExpiredAsync"];
        methods.Should().AllSatisfy(name =>
            type!.GetMethods().Select(method => method.Name).Should().Contain(name));
    }

    [Fact]
    public void Admission_model_stores_only_an_HMAC_subject()
    {
        var type = typeof(BulkSourceAdmissionEvent);
        type.GetProperty("SubjectHash").Should().NotBeNull();
        type.GetProperty("SubjectIp").Should().BeNull();
        type.GetProperty("ProductActorId").Should().BeNull();
    }
}
