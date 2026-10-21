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

    // Fast heartbeat and a short lease so renewal behaviour can be observed in well under a second.
    private static SequentialScoringPool CreateRenewalPool(
        ServiceProvider services, TimeSpan lease, int renewAttempts = 3)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(), new SequentialScoringSignal(),
            new SequentialScoringOptions
            {
                MaxParallel = 1,
                PollInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(30),
                LeaseDuration = lease,
                RenewAttempts = renewAttempts,
                RenewRetryDelay = TimeSpan.FromMilliseconds(5),
                RenewRetryMaxDelay = TimeSpan.FromMilliseconds(20),
            },
            NullLogger<SequentialScoringPool>.Instance);

    private static async Task<(TestQueue Queue, ControlledScorer Scorer, ServiceProvider Services)> StartOneApplication(
        Func<int, Task<bool>> renew)
    {
        var queue = new TestQueue { Renew = renew };
        var scorer = new ControlledScorer();
        var services = new ServiceCollection()
            .AddSingleton<ISequentialScoringQueueRepository>(queue)
            .AddSingleton<ISequentialApplicationScorer>(scorer)
            .BuildServiceProvider();
        queue.Upload(1, 1);
        await Task.CompletedTask;
        return (queue, scorer, services);
    }

    [Fact]
    public async Task Lease_renewal_that_fails_briefly_is_retried_and_scoring_continues()
    {
        var (queue, scorer, services) = await StartOneApplication(call =>
            call <= 2 ? throw new TimeoutException("sql busy") : Task.FromResult(true));
        using var _ = services;
        using var pool = CreateRenewalPool(services, lease: TimeSpan.FromSeconds(10));
        await pool.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => scorer.Started.Count == 1);
            await Eventually(() => queue.RenewCalls >= 4);

            scorer.Complete("app-1");
            await Eventually(() => scorer.Active == 0);
            queue.Failures.Should().BeEmpty();
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Lease_renewal_that_fails_for_a_whole_retry_set_is_tried_again_while_the_lease_is_valid()
    {
        // Three attempts per set: the whole first set fails, the next heartbeat succeeds.
        var (queue, scorer, services) = await StartOneApplication(call =>
            call <= 4 ? throw new TimeoutException("sql busy") : Task.FromResult(true));
        using var _ = services;
        using var pool = CreateRenewalPool(services, lease: TimeSpan.FromSeconds(10));
        await pool.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => scorer.Started.Count == 1);
            await Eventually(() => queue.RenewCalls >= 6);

            scorer.Complete("app-1");
            await Eventually(() => scorer.Active == 0);
            queue.Failures.Should().BeEmpty();
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scoring_is_stopped_when_the_lease_can_no_longer_be_renewed_before_it_expires()
    {
        var (queue, scorer, services) = await StartOneApplication(_ => throw new TimeoutException("sql down"));
        using var _ = services;
        using var pool = CreateRenewalPool(services, lease: TimeSpan.FromMilliseconds(300));
        await pool.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => queue.Failures.Contains("app-1"));
            queue.RenewCalls.Should().BeGreaterThanOrEqualTo(3, "a whole retry set was used before giving up");
            await Eventually(() => scorer.Active == 0);
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_lease_that_is_no_longer_owned_is_not_retried()
    {
        var (queue, scorer, services) = await StartOneApplication(_ => Task.FromResult(false));
        using var _ = services;
        using var pool = CreateRenewalPool(services, lease: TimeSpan.FromSeconds(10), renewAttempts: 5);
        await pool.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => queue.Failures.Contains("app-1"));
            queue.RenewCalls.Should().Be(1);
            await Eventually(() => scorer.Active == 0);
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(null, 240)]
    [InlineData("", 240)]
    [InlineData("  ", 240)]
    [InlineData("60", 60)]
    [InlineData("300", 300)]
    [InlineData("1800", 1800)]
    public void Lease_duration_defaults_to_four_minutes_and_reads_the_variable(string? configured, int expectedSeconds)
        => SequentialScoringOptions.ResolveLeaseDuration(configured).Should().Be(TimeSpan.FromSeconds(expectedSeconds));

    [Theory]
    [InlineData("30")]
    [InlineData("59")]
    [InlineData("1801")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("four")]
    [InlineData("2.5")]
    public void Lease_duration_outside_the_supported_range_is_refused(string configured)
    {
        var action = () => SequentialScoringOptions.ResolveLeaseDuration(configured);

        action.Should().Throw<InvalidOperationException>().WithMessage("*AWR_SCORING_LEASE_SECONDS*60*1800*");
    }

    [Fact]
    public void The_default_lease_is_four_minutes_and_longer_than_the_heartbeat()
    {
        var options = new SequentialScoringOptions();

        options.LeaseDuration.Should().Be(TimeSpan.FromMinutes(4));
        options.LeaseDuration.Should().BeGreaterThan(options.HeartbeatInterval * 6);
    }

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
        private int _renewCalls;

        // Scripts RenewLeaseAsync by call number (1-based); null means always renewed.
        public Func<int, Task<bool>>? Renew { get; init; }
        public int RenewCalls => Volatile.Read(ref _renewCalls);

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
        {
            var call = Interlocked.Increment(ref _renewCalls);
            return Renew is null ? Task.FromResult(true) : Renew(call);
        }

        public Task<bool> FinalizeAsync(
            string applicationId, string owner, ApplicationScoringFinalization result, CancellationToken ct)
            => Task.FromResult(true);

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
