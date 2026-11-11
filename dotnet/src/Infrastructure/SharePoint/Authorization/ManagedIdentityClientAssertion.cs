using Azure.Core;
using Azure.Identity;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

public interface IClientAssertionProvider
{
    ValueTask<string> GetClientAssertionAsync(CancellationToken cancellationToken = default);
}

public sealed class ManagedIdentityClientAssertion : IClientAssertionProvider
{
    private static readonly string[] ExchangeScope = ["api://AzureADTokenExchange/.default"];
    private readonly TokenCredential _credential;

    public ManagedIdentityClientAssertion()
        : this(CreateCredential())
    {
    }

    public ManagedIdentityClientAssertion(TokenCredential credential) =>
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    public async ValueTask<string> GetClientAssertionAsync(CancellationToken cancellationToken = default) =>
        (await _credential.GetTokenAsync(
            new TokenRequestContext(ExchangeScope),
            cancellationToken)).Token;

    public static async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        var provider = new ManagedIdentityClientAssertion();
        return await provider.GetClientAssertionAsync(cancellationToken);
    }

    private static TokenCredential CreateCredential()
    {
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        return string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }
}
