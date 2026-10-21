using FluentAssertions;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class AsyncRefreshLoopTests
{
    [Fact]
    public async Task SlowRefreshesNeverOverlapAndWaitTheFullIntervalAfterCompletion()
    {
        var clock = new ScoringThroughputPanelTests.ManualTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var loop = new AsyncRefreshLoop(TimeSpan.FromSeconds(60), clock, async token =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await completed.Task.WaitAsync(token);
        }, Task.FromException);

        loop.Start();
        clock.TimeUntilNextTick.Should().Be(TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(60));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(5));
        calls.Should().Be(1);
        clock.ActiveTimers.Should().Be(0);

        loop.UpdateInterval(TimeSpan.FromSeconds(120));
        completed.TrySetResult();
        await WaitUntilAsync(() => clock.ActiveTimers == 1);
        clock.TimeUntilNextTick.Should().Be(TimeSpan.FromSeconds(120));
        calls.Should().Be(1);
    }

    [Fact]
    public async Task AChangedIntervalReschedulesWaitingWithoutRunningImmediatelyAndDisposalCancelsIt()
    {
        var clock = new ScoringThroughputPanelTests.ManualTimeProvider();
        var calls = 0;
        var loop = new AsyncRefreshLoop(TimeSpan.FromSeconds(60), clock, _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, Task.FromException);
        loop.Start();
        clock.Advance(TimeSpan.FromSeconds(20));

        loop.UpdateInterval(TimeSpan.FromSeconds(90));

        await WaitUntilAsync(() => clock.ActiveTimers == 1 && clock.TimeUntilNextTick == TimeSpan.FromSeconds(90));
        calls.Should().Be(0);
        await loop.DisposeAsync();
        clock.ActiveTimers.Should().Be(0);
        clock.Advance(TimeSpan.FromHours(1));
        calls.Should().Be(0);
        await loop.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }
}
