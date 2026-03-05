using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Queries;

public record GetCurrentUserQuery : IRequest<User?>;

public class GetCurrentUserQueryHandler : IRequestHandler<GetCurrentUserQuery, User?>
{
    private readonly IUserRepository _userRepository;
    private readonly ICurrentUserService _currentUser;

    public GetCurrentUserQueryHandler(IUserRepository userRepository, ICurrentUserService currentUser)
    {
        _userRepository = userRepository;
        _currentUser = currentUser;
    }

    public async Task<User?> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId == null) return null;
        return await _userRepository.GetByIdAsync(_currentUser.UserId, cancellationToken);
    }
}
