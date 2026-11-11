using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Users.Queries;

public record GetUsersQuery : IRequest<IReadOnlyList<User>>;

public class GetUsersQueryHandler : IRequestHandler<GetUsersQuery, IReadOnlyList<User>>
{
    private readonly IUserRepository _userRepository;

    public GetUsersQueryHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<IReadOnlyList<User>> Handle(GetUsersQuery request, CancellationToken cancellationToken)
    {
        return await _userRepository.GetAllAsync(cancellationToken);
    }
}
