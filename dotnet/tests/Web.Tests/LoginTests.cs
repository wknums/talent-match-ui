using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public class LoginTests : BunitContext
{
    [Fact]
    public void MicrosoftLogin_RequestsAccountSelection()
    {
        var authConfiguration = new PublicAuthConfiguration(
            "entra",
            TenantId: "tenant-id",
            ClientId: "client-id",
            Authority: "https://login.microsoftonline.com/tenant-id",
            ApiScope: "api://api-id/access_as_user",
            ApiBaseAddress: "http://localhost/");
        var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(authConfiguration);
        Services.AddSingleton(new ApiClient(httpClient, authConfiguration));

        var cut = Render<Login>();
        cut.Find("button").Click();

        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.Uri.Should().Be("http://localhost/authentication/login");
        navigation.History.Single().Options.HistoryEntryState.Should().Contain("select_account");
    }
}