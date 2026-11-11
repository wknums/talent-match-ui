using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Users.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class UpdateUserCommandTests
{
    private readonly Mock<IUserRepository> _userRepoMock = new();
    private readonly Mock<ICurrentUserService> _currentUserMock = new();

    private UpdateUserCommandHandler CreateHandler()
        => new(_userRepoMock.Object, _currentUserMock.Object);

    [Fact]
    public async Task Handle_ValidCommand_UpdatesUser()
    {
        var userId = "user-1";
        var existingUser = new User { Id = userId, Username = "jdoe", FullName = "John Doe", Email = "john@test.com", Role = "recruiter", Department = "Engineering" };

        _currentUserMock.Setup(c => c.UserId).Returns("admin-1");
        _userRepoMock.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existingUser);
        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<User> { existingUser });
        _userRepoMock.Setup(r => r.UpdateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var handler = CreateHandler();
        var command = new UpdateUserCommand(userId, "Jane Doe", "jane@test.com", "business_panel", "Engineering,HR");

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().BeTrue();
        _userRepoMock.Verify(r => r.UpdateAsync(It.Is<User>(u =>
            u.FullName == "Jane Doe" &&
            u.Email == "jane@test.com" &&
            u.Role == "business_panel" &&
            u.Department == "Engineering,HR"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_SelfRoleChange_PreservesExistingRole()
    {
        var userId = "admin-1";
        var existingUser = new User { Id = userId, Username = "admin", FullName = "Admin", Email = "admin@test.com", Role = "admin", Department = "" };

        _currentUserMock.Setup(c => c.UserId).Returns(userId);
        _userRepoMock.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existingUser);
        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<User> { existingUser });
        _userRepoMock.Setup(r => r.UpdateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var handler = CreateHandler();
        var command = new UpdateUserCommand(userId, "Admin Updated", "admin@test.com", "recruiter", "");

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().BeTrue();
        _userRepoMock.Verify(r => r.UpdateAsync(It.Is<User>(u =>
            u.Role == "admin" &&
            u.FullName == "Admin Updated"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFalse()
    {
        _currentUserMock.Setup(c => c.UserId).Returns("admin-1");
        _userRepoMock.Setup(r => r.GetByIdAsync("nonexistent", It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var handler = CreateHandler();
        var command = new UpdateUserCommand("nonexistent", "Name", "email@test.com", "recruiter", "");

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().BeFalse();
        _userRepoMock.Verify(r => r.UpdateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DuplicateEmail_ThrowsException()
    {
        var userId = "user-1";
        var existingUser = new User { Id = userId, Username = "jdoe", Email = "john@test.com", Role = "recruiter" };
        var otherUser = new User { Id = "user-2", Username = "other", Email = "taken@test.com", Role = "recruiter" };

        _currentUserMock.Setup(c => c.UserId).Returns("admin-1");
        _userRepoMock.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existingUser);
        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<User> { existingUser, otherUser });

        var handler = CreateHandler();
        var command = new UpdateUserCommand(userId, "John", "taken@test.com", "recruiter", "");

        var act = () => handler.Handle(command, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in use*");
    }

    [Fact]
    public async Task Validator_AllowsBusinessPanelRole()
    {
        var validator = new UpdateUserValidator();

        var result = await validator.ValidateAsync(new UpdateUserCommand(
            "user-1",
            "Jane Doe",
            "jane@test.com",
            "business_panel",
            "Engineering,HR"));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validator_InvalidRole_ReturnsValidationError()
    {
        var validator = new UpdateUserValidator();

        var result = await validator.ValidateAsync(new UpdateUserCommand(
            "user-1",
            "Jane Doe",
            "jane@test.com",
            "viewer",
            "Engineering"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "Role");
    }
}
