import { createHash, randomUUID } from 'node:crypto'
import { getAwrAuthHeaders } from './awr-auth.js'
import { createAwrTimeoutSignal } from './awr-timeout.js'
import {
  PROTECTED_EXTRACTION_CONTRACT_VERSION,
  composeExtractionPrompt,
  createExtractionRecord,
  mapExtractionFailure,
  mapExtractionSuccess,
  type ExtractionValidationResult,
  validateExtractionResponse,
} from './extraction-contract.js'
import { extractionInstructionRepo, jobSpecExtractionRepo } from '../storage/repos/index.js'
import type { User } from '../middleware/auth.js'
import type {
  ExtractionFailure,
  ExtractionInstructionVersion,
  ExtractionResult,
  JobSpecExtractionRecord,
} from '../../src/types/index.js'
import { addScoringProfile } from './scoring-profile.js'

export interface JobSpecExtractionExecutionContext {
  fileName: string
  contentBase64: string
  mimeType?: string
  purpose: JobSpecExtractionRecord['purpose']
  actor?: User
  instructionVersionId?: string
}

export type JobSpecExtractionExecutionOutcome =
  | { ok: true; result: ExtractionResult; record: JobSpecExtractionRecord; instruction: ExtractionInstructionVersion }
  | { ok: false; result: ExtractionFailure; record: JobSpecExtractionRecord; instruction: ExtractionInstructionVersion }

export const jobSpecExtractionService = {
  async execute(context: JobSpecExtractionExecutionContext): Promise<JobSpecExtractionExecutionOutcome> {
    const endpoint = process.env.AWR_SEQ_API_ENDPOINT || ''
    if (!endpoint) {
      throw new Error('AWR_SEQ_API_ENDPOINT is not configured')
    }

    const instruction = context.instructionVersionId
      ? await extractionInstructionRepo.getById(context.instructionVersionId)
      : await extractionInstructionRepo.getActive()
    if (!instruction) {
      throw new Error('No active extraction instruction is available')
    }

    const documentBuffer = Buffer.from(context.contentBase64, 'base64')
    const formData = new FormData()
    formData.append('promptFile', new Blob([composeExtractionPrompt(instruction.instructionText)], { type: 'text/plain' }), 'job-spec-extraction-prompt.md')
    formData.append('specFile', new Blob([documentBuffer], { type: context.mimeType || 'application/octet-stream' }), context.fileName)
    addScoringProfile(formData, {
      modelId: instruction.modelId,
      reasoningLevel: instruction.reasoningLevel,
    })

    const awrHeaders = await getAwrAuthHeaders(context.actor ? { username: context.actor.username, role: context.actor.role } : undefined)
    const timeout = createAwrTimeoutSignal()
    const response = await fetch(`${endpoint}/assess/passthrough`, {
      method: 'POST',
      headers: awrHeaders,
      body: formData,
      signal: timeout.signal,
    }).finally(() => timeout.dispose())

    if (!response.ok) {
      const errorText = await response.text()
      throw new Error(errorText || `Extraction failed with status ${response.status}`)
    }

    const rawResponse = await response.text()
    const validation = validateExtractionResponse(rawResponse)
    const record = await persistExtractionRecord(context, instruction, rawResponse, validation, documentBuffer)

    if (!validation.isValid || !validation.document || !validation.rubric) {
      return {
        ok: false,
        result: mapExtractionFailure({
          extractionId: record.id,
          instructionVersionId: instruction.id,
          protectedContractVersion: PROTECTED_EXTRACTION_CONTRACT_VERSION,
          findings: validation.findings,
        }),
        record,
        instruction,
      }
    }

    return {
      ok: true,
      result: mapExtractionSuccess({
        extractionId: record.id,
        instructionVersionId: instruction.id,
        protectedContractVersion: PROTECTED_EXTRACTION_CONTRACT_VERSION,
        findings: validation.findings,
        rubric: validation.rubric,
        document: validation.document,
      }),
      record,
      instruction,
    }
  },
}

async function persistExtractionRecord(
  context: JobSpecExtractionExecutionContext,
  instruction: ExtractionInstructionVersion,
  rawResponse: string,
  validation: ExtractionValidationResult,
  documentBuffer: Buffer,
) {
  const record = createExtractionRecord({
    id: randomUUID(),
    purpose: context.purpose,
    instructionVersionId: instruction.id,
    protectedContractVersion: PROTECTED_EXTRACTION_CONTRACT_VERSION,
    sourceFileName: context.fileName,
    sourceMimeType: context.mimeType || 'application/octet-stream',
    sourceSha256: createHash('sha256').update(documentBuffer).digest('hex'),
    rawResponse,
    normalizedResponseJson: validation.document ? JSON.stringify(validation.document) : null,
    validationStatus: validation.validationStatus,
    validationFindings: validation.findings,
    createdBy: context.actor?.userId || context.actor?.username || 'unknown',
    correlationId: randomUUID(),
  })
  await jobSpecExtractionRepo.create(record)
  return record
}
