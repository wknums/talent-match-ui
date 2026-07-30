using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.Authentication.WebAssembly.Msal;
using Microsoft.JSInterop;
using System.Net.Http.Json;
using TalentMatch.Web.Client;
using TalentMatch.Web.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var authConfiguration = await LoadAuthConfigurationAsync(builder.HostEnvironment.BaseAddress);
builder.Services.AddSingleton(authConfiguration);

var apiClientBuilder = builder.Services.AddHttpClient("API", client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddHttpClient("Public", client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
});

if (authConfiguration.IsSimple)
{
    builder.Services.AddScoped<CookieHandler>();
    apiClientBuilder.AddHttpMessageHandler<CookieHandler>();
}
else if (authConfiguration.IsEntra)
{
    builder.Services.AddMsalAuthentication(options =>
    {
        options.ProviderOptions.Authentication.Authority = authConfiguration.Authority;
        options.ProviderOptions.Authentication.ClientId = authConfiguration.ClientId;
        options.ProviderOptions.Authentication.ValidateAuthority = true;
        options.ProviderOptions.LoginMode = "redirect";
        options.ProviderOptions.DefaultAccessTokenScopes.Add(authConfiguration.ApiScope!);
    });

    builder.Services.AddScoped<IAccessTokenCacheInvalidator, MsalAccessTokenCacheInvalidator>();
    builder.Services.AddScoped<StaleTokenRetryHandler>();
    builder.Services.AddScoped<ApiAuthorizationMessageHandler>();

    // Registration order is pipeline order: the retry handler must sit outside the authorization
    // handler so that a retried request acquires a freshly minted token.
    apiClientBuilder.AddHttpMessageHandler<StaleTokenRetryHandler>();
    apiClientBuilder.AddHttpMessageHandler<ApiAuthorizationMessageHandler>();
}

builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("API"));
builder.Services.AddScoped<ApiClient>();

var host = builder.Build();
await LogBuildStampAsync(host);
await host.RunAsync();

static async Task<PublicAuthConfiguration> LoadAuthConfigurationAsync(string baseAddress)
{
    try
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
        var response = await client.GetAsync("/api/auth/config");
        if (!response.IsSuccessStatusCode)
        {
            return PublicAuthConfiguration.Failed(
                baseAddress,
                $"Authentication configuration could not be loaded (HTTP {(int)response.StatusCode}).");
        }

        var configuration = await response.Content.ReadFromJsonAsync<PublicAuthConfiguration>();
        return configuration?.WithBaseAddress(baseAddress).Validate()
            ?? PublicAuthConfiguration.Failed(baseAddress, "Authentication configuration was empty.");
    }
    catch (Exception ex)
    {
        return PublicAuthConfiguration.Failed(
            baseAddress,
            $"Authentication configuration could not be loaded: {ex.Message}");
    }
}

static async Task LogBuildStampAsync(WebAssemblyHost host)
{
    var http = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Public");
    var js = host.Services.GetRequiredService<IJSRuntime>();

    try
    {
        var stamp = await http.GetFromJsonAsync<BuildStamp>("build-info.json", cancellationToken: default);
        if (stamp is null || string.IsNullOrWhiteSpace(stamp.Version) || string.IsNullOrWhiteSpace(stamp.CreatedAtUtc))
        {
            await js.InvokeVoidAsync("console.warn", "[TalentMatch Build] build-info.json is missing required fields.");
            return;
        }

        await js.InvokeVoidAsync(
            "console.info",
            $"[TalentMatch Build] version={stamp.Version} createdAtUtc={stamp.CreatedAtUtc}");
    }
    catch (Exception ex)
    {
        await js.InvokeVoidAsync("console.warn", $"[TalentMatch Build] unable to read build-info.json: {ex.Message}");
    }
}

public sealed record BuildStamp(string? Version, string? CreatedAtUtc, string? Source);

// Ensures browser fetch sends cookies with every request
public class CookieHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class ApiAuthorizationMessageHandler : AuthorizationMessageHandler
{
    public ApiAuthorizationMessageHandler(
        IAccessTokenProvider provider,
        NavigationManager navigation,
        PublicAuthConfiguration configuration)
        : base(provider, navigation)
    {
        ConfigureHandler(
            authorizedUrls: [configuration.ApiAuthorizationUrl],
            scopes: [configuration.ApiScope!]);
    }
}
