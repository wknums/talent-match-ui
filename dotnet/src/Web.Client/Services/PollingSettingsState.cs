namespace TalentMatch.Web.Client.Services;

public sealed record PollingSettingsDto(
    [property: System.Text.Json.Serialization.JsonRequired] int ThroughputRefreshIntervalSeconds = 60,
    [property: System.Text.Json.Serialization.JsonRequired] int StatusPollingIntervalSeconds = 60);

public sealed class PollingSettingsState
{
    public PollingSettingsDto Current { get; private set; } = new();
    public event Action? Changed;

    public void Update(PollingSettingsDto settings)
    {
        if (Current == settings)
            return;
        Current = settings;
        Changed?.Invoke();
    }
}
