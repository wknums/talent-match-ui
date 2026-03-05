namespace TalentMatch.Application.Common.Interfaces;

public interface ICurrentUserService
{
    string? UserId { get; }
    string? Username { get; }
    string? Role { get; }
    string? Department { get; }
    bool IsAdmin { get; }
}
