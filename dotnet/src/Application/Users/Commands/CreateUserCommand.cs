using MediatR;
using System.Security.Cryptography;
using System.Text;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Commands;

public record CreateUserCommand(
    string Username,
    string Role,
    string Department,
    string Password,
    string FullName,
    string Email
) : IRequest<User>;

public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, User>
{
    private readonly IUserRepository _userRepository;

    public CreateUserCommandHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<User> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var existing = await _userRepository.GetByUsernameAsync(request.Username, cancellationToken);
        if (existing != null)
            throw new InvalidOperationException($"Username '{request.Username}' already exists.");

        var user = new User
        {
            Username = request.Username,
            Role = request.Role,
            Department = request.Department,
            FullName = request.FullName,
            Email = request.Email,
            PasswordHash = HashPassword(request.Password)
        };

        await _userRepository.AddAsync(user, cancellationToken);
        return user;
    }

    private static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexStringLower(bytes);
    }
}
