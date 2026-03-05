using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Commands;

public record RequestPasswordResetCommand(string Reason) : IRequest<PasswordResetRequest>;

public class RequestPasswordResetCommandHandler : IRequestHandler<RequestPasswordResetCommand, PasswordResetRequest>
{
    private readonly IUserRepository _userRepository;
    private readonly ICurrentUserService _currentUser;

    public RequestPasswordResetCommandHandler(IUserRepository userRepository, ICurrentUserService currentUser)
    {
        _userRepository = userRepository;
        _currentUser = currentUser;
    }

    public async Task<PasswordResetRequest> Handle(RequestPasswordResetCommand request, CancellationToken cancellationToken)
    {
        var resetRequest = new PasswordResetRequest
        {
            UserId = _currentUser.UserId ?? throw new UnauthorizedAccessException(),
            Username = _currentUser.Username ?? "",
            Reason = request.Reason
        };
        await _userRepository.AddResetRequestAsync(resetRequest, cancellationToken);
        return resetRequest;
    }
}
