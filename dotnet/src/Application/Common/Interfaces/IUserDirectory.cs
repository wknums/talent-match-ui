namespace TalentMatch.Application.Common.Interfaces;

public interface IUserDirectory
{
    Task<IReadOnlyDictionary<string, string>> ResolveDisplayNamesAsync(
        IReadOnlyCollection<string> actorIds,
        CancellationToken cancellationToken = default);
}
