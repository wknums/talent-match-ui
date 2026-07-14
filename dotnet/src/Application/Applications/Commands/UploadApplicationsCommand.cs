using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using System.IO;

namespace TalentMatch.Application.Applications.Commands;

public record UploadedFile(string FileName, string FileType, long FileSize, string ContentBase64, string Fingerprint);

public record UploadApplicationsCommand(string JobId, List<UploadedFile> Files) : IRequest<List<Domain.Entities.Application>>;

public class UploadApplicationsCommandHandler : IRequestHandler<UploadApplicationsCommand, List<Domain.Entities.Application>>
{
    private readonly IApplicationRepository _applicationRepository;
    private readonly IJobRepository _jobRepository;

    public UploadApplicationsCommandHandler(IApplicationRepository applicationRepository, IJobRepository jobRepository)
    {
        _applicationRepository = applicationRepository;
        _jobRepository = jobRepository;
    }

    public async Task<List<Domain.Entities.Application>> Handle(UploadApplicationsCommand request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");

        var applications = new List<Domain.Entities.Application>();
        var createdApplicationIds = new List<string>();

        try
        {
            foreach (var file in request.Files)
            {
                var app = new Domain.Entities.Application
                {
                    JobId = request.JobId,
                    CandidateRef = $"candidate-{Guid.NewGuid().ToString("N")[..8]}",
                    CandidateName = DeriveCandidateName(file.FileName),
                    Status = "Queued"
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

            return applications;
        }
        catch
        {
            foreach (var applicationId in createdApplicationIds)
            {
                await SafeDeleteApplicationAsync(applicationId, cancellationToken);
            }

            throw;
        }

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
