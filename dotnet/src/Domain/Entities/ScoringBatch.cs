namespace TalentMatch.Domain.Entities;

/// <summary>
/// Platform-mode scoring batch. See specs/008-platform-mode-shift/platform-contract.md §3.
/// Each row represents an enqueued submission of 1..K applications to the platform
/// scoring API. The reconciler hosted service drives state transitions:
///   pending → submitted → completed | failed | cancelled.
/// </summary>
public class ScoringBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobId { get; set; } = string.Empty;
    public string PromptVersionId { get; set; } = string.Empty;
    /// <summary>JSON array of application IDs included in this batch.</summary>
    public string ApplicationIdsJson { get; set; } = "[]";
    public int RunCount { get; set; } = 1;
    /// <summary>pending | submitting | submitted | completed | failed | cancelling | cancelled</summary>
    public string Status { get; set; } = "pending";
    public string? SubmissionId { get; set; }
    public string? PollUrl { get; set; }
    public int Attempt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public DateTime NextPollAt { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeasedUntil { get; set; }
    public string? ResultJson { get; set; }
    public bool CancelRequested { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
