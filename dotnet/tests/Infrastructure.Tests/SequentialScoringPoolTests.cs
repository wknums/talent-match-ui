using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.HostedServices;
using TalentMatch.Infrastructure.Services;

namespace TalentMatch.Infrastructure.Tests;

public sealed class SequentialScoringPoolTests
{
    [Fact]
    public async Task TwoHundredSlots_CapConcurrencyAndRefillWithoutDuplicateScheduling()
    {
        var queue = new TestQueue();
        var scorer = new ControlledScorer();
        var signal = new SequentialScoringSignal();
        using var services = new ServiceCollection()
            .AddSingleton<ISequentialScoringQueueRepository>(queue)
            .AddSingleton<ISequentialApplicationScorer>(scorer)
            .BuildServiceProvider();
        using var pool = CreatePool(services, signal, parallelism: 200);
        queue.Upload(1, 220);
        await pool.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => scorer.Started.Count == 200);
            scorer.Active.Should().Be(200);
            queue.Pending.Count.Should().Be(20);

            scorer.Complete("app-1");
            scorer.Fail("app-2");
            await Eventually(() => scorer.Started.Count == 202);
            scorer.Active.Should().Be(200);
            queue.Pending.Count.Should().Be(18);
            queue.Failures.Should().ContainSingle(id => id == "app-2");

