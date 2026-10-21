using System.Net.Http.Json;

namespace TalentMatch.Web.Client.Services;

public sealed class BuildInfoService(HttpClient http)
{
    public async Task<BuildStamp?> GetBuildStampAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("build-info.json", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Build information could not be loaded (HTTP {(int)response.StatusCode}).");
        }

        return await response.Content.ReadFromJsonAsync<BuildStamp>(
            cancellationToken: cancellationToken);
    }
}
