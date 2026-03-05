using Microsoft.AspNetCore.SignalR.Client;

namespace TalentMatch.Web.Client.Services;

public class SignalRService : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    public event Action<SystemStatsDto>? OnStatsUpdated;
    public event Action<string, string>? OnApplicationStatusChanged;

    public async Task StartAsync(string hubUrl)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<SystemStatsDto>("StatsUpdated", stats =>
        {
            OnStatsUpdated?.Invoke(stats);
        });

        _hubConnection.On<string, string>("ApplicationStatusChanged", (applicationId, newStatus) =>
        {
            OnApplicationStatusChanged?.Invoke(applicationId, newStatus);
        });

        await _hubConnection.StartAsync();
    }

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
            await _hubConnection.DisposeAsync();
    }
}
