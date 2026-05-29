using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.JSInterop;
using System.Net.Http.Json;
using TalentMatch.Web.Client;
using TalentMatch.Web.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<CookieHandler>();
builder.Services.AddHttpClient("API", client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
    client.Timeout = TimeSpan.FromMinutes(10);
})
    .AddHttpMessageHandler<CookieHandler>();
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("API"));
builder.Services.AddScoped<ApiClient>();

var host = builder.Build();
await LogBuildStampAsync(host);
await host.RunAsync();

static async Task LogBuildStampAsync(WebAssemblyHost host)
{
    var http = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("API");
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
