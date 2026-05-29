namespace TalentMatch.Domain.Entities;

/// <summary>
/// Job-level rollup of platform-mode batched scoring progress. One row per job
/// in platform mode. See specs/008-platform-mode-shift/platform-contract.md §3.
/// </summary>
public class ScoringJobProgress
{
    public string JobId { get; set; } = string.Empty;
    public int TotalApps { get; set; }
    public int BatchesPending { get; set; }
    public int BatchesSubmitted { get; set; }
    public int BatchesCompleted { get; set; }
    public int BatchesFailed { get; set; }
    public int AppsCompleted { get; set; }
    public int AppsFailed { get; set; }
    public bool CancelRequested { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
