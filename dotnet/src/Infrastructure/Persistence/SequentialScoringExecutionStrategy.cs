using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TalentMatch.Infrastructure.Persistence;

public class SequentialScoringExecutionStrategy(DbContext context)
    : SqlServerRetryingExecutionStrategy(context, 6, TimeSpan.FromSeconds(15), [1205])
{
    protected override TimeSpan? GetNextDelay(Exception lastException)
    {
        if (!IsDeadlock(lastException))
            return base.GetNextDelay(lastException);

        var retryIndex = ExceptionsEncountered.Count - 1;
        if (retryIndex >= MaxRetryCount)
            return null;

        // Equal jitter avoids immediate retries; the six waits total less than 3.5 s.
        var ceilingMs = Math.Min(1000, 100 * (1 << retryIndex));
        return TimeSpan.FromMilliseconds(ceilingMs * (0.5 + Random.Shared.NextDouble() * 0.5));
    }

    private static bool IsDeadlock(Exception exception) =>
        exception is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => error.Number == 1205)
        || exception.InnerException is not null && IsDeadlock(exception.InnerException);
}
