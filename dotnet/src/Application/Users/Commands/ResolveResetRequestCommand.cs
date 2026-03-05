using MediatR;
using System.Security.Cryptography;
using System.Text;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Commands;

public record ResolveResetRequestCommand(string RequestId, string Action, string? NewPassword) : IRequest<bool>;

public class ResolveResetRequestCommandHandler : IRequestHandler<ResolveResetRequestCommand, bool>
{
    private readonly IUserRepository _userRepository;

    public ResolveResetRequestCommandHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<bool> Handle(ResolveResetRequestCommand request, CancellationToken cancellationToken)
    {
        var requests = await _userRepository.GetResetRequestsAsync(cancellationToken);
        var resetRequest = requests.FirstOrDefault(r => r.Id == request.RequestId);
        if (resetRequest == null) return false;

        resetRequest.Status = request.Action; // "approved" or "rejected"
        await _userRepository.UpdateResetRequestAsync(resetRequest, cancellationToken);

        if (request.Action == "approved" && !string.IsNullOrEmpty(request.NewPassword))
        {
            var user = await _userRepository.GetByIdAsync(resetRequest.UserId, cancellationToken);
            if (user != null)
            {
                user.PasswordHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.NewPassword)));
                await _userRepository.UpdateAsync(user, cancellationToken);
            }
        }

        return true;
    }
}
