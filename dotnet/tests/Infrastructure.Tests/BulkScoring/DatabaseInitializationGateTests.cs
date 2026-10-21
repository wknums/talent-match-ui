using FluentAssertions;
using TalentMatch.Infrastructure.HostedServices;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class DatabaseInitializationGateTests
{
    [Fact]
    public async Task A_new_gate_is_open()
    {
        var gate = new DatabaseInitializationGate();

        await gate.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_closed_gate_holds_waiters_until_it_is_opened()
    {
        var gate = new DatabaseInitializationGate();
        gate.Close();

        var waiter = gate.WaitAsync(CancellationToken.None);
        await Task.Delay(50);
        waiter.IsCompleted.Should().BeFalse();

        gate.Open();
        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Waiting_on_a_closed_gate_honours_cancellation()
    {
        var gate = new DatabaseInitializationGate();
        gate.Close();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var wait = () => gate.WaitAsync(cts.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
    }
}
