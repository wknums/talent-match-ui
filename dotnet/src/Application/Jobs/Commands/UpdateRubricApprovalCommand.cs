using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record UpdateRubricApprovalCommand(
    string JobId,
    string Status // "approved" or "draft"
) : IRequest<RubricApprovalResult>;

public record RubricApprovalResult(
    string VersionId,
    string RubricApprovalStatus,
    DateTime UpdatedAt
);

public class UpdateRubricApprovalCommandHandler : IRequestHandler<UpdateRubricApprovalCommand, RubricApprovalResult>
{
    private readonly IJobRepository _jobRepository;
    private readonly IProcessingEventRepository _eventRepository;
    private readonly ICurrentUserService _currentUser;

    public UpdateRubricApprovalCommandHandler(
        IJobRepository jobRepository,
        IProcessingEventRepository eventRepository,
        ICurrentUserService currentUser)
    {
        _jobRepository = jobRepository;
        _eventRepository = eventRepository;
        _currentUser = currentUser;
    }

    public async Task<RubricApprovalResult> Handle(UpdateRubricApprovalCommand request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");

        var currentVersion = job.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
            ?? throw new InvalidOperationException($"No current config version found for job '{request.JobId}'.");

        var newStatus = request.Status?.ToLowerInvariant() ?? "approved";
        if (newStatus != "approved" && newStatus != "draft")
            throw new InvalidOperationException($"Invalid rubric approval status: '{request.Status}'. Must be 'approved' or 'draft'.");

        if (string.Equals(currentVersion.RubricApprovalStatus, newStatus, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Rubric is already '{newStatus}'.");

        currentVersion.RubricApprovalStatus = newStatus;
        var now = DateTime.UtcNow;

        await _jobRepository.UpdateAsync(job, cancellationToken);

        // Create audit entry
        var eventType = newStatus == "approved" ? "rubric.approved" : "rubric.reverted-to-draft";
        await _eventRepository.AddAsync(new ProcessingEvent
        {
            Actor = _currentUser.UserId ?? "system",
            EventType = eventType,
            EntityType = "job",
            EntityId = request.JobId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { VersionId = currentVersion.Id, NewStatus = newStatus }),
            Timestamp = now
        }, cancellationToken);

        return new RubricApprovalResult(currentVersion.Id, newStatus, now);
    }
}
