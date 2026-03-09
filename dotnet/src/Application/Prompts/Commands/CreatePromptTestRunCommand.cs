using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record TestRunFile(string FileName, string FileType, long FileSize, string ContentBase64, string Fingerprint);

public record CreatePromptTestRunCommand(
    string JobId,
    string PromptId,
    List<TestRunFile> Files
) : IRequest<PromptTestRun>;

public class CreatePromptTestRunCommandHandler : IRequestHandler<CreatePromptTestRunCommand, PromptTestRun>
{
    private readonly IPromptTestRunRepository _testRunRepo;
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IApplicationRepository _applicationRepo;

    public CreatePromptTestRunCommandHandler(
        IPromptTestRunRepository testRunRepo,
        IScoringPromptRepository promptRepo,
        IApplicationRepository applicationRepo)
    {
        _testRunRepo = testRunRepo;
        _promptRepo = promptRepo;
        _applicationRepo = applicationRepo;
    }

    public async Task<PromptTestRun> Handle(CreatePromptTestRunCommand request, CancellationToken ct)
    {
        var prompt = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        if (prompt.JobId != request.JobId)
            throw new InvalidOperationException("Prompt does not belong to the specified job");

        var testRun = new PromptTestRun
        {
            JobId = request.JobId,
            PromptId = request.PromptId,
            Status = "pending_review"
        };

        var applicationIds = new List<string>();

        // Create Application records with TestRunId set (marking them as test cases)
        foreach (var file in request.Files)
        {
            var app = new Domain.Entities.Application
            {
                JobId = request.JobId,
                Status = "Queued",
                TestRunId = testRun.Id
            };
            await _applicationRepo.AddAsync(app, ct);

            var doc = new ApplicationDocument
            {
                ApplicationId = app.Id,
                FileName = file.FileName,
                FileType = file.FileType,
                FileSize = file.FileSize,
                ContentBase64 = file.ContentBase64,
                Fingerprint = file.Fingerprint
            };
            await _applicationRepo.AddDocumentAsync(doc, ct);

            applicationIds.Add(app.Id);
        }

        testRun.ApplicationIdsJson = JsonSerializer.Serialize(applicationIds);
        await _testRunRepo.AddAsync(testRun, ct);

        return testRun;
    }
}
