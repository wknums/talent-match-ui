using MediatR;
using System.Security.Cryptography;
using System.Text;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Commands;

public record ChangePasswordCommand(string CurrentPassword, string NewPassword) : IRequest<bool>;

public class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand, bool>
{
    private readonly IUserRepository _userRepository;
    private readonly ICurrentUserService _currentUser;

    public ChangePasswordCommandHandler(IUserRepository userRepository, ICurrentUserService currentUser)
    {
        _userRepository = userRepository;
        _currentUser = currentUser;
    }

    public async Task<bool> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId == null)
            throw new UnauthorizedAccessException();

        var user = await _userRepository.GetByIdAsync(_currentUser.UserId, cancellationToken);
        if (user == null) return false;

        var currentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.CurrentPassword)));
        if (user.PasswordHash != currentHash)
            throw new InvalidOperationException("Current password is incorrect.");

        user.PasswordHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.NewPassword)));
        await _userRepository.UpdateAsync(user, cancellationToken);
        return true;
    }
}