            scorer.Drain();
            await Eventually(() => scorer.Started.Count == 220 && scorer.Active == 0);
            scorer.Peak.Should().Be(200);
            scorer.Started.Distinct().Should().HaveCount(220);
            queue.Pending.Should().BeEmpty();
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SevenThenSeven_FillsTenSlots_AndRefillsAfterSuccessAndFailure()
    {
        var queue = new TestQueue();
        var scorer = new ControlledScorer();
        var signal = new SequentialScoringSignal();
        using var services = new ServiceCollection()
            .AddSingleton<ISequentialScoringQueueRepository>(queue)
            .AddSingleton<ISequentialApplicationScorer>(scorer)
            .BuildServiceProvider();
        using var pool = CreatePool(services, signal);
        await pool.StartAsync(CancellationToken.None);
        try
        {
            queue.Upload(1, 7);
            signal.Pulse();
            await Eventually(() => scorer.Started.Count == 7);
            scorer.Active.Should().Be(7);

            queue.Upload(8, 7);
            signal.Pulse();
            await Eventually(() => scorer.Started.Count == 10);
            scorer.Active.Should().Be(10);
            queue.Pending.Count.Should().Be(4);

            scorer.Complete("app-1");
            await Eventually(() => scorer.Started.Count == 11);
            scorer.Active.Should().Be(10);
            queue.Pending.Count.Should().Be(3);

            scorer.Fail("app-2");
            await Eventually(() => scorer.Started.Count == 12);
            scorer.Active.Should().Be(10);
            queue.Failures.Should().ContainSingle(id => id == "app-2");

            scorer.Drain();
            await Eventually(() => scorer.Started.Count == 14 && scorer.Active == 0);
            scorer.Peak.Should().Be(10);
            scorer.Started.Distinct().Should().HaveCount(14);
            queue.Pending.Should().BeEmpty();
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PollingFindsWorkWithoutAnUploadSignal_AndRepeatedSignalsDoNotMultiplyCapacity()
    {
        var queue = new TestQueue();
        var scorer = new ControlledScorer();
        var signal = new SequentialScoringSignal();
        using var services = new ServiceCollection()
            .AddSingleton<ISequentialScoringQueueRepository>(queue)
            .AddSingleton<ISequentialApplicationScorer>(scorer)
            .BuildServiceProvider();
        using var pool = CreatePool(services, signal, parallelism: 2);
        await pool.StartAsync(CancellationToken.None);
        try
        {
            queue.Upload(1, 5);
            await Eventually(() => scorer.Started.Count == 2);
            for (var i = 0; i < 20; i++)
                signal.Pulse();
            scorer.Complete("app-1");
            await Eventually(() => scorer.Started.Count == 3);
            scorer.Peak.Should().Be(2);
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }

        await Eventually(() => scorer.Active == 0);
        queue.Failures.Should().Contain(["app-2", "app-3"]);
        queue.Pending.Count.Should().Be(2);
    }

    [Fact]
    public async Task DisabledPool_DoesNotClaimOrRecoverPlatformWork()
    {
        var queue = new TestQueue();
        queue.Upload(1, 7);
        using var services = new ServiceCollection()
            .AddSingleton<ISequentialScoringQueueRepository>(queue)
            .BuildServiceProvider();
        using var pool = new SequentialScoringPool(
            services.GetRequiredService<IServiceScopeFactory>(),
            new SequentialScoringSignal(),
            new SequentialScoringOptions { Enabled = false },
            NullLogger<SequentialScoringPool>.Instance);

        await pool.StartAsync(CancellationToken.None);
        await pool.StopAsync(CancellationToken.None);

        queue.Pending.Count.Should().Be(7);
        queue.Recoveries.Should().Be(0);
    }

    private static SequentialScoringPool CreatePool(
        ServiceProvider services, SequentialScoringSignal signal, int parallelism = 10)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(), signal,
            new SequentialScoringOptions
            {
                MaxParallel = parallelism,
                PollInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromSeconds(1),
                LeaseDuration = TimeSpan.FromMinutes(2),
            },
            NullLogger<SequentialScoringPool>.Instance);

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class TestQueue : ISequentialScoringQueueRepository
    {
        public ConcurrentQueue<string> Pending { get; } = new();
        public ConcurrentBag<string> Failures { get; } = new();
        public int Recoveries;

        public void Upload(int start, int count)
        {
            foreach (var i in Enumerable.Range(start, count))
                Pending.Enqueue($"app-{i}");
        }

        public Task<SequentialScoringWork?> TryClaimAsync(
            string owner, DateTime now, TimeSpan leaseDuration, CancellationToken ct)
            => Task.FromResult(Pending.TryDequeue(out var id)
                ? new SequentialScoringWork(id, "job-1", owner, "prompt-1", 3, "Job", "[]", 15, 70)
                : null);

        public Task<bool> RenewLeaseAsync(
            string applicationId, string owner, DateTime now, TimeSpan leaseDuration, CancellationToken ct)
            => Task.FromResult(true);

        public async Task<bool> FinalizeAsync(
            string applicationId, string owner, Func<CancellationToken, Task> persistResult, CancellationToken ct)
        {
            await persistResult(ct);
            return true;
        }

        public Task FailAsync(
            string applicationId, string owner, string error, int failureCount, CancellationToken ct)
        {
            Failures.Add(applicationId);
            return Task.CompletedTask;
        }

        public Task RecoverExpiredAsync(DateTime now, CancellationToken ct)
        {
            Interlocked.Increment(ref Recoveries);
            return Task.CompletedTask;
        }

        public Task<bool> RetryAsync(string itemId, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class ControlledScorer : ISequentialApplicationScorer
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();
        private int _active;
        private int _peak;
        private volatile bool _drain;
        public ConcurrentBag<string> Started { get; } = new();
        public int Active => Volatile.Read(ref _active);
        public int Peak => Volatile.Read(ref _peak);

        public async Task ScoreAsync(SequentialScoringWork work, CancellationToken ct)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[work.ApplicationId] = completion;
            var active = Interlocked.Increment(ref _active);
            int peak;
            do { peak = _peak; }
            while (active > peak && Interlocked.CompareExchange(ref _peak, active, peak) != peak);
            Started.Add(work.ApplicationId);
            if (_drain)
                completion.TrySetResult();
            try { await completion.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Complete(string id) => _pending[id].TrySetResult();
        public void Fail(string id) => _pending[id].TrySetException(new InvalidOperationException("Scoring failed"));
        public void Drain()
        {
            _drain = true;
            foreach (var item in _pending.Values)
                item.TrySetResult();
        }
    }
}
