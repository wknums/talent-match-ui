namespace TalentMatch.Infrastructure.Services;

// The scoring service answered 200 with no body. Deriving from HttpRequestException keeps the application-level
// scoring retry (which already retries HttpRequestException) working if every per-run retry is used up.
public sealed class EmptyScoringResponseException(string fileName, int runNumber, int totalRuns)
    : HttpRequestException($"The scoring service returned an empty response for run {runNumber} of {totalRuns} ({fileName}).");
