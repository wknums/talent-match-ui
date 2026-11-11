using System.Reflection;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests;

public sealed class SequentialScoringExecutionStrategyTests
{
    [Fact]
    public async Task DeadlocksHaveSixBoundedExponentiallyIncreasingJitteredDelays()
    {
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var strategy = new RecordingStrategy(context);
        var calls = 0;

        Func<Task> execute = () => strategy.ExecuteAsync<int>(() =>
        {
            calls++;
            return Task.FromException<int>(new DbUpdateException("save failed", SqlServerTestErrors.Create(1205)));
        });

        await execute.Should().ThrowAsync<RetryLimitExceededException>()
            .Where(exception => exception.InnerException is DbUpdateException);
        calls.Should().Be(7);
        strategy.Delays.Should().HaveCount(6);
        for (var index = 0; index < strategy.Delays.Count; index++)
        {
            var ceiling = Math.Min(1000, 100 * (1 << index));
            strategy.Delays[index].TotalMilliseconds.Should().BeInRange(ceiling / 2, ceiling);
        }
        strategy.Delays.Sum(delay => delay.TotalMilliseconds).Should().BeLessThan(3500);
    }

    [Fact]
    public async Task PermanentSqlErrorsAreNotRetried()
    {
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var strategy = new RecordingStrategy(context);
        var calls = 0;
        Func<Task> execute = () => strategy.ExecuteAsync<int>(() =>
        {
            calls++;
            return Task.FromException<int>(SqlServerTestErrors.Create(2601));
        });

        await execute.Should().ThrowAsync<SqlException>();
        calls.Should().Be(1);
        strategy.Delays.Should().BeEmpty();
    }

    private sealed class RecordingStrategy(DbContext context) : SequentialScoringExecutionStrategy(context)
    {
        public List<TimeSpan> Delays { get; } = [];

        protected override TimeSpan? GetNextDelay(Exception lastException)
        {
            var delay = base.GetNextDelay(lastException);
            if (delay.HasValue)
                Delays.Add(delay.Value);
            return delay;
        }
    }
}

internal static class SqlServerTestErrors
{
    public static SqlException Create(int number)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var constructor = typeof(SqlError).GetConstructors(instance).First();
        var arguments = constructor.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(string) ? "simulated SQL failure"
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
        arguments[0] = number;
        var error = (SqlError)constructor.Invoke(arguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", instance)!.Invoke(errors, [error]);
        var create = typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2);
        return (SqlException)create.Invoke(null, [errors, "10.0"])!;
    }
}
