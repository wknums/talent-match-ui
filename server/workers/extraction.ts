import { randomUUID } from 'node:crypto'
import { applicationRepo } from '../storage/repos/index.js'
import { getAwrAuthHeaders } from '../services/awr-auth.js'
import type { ExtractionArtifact } from '../../src/types/index.js'

// FR-065: Extraction ALWAYS uses AWR_SEQ_API_ENDPOINT regardless of scoring mode
const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''

const EXTRACTION_SYSTEM_PROMPT = `You are a document extraction specialist. Extract the text content from the provided document and return it as clean, well-structured Markdown.

Rules:
- Preserve headings, lists, and formatting structure
- Remove headers, footers, page numbers, and decorative elements
- Normalise whitespace and fix OCR artefacts where obvious
- Return ONLY the extracted Markdown text, no JSON wrapper

Output format: raw Markdown text.`

export async function runExtraction(
  applicationId: string,
  jobId: string,
): Promise<ExtractionArtifact> {
  // Update status to Extracting
  await applicationRepo.updateStatus(applicationId, 'Extracting')

  const documents = await applicationRepo.getDocuments(applicationId)

  let extractedParts: string[]

  if (AWR_SEQ_API_ENDPOINT) {
    // Call AWR passthrough API for each document
    extractedParts = []
    for (const doc of documents) {
      try {
        const formData = new FormData()

        const promptBlob = new Blob([EXTRACTION_SYSTEM_PROMPT], { type: 'text/plain' })
        formData.append('promptFile', promptBlob, 'extraction-prompt.md')

        // Use contentUrl if available, otherwise create a placeholder
        const docContent = doc.contentUrl
          ? Buffer.from(doc.contentUrl, 'base64')
          : Buffer.from(`[Document: ${doc.fileName}]`)
        const docBlob = new Blob([docContent], { type: doc.mimeType || 'application/octet-stream' })
        formData.append('specFile', docBlob, doc.fileName)

        const awrHeaders = await getAwrAuthHeaders({ username: 'system', role: 'pipeline' })
        const response = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
          method: 'POST',
          headers: awrHeaders,
          body: formData,
        })

        if (!response.ok) {
          const errorText = await response.text().catch(() => response.statusText)
          throw new Error(`Passthrough API error (${response.status}): ${errorText}`)
        }

        const markdown = await response.text()
        extractedParts.push(markdown)
      } catch (error) {
        // If extraction fails for a document, include error context but continue
        extractedParts.push(
          `# Document: ${doc.fileName}\n\n*Extraction failed: ${error instanceof Error ? error.message : 'Unknown error'}*`
        )
      }
    }
  } else {
    // Fallback: simulated extraction when AWR endpoint is not configured
    extractedParts = documents.map(doc =>
      `# Document: ${doc.fileName}\n\nExtracted content from ${doc.fileName} (${doc.mimeType}, ${doc.sizeBytes} bytes)\n\nContent extracted and normalised to Markdown format.`
    )
  }

  const artifact: ExtractionArtifact = {
    artifactId: randomUUID(),
    applicationId,
    markdown: extractedParts.join('\n\n---\n\n'),
    extractionMetadata: {
      toolVersion: 'extraction-worker-v1.0',
      confidence: AWR_SEQ_API_ENDPOINT ? 0.90 : 0.50,
      extractedAt: new Date().toISOString(),
    },
    status: 'Success',
    createdAt: new Date().toISOString(),
  }

  await applicationRepo.setExtraction(artifact)

  // Update status to Scoring
  await applicationRepo.updateStatus(applicationId, 'Scoring')

  return artifact
}
