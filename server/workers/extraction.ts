import { randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { appDocumentsKey, appExtractionKey, jobApplicationsKey } from '../storage/kv-keys.js'
import { getArray, setArray } from '../storage/kv-helpers.js'
import type { Application, ApplicationDocument, ExtractionArtifact } from '../../src/types/index.js'

export async function runExtraction(
  storage: StorageProvider,
  applicationId: string,
  jobId: string,
): Promise<ExtractionArtifact> {
  // Update status to Extracting
  const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
  const appIndex = apps.findIndex(a => a.applicationId === applicationId)
  if (appIndex !== -1) {
    apps[appIndex] = { ...apps[appIndex], status: 'Extracting' }
    await setArray(storage, jobApplicationsKey(jobId), apps)
  }

  const documents = await getArray<ApplicationDocument>(storage, appDocumentsKey(applicationId))

  // Simulate extraction - in production this would call POST /api/llm
  const extractedParts = documents.map(doc =>
    `# Document: ${doc.fileName}\n\nExtracted content from ${doc.fileName} (${doc.mimeType}, ${doc.sizeBytes} bytes)\n\nContent extracted and normalised to Markdown format.`
  )

  const artifact: ExtractionArtifact = {
    artifactId: randomUUID(),
    applicationId,
    markdown: extractedParts.join('\n\n---\n\n'),
    extractionMetadata: {
      toolVersion: 'extraction-worker-v1.0',
      confidence: 0.85 + Math.random() * 0.15,
      extractedAt: new Date().toISOString(),
    },
    status: 'Success',
    createdAt: new Date().toISOString(),
  }

  await storage.set(appExtractionKey(applicationId), artifact)

  // Update status to Scoring
  const updatedApps = await getArray<Application>(storage, jobApplicationsKey(jobId))
  const idx = updatedApps.findIndex(a => a.applicationId === applicationId)
  if (idx !== -1) {
    updatedApps[idx] = { ...updatedApps[idx], status: 'Scoring' }
    await setArray(storage, jobApplicationsKey(jobId), updatedApps)
  }

  return artifact
}
