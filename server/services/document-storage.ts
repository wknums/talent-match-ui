import { createHash, randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { appDocumentsKey, jobApplicationsKey } from '../storage/kv-keys.js'
import { getArray } from '../storage/kv-helpers.js'
import type { ApplicationDocument, Application } from '../../src/types/index.js'

export function createDocumentStorageService(storage: StorageProvider) {
  return {
    async computeFingerprint(content: string): Promise<string> {
      return createHash('sha256').update(content).digest('hex')
    },

    async checkDuplicate(jobId: string, fingerprint: string): Promise<ApplicationDocument | null> {
      const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
      for (const app of apps) {
        const docs = await getArray<ApplicationDocument>(storage, appDocumentsKey(app.applicationId))
        const dup = docs.find(d => d.sha256 === fingerprint)
        if (dup) return dup
      }
      return null
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

      const existing = await getArray<ApplicationDocument>(storage, appDocumentsKey(applicationId))
      existing.push(doc)
      await storage.set(appDocumentsKey(applicationId), existing)

      return doc
    },
  }
}
