using System.Data;
using Microsoft.EntityFrameworkCore;

namespace TalentMatch.Infrastructure.Persistence;

// Azure SQL enables EnableRetryOnFailure, and the resulting SqlServerRetryingExecutionStrategy
// refuses user-initiated transactions unless the whole unit is replayable. Every explicit
// transaction therefore has to run inside the execution strategy.
internal static class AppDbContextTransactionExtensions
{
    public static Task ExecuteInTransactionAsync(
        this AppDbContext context,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        IsolationLevel? isolationLevel = null)
        => context.ExecuteInTransactionAsync(
            async token =>
            {
                await operation(token);
                return true;
            },
            cancellationToken,
            isolationLevel);

    public static Task<TResult> ExecuteInTransactionAsync<TResult>(
        this AppDbContext context,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken,
        IsolationLevel? isolationLevel = null)
        => context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // A retry replays the delegate, so discard anything the previous attempt staged.
            context.ChangeTracker.Clear();
            await using var transaction = isolationLevel is null
                ? await context.Database.BeginTransactionAsync(cancellationToken)
                : await context.Database.BeginTransactionAsync(isolationLevel.Value, cancellationToken);
            try
            {
                var result = await operation(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                context.ChangeTracker.Clear();
                throw;
            }
        });
}
