using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Queries;

public record GetResetRequestsQuery : IRequest<IReadOnlyList<PasswordResetRequest>>;

public class GetResetRequestsQueryHandler : IRequestHandler<GetResetRequestsQuery, IReadOnlyList<PasswordResetRequest>>
{
    private readonly IUserRepository _userRepository;

    public GetResetRequestsQueryHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<IReadOnlyList<PasswordResetRequest>> Handle(GetResetRequestsQuery request, CancellationToken cancellationToken)
    {
        return await _userRepository.GetResetRequestsAsync(cancellationToken);
    }
}
