using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Commands;

public record DeleteUserCommand(string UserId) : IRequest<bool>;

public class DeleteUserCommandHandler : IRequestHandler<DeleteUserCommand, bool>
{
    private readonly IUserRepository _userRepository;
    private readonly ICurrentUserService _currentUser;

    public DeleteUserCommandHandler(IUserRepository userRepository, ICurrentUserService currentUser)
    {
        _userRepository = userRepository;
        _currentUser = currentUser;
    }

    public async Task<bool> Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId == request.UserId)
            throw new InvalidOperationException("Cannot delete your own account.");

        var user = await _userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user == null) return false;

        await _userRepository.DeleteAsync(request.UserId, cancellationToken);
        return true;
    }
}
