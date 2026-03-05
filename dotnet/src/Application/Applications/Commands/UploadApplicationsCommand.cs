using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

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

        foreach (var file in request.Files)
        {
            var app = new Domain.Entities.Application
            {
                JobId = request.JobId,
                Status = "Queued"
            };
            await _applicationRepository.AddAsync(app, cancellationToken);

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

            applications.Add(app);
        }

        return applications;
    }
}
