using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Application.Rubrics.Models;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.JobExtraction.Services;

public sealed class JobSpecExtractionOrchestrator : IExtractionInstructionValidationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IExtractionInstructionRepository _instructionRepository;
    private readonly IJobSpecExtractionRepository _extractionRepository;
    private readonly IJobSpecExtractionTransport _transport;
    private readonly JobSpecExtractionContractValidator _validator;
    private readonly ICurrentUserService _currentUser;

    public JobSpecExtractionOrchestrator(
        IExtractionInstructionRepository instructionRepository,
        IJobSpecExtractionRepository extractionRepository,
        IJobSpecExtractionTransport transport,
        JobSpecExtractionContractValidator validator,
        ICurrentUserService currentUser)
    {
        _instructionRepository = instructionRepository;
        _extractionRepository = extractionRepository;
        _transport = transport;
        _validator = validator;
        _currentUser = currentUser;
    }

    public async Task<JobSpecExtractionExecutionResult> ExecuteAsync(
        ExtractDocumentRequestModel request,
        string purpose,
        string? instructionVersionId = null,
        CancellationToken cancellationToken = default)
    {
        var instruction = instructionVersionId is null
            ? await _instructionRepository.GetActiveAsync(cancellationToken)
            : await _instructionRepository.GetByIdAsync(instructionVersionId, cancellationToken);

        if (instruction is null)
            throw new InvalidOperationException("No active extraction instruction is available.");

        byte[] documentBytes;
        try
        {
            documentBytes = Convert.FromBase64String(request.Content);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Invalid base64 document content.", exception);
        }

        var rawResponse = await _transport.ExtractAsync(
            documentBytes,
            request.FileName,
            request.MimeType,
            ComposePrompt(instruction.InstructionText),
            cancellationToken);

        var validation = _validator.Validate(rawResponse);
        var extractionId = Guid.NewGuid().ToString();
        var extractionRecord = new JobSpecExtraction
        {
            Id = extractionId,
            Purpose = purpose,
            InstructionVersionId = instruction.Id,
            ProtectedContractVersion = instruction.ProtectedContractVersion,
            SourceFileName = request.FileName,
            SourceMimeType = request.MimeType,
            SourceSha256 = Convert.ToHexStringLower(SHA256.HashData(documentBytes)),
            RawResponse = rawResponse,
            NormalizedResponseJson = validation.Document is null ? null : JsonSerializer.Serialize(validation.Document, JsonOptions),
            ValidationStatus = validation.ValidationStatus,
            ValidationFindingsJson = JsonSerializer.Serialize(validation.Findings, JsonOptions),
            CreatedBy = _currentUser.UserId ?? _currentUser.Username ?? "unknown",
            CompletedAt = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid().ToString(),
        };

        await _extractionRepository.AddAsync(extractionRecord, cancellationToken);

        return new JobSpecExtractionExecutionResult(
            extractionId,
            instruction.Id,
            instruction.ProtectedContractVersion,
            validation.ValidationStatus,
            validation.Findings,
            validation.Document?.JobTitle,
            validation.Document?.JobDescription,
            validation.Document?.Department,
            validation.Document?.Organization,
            validation.Rubric);
    }

    public Task<JobSpecExtractionExecutionResult> ValidateAsync(
        string instructionVersionId,
        ExtractDocumentRequestModel request,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(request, "instruction_validation", instructionVersionId, cancellationToken);

    private static string ComposePrompt(string editableInstructionText)
        => $"""
{editableInstructionText.Trim()}

## Protected Output Contract (Mandatory)

Return JSON only. Do not include Markdown fences or commentary.

Required top-level fields:
- job_title: string | null
- job_description: string | null
- department: string | null
- organization: string | null
- requirements: array of individually assessable requirements
- rubric: object with has_rubric_in_doc, categories, and weights_sum_to_1_0=true

Requirement shape:
- id: stable non-empty string
- text: one independently assessable requirement
- requirement_type: must_have | desired | experience | responsibility | other
- category_id: exact category id, or {RubricSpecialCategoryIds.NeedsReview} when the assignment needs review
- source_text: original wording from the source
- source_location: optional location string or null
- duplicate_of: nullable id when the requirement is a genuine duplicate of an earlier item
- needs_review: boolean

Rubric category shape:
- id: stable non-empty string
- name: category display name
- weight: numeric weight between 0 and 1
- description: non-empty string
- source: doc | generated

Mandatory constraints:
- Split compound bullets into separate requirements.
- Preserve every independently assessable requirement as its own item.
- Consolidate only true duplicates and represent them with duplicate_of.
- Keep ambiguous assignments as explicit needs_review items instead of discarding them.
- Ensure category weights sum to exactly 1.0 and set weights_sum_to_1_0=true only when they do.
""";
}
