using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Commands;

public record UpdateUserCommand(
    string UserId,
    string FullName,
    string Email,
    string Role,
    string Department
) : IRequest<bool>;

public class UpdateUserCommandHandler : IRequestHandler<UpdateUserCommand, bool>
{
    private readonly IUserRepository _userRepository;
    private readonly ICurrentUserService _currentUser;

    public UpdateUserCommandHandler(IUserRepository userRepository, ICurrentUserService currentUser)
    {
        _userRepository = userRepository;
        _currentUser = currentUser;
    }

    public async Task<bool> Handle(UpdateUserCommand request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user == null) return false;

        // Check email uniqueness (exclude current user)
        var allUsers = await _userRepository.GetAllAsync(cancellationToken);
        var emailConflict = allUsers.Any(u => u.Id != request.UserId &&
            string.Equals(u.Email, request.Email, StringComparison.OrdinalIgnoreCase));
        if (emailConflict)
            throw new InvalidOperationException($"Email '{request.Email}' is already in use by another user.");

        user.FullName = request.FullName;
        user.Email = request.Email;
        user.Department = request.Department;

        // Self-role-change prevention: silently preserve existing role when admin edits self
        if (_currentUser.UserId != request.UserId)
        {
            user.Role = request.Role;
        }

        await _userRepository.UpdateAsync(user, cancellationToken);
        return true;
    }
}
