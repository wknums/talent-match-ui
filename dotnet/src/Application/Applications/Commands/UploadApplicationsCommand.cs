using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using System.IO;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Application.Prompts.Services;

namespace TalentMatch.Application.Applications.Commands;

public record UploadedFile(string FileName, string FileType, long FileSize, string ContentBase64, string Fingerprint);

public record UploadApplicationsCommand(
    string JobId,
    List<UploadedFile> Files,
    bool AllowDuplicates = false) : IRequest<List<Domain.Entities.Application>>;

public class UploadApplicationsCommandHandler : IRequestHandler<UploadApplicationsCommand, List<Domain.Entities.Application>>
{
    private readonly IApplicationRepository _applicationRepository;
    private readonly IJobRepository _jobRepository;
    private readonly IScoringQueueSignal? _queueSignal;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizations;
    private readonly IScoringPromptRepository? _prompts;
    private readonly IPromptProfileGuard? _profileGuard;

    public UploadApplicationsCommandHandler(
        IApplicationRepository applicationRepository,
        IJobRepository jobRepository,
        IScoringQueueSignal? queueSignal = null,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizations = null,
        IScoringPromptRepository? prompts = null,
        IPromptProfileGuard? profileGuard = null)
    {
        _applicationRepository = applicationRepository;
        _jobRepository = jobRepository;
        _queueSignal = queueSignal;
        _currentUser = currentUser;
        _organizations = organizations;
        _prompts = prompts;
        _profileGuard = profileGuard;
    }

    public async Task<List<Domain.Entities.Application>> Handle(UploadApplicationsCommand request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");
        await JobAuthorization.EnsureCanMutateAsync(job, _currentUser, _organizations, cancellationToken);
        if (_prompts is not null && _profileGuard is not null)
        {
            var productionPrompt = await _prompts.GetProductionApprovedForJobAsync(
                request.JobId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A production-approved prompt is required before applications can be queued.");
            await _profileGuard.EnsureProductionReadyAsync(
                productionPrompt, cancellationToken);
        }

        var applications = new List<Domain.Entities.Application>();
        var createdApplicationIds = new List<string>();
        var requestFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in request.Files)
            {
                if (!request.AllowDuplicates)
                {
                    var repeatedInRequest = !requestFingerprints.Add(file.Fingerprint);
                    var existingDocument = repeatedInRequest
                        ? null
                        : await _applicationRepository.FindDocumentByFingerprintAsync(
                            request.JobId, file.Fingerprint, cancellationToken);
                    if (repeatedInRequest || existingDocument is not null)
                        continue;
                }

                var app = new Domain.Entities.Application
                {
                    JobId = request.JobId,
                    CandidateRef = $"candidate-{Guid.NewGuid().ToString("N")[..8]}",
                    CandidateName = DeriveCandidateName(file.FileName),
                    Status = "Uploading"
                };
                await _applicationRepository.AddAsync(app, cancellationToken);
                createdApplicationIds.Add(app.Id);

                try
                {
                    var doc = new ApplicationDocument
                    {
                        ApplicationId = app.Id,
                        FileName = file.FileName,
                        FileType = file.FileType,
                        FileSize = file.FileSize,
                        Fingerprint = file.Fingerprint,
                        ContentBase64 = file.ContentBase64
                    };
                    await _applicationRepository.AddDocumentAsync(doc, cancellationToken);
                }
                catch (Exception ex)
                {
                    await SafeDeleteApplicationAsync(app.Id, cancellationToken);
                    createdApplicationIds.Remove(app.Id);
                    throw new InvalidOperationException(
                        $"Failed to persist uploaded document '{file.FileName}' for application {app.Id}. Upload was rolled back.",
                        ex);
                }

                applications.Add(app);
            }

            await _applicationRepository.PublishUploadedAsync(createdApplicationIds, cancellationToken);
        }
        catch
        {
            foreach (var applicationId in createdApplicationIds)
            {
                await SafeDeleteApplicationAsync(applicationId, cancellationToken);
            }

            throw;
        }

        foreach (var application in applications)
            application.Status = "Queued";
        if (applications.Count > 0)
            _queueSignal?.Pulse();
        return applications;

        async Task SafeDeleteApplicationAsync(string applicationId, CancellationToken ct)
        {
            try
            {
                await _applicationRepository.DeleteAsync(applicationId, ct);
            }
            catch
            {
                // Best-effort cleanup: preserve the original exception path.
            }
        }
    }

    private static string DeriveCandidateName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();

        return string.IsNullOrWhiteSpace(name) ? "Unknown Applicant" : name;
    }
}
