using FluentAssertions;
using Moq;
using TalentMatch.Application.Users.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class CreateUserCommandTests
{
    private readonly Mock<IUserRepository> _userRepoMock = new();

    [Fact]
    public async Task Handle_ValidCommand_CreatesUser()
    {
        _userRepoMock.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _userRepoMock.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new CreateUserCommandHandler(_userRepoMock.Object);
        var command = new CreateUserCommand("newuser", "recruiter", "Engineering", "password123", "New User", "new@test.com");

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        result.Username.Should().Be("newuser");
        result.Role.Should().Be("recruiter");
        result.Department.Should().Be("Engineering");
        result.PasswordHash.Should().NotBeEmpty();
        _userRepoMock.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_DuplicateUsername_ThrowsException()
    {
        _userRepoMock.Setup(r => r.GetByUsernameAsync("existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Username = "existing" });

        var handler = new CreateUserCommandHandler(_userRepoMock.Object);
        var command = new CreateUserCommand("existing", "recruiter", "Engineering", "password123", "User", "test@test.com");

        var act = () => handler.Handle(command, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }
}
