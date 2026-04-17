import { createHash, randomUUID } from 'node:crypto'
import { applicationRepo } from '../storage/repos/index.js'
import type { ApplicationDocument } from '../../src/types/index.js'

export const documentStorageService = {
  async computeFingerprint(content: string): Promise<string> {
    return createHash('sha256').update(content).digest('hex')
  },

  async checkDuplicate(jobId: string, fingerprint: string): Promise<ApplicationDocument | null> {
    const dup = await applicationRepo.findDuplicateFingerprint(jobId, fingerprint)
    return dup ?? null
  },

  async storeDocument(
    applicationId: string,
    fileName: string,
    content: string,
    mimeType: string,
    sizeBytes: number,
  ): Promise<ApplicationDocument> {
    const fingerprint = createHash('sha256').update(content).digest('hex')

    const doc: ApplicationDocument = {
      documentId: randomUUID(),
      applicationId,
      fileName,
      mimeType,
      sizeBytes,
      sha256: fingerprint,
      uploadedAt: new Date().toISOString(),
    }

    await applicationRepo.createDocument(doc)
    return doc
  },
}

/** @deprecated Use documentStorageService directly */
export function createDocumentStorageService(_storage?: unknown) {
  return documentStorageService
}
