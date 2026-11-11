using FluentAssertions;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Tests;

public class UserTests
{
    [Fact]
    public void User_DefaultValues_AreCorrect()
    {
        var user = new User();
        user.Id.Should().NotBeNullOrEmpty();
        user.Role.Should().Be("recruiter");
        user.Username.Should().BeEmpty();
        user.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        user.LastLogin.Should().BeNull();
    }

    [Fact]
    public void User_CanSetProperties()
    {
        var user = new User
        {
            Username = "testuser",
            Role = "admin",
            Department = "Engineering",
            FullName = "Test User",
            Email = "test@example.com"
        };
        user.Username.Should().Be("testuser");
        user.Role.Should().Be("admin");
        user.Department.Should().Be("Engineering");
    }
}
