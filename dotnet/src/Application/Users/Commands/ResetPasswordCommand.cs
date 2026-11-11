using MediatR;
using System.Security.Cryptography;
using System.Text;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Commands;

public record ResetPasswordCommand(string UserId, string NewPassword) : IRequest<bool>;

public class ResetPasswordCommandHandler : IRequestHandler<ResetPasswordCommand, bool>
{
    private readonly IUserRepository _userRepository;

    public ResetPasswordCommandHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<bool> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user == null) return false;

        user.PasswordHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.NewPassword)));
        await _userRepository.UpdateAsync(user, cancellationToken);
        return true;
    }
}
