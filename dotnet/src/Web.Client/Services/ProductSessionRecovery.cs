using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace TalentMatch.Web.Client.Services;

public static class ProductSessionRecovery
{
    public static bool IsRequired(Exception exception) =>
        exception is AccessTokenNotAvailableException
        || exception is ApiException
        {
            StatusCode: 401,
            ErrorCode: null or "token_stale" or "auth_required" or "invalid_token",
        };

    public static void SignInAgain(NavigationManager navigation, PublicAuthConfiguration configuration)
    {
        if (!configuration.IsEntra)
        {
            navigation.NavigateTo("/login");
            return;
        }

        var options = new InteractiveRequestOptions
        {
            Interaction = InteractionType.SignIn,
            ReturnUrl = navigation.Uri,
        };
        options.TryAddAdditionalParameter("prompt", "login");
        navigation.NavigateToLogin("authentication/login", options);
    }
}
