using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TalentMatch.Application.Users.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using Moq;

namespace TalentMatch.Application.Tests;

public class PasswordHashConsistencyTests
{
    [Theory]
    [InlineData("password123")]
    [InlineData("adm1n99")]
    [InlineData("complex!P@ssw0rd#2024")]
    [InlineData("")]
    [InlineData("short")]
    public async Task HashPassword_ProducesSameHash_AsLoginComparison(string password)
    {
        // Arrange: compute the hash the same way AuthEndpoints.cs login does
        var loginHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

        // Act: create a user via the command handler (which uses its own HashPassword method)
        var repoMock = new Mock<IUserRepository>();
        repoMock.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        repoMock.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new CreateUserCommandHandler(repoMock.Object);
        var command = new CreateUserCommand("testuser", "recruiter", "Engineering", password, "Test User", "test@test.com");
        var user = await handler.Handle(command, CancellationToken.None);

        // Assert: the stored hash must exactly match what login will compare against
        user.PasswordHash.Should().Be(loginHash,
            because: "CreateUserCommandHandler.HashPassword() and AuthEndpoints login must use identical SHA-256 hashing");
    }

    [Fact]
    public async Task SeededAdminHash_MatchesLoginHash()
    {
        // The hash seeded in Program.cs for the default admin user
        var seededHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("adm1n99")));

        // Simulate creating a user with the same password through the handler
        var repoMock = new Mock<IUserRepository>();
        repoMock.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        repoMock.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new CreateUserCommandHandler(repoMock.Object);
        var command = new CreateUserCommand("admin", "admin", "all", "adm1n99", "Administrator", "");
        var user = await handler.Handle(command, CancellationToken.None);

        user.PasswordHash.Should().Be(seededHash,
            because: "the seeded admin password hash must match what CreateUserCommandHandler produces");
    }
}
