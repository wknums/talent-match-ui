// Blob storage abstraction for platform-mode CV uploads.
//
// Background:
//   - Sequential mode keeps CVs inline in talentmatch.DocumentBlobs.Content and
//     hands bytes to the engine over multipart. No blob storage needed.
//   - Platform mode submits a JSON batch with blob URIs; the platform reads CVs
//     via its own managed identity from the cv-uploads container.
//   - Per constitution: SAS tokens are forbidden — RBAC via managed identity only.
//
// This module provides a small interface so the rest of the code never imports
// @azure/storage-blob directly. The Azure provider is lazy-imported so
// sequential-mode deployments never load it.

import { createHash } from 'node:crypto'

const AWR_BLOB_STORAGE_ACCOUNT = process.env.AWR_BLOB_STORAGE_ACCOUNT || ''
const AWR_BLOB_CONTAINER = process.env.AWR_BLOB_CONTAINER || 'cv-uploads'

export interface BlobUploadInput {
  jobId: string
  applicationId: string
  documentId: string
  fileName: string
  mimeType: string
  bytes: Buffer
}

export interface BlobUploadResult {
  /** Canonical https URI when the blob lives in Azure; null for inline mode. */
  blobUri: string | null
  /** Hex sha256 of the bytes. Always populated. */
  sha256: string
}

export interface BlobStore {
  /** True iff this provider actually writes to Azure Blob Storage. */
  readonly isRemote: boolean
  /** Idempotent: returns existing blob if already uploaded with same sha256. */
  put(input: BlobUploadInput): Promise<BlobUploadResult>
}

function sha256Hex(bytes: Buffer): string {
  return createHash('sha256').update(bytes).digest('hex')
}

function extFromName(name: string): string {
  const m = /\.([A-Za-z0-9]+)$/.exec(name)
  return m ? m[1].toLowerCase() : 'bin'
}

class InlineBlobStore implements BlobStore {
  readonly isRemote = false
  async put(input: BlobUploadInput): Promise<BlobUploadResult> {
    return { blobUri: null, sha256: sha256Hex(input.bytes) }
  }
}

class AzureBlobStore implements BlobStore {
  readonly isRemote = true
  private readonly account: string
  private readonly container: string
  // Lazily initialised on first use so the SDK is only loaded when needed.
  private containerClient: any = null

  constructor(account: string, container: string) {
    this.account = account
    this.container = container
  }

  private async getContainerClient(): Promise<any> {
    if (this.containerClient) return this.containerClient
    // Lazy import so sequential-only deployments never load the SDK.
    const { BlobServiceClient } = await import('@azure/storage-blob')
    const { DefaultAzureCredential } = await import('@azure/identity')
    const url = `https://${this.account}.blob.core.windows.net`
    const svc = new BlobServiceClient(url, new DefaultAzureCredential())
    this.containerClient = svc.getContainerClient(this.container)
    return this.containerClient
  }

  async put(input: BlobUploadInput): Promise<BlobUploadResult> {
    const sha = sha256Hex(input.bytes)
    const ext = extFromName(input.fileName)
    const blobName = `${input.jobId}/${input.applicationId}/${input.documentId}.${ext}`

    const container = await this.getContainerClient()
    const blob = container.getBlockBlobClient(blobName)

    // Idempotency: if blob already exists with matching sha256 metadata, skip upload.
    try {
      const props = await blob.getProperties()
      const existingSha = props.metadata?.sha256
      if (existingSha === sha) {
        return { blobUri: blob.url, sha256: sha }
      }
    } catch (err: any) {
      if (err?.statusCode !== 404) throw err
    }

    await blob.uploadData(input.bytes, {
      blobHTTPHeaders: { blobContentType: input.mimeType || 'application/octet-stream' },
      metadata: { sha256: sha },
    })

    // Sibling .sha256 marker (contract §2.1) — small text blob with the hash.
    const marker = container.getBlockBlobClient(`${blobName}.sha256`)
    await marker.uploadData(Buffer.from(sha, 'utf8'), {
      blobHTTPHeaders: { blobContentType: 'text/plain' },
    })

    return { blobUri: blob.url, sha256: sha }
  }
}

let cached: BlobStore | null = null

export function getBlobStore(): BlobStore {
  if (cached) return cached
  if (AWR_BLOB_STORAGE_ACCOUNT) {
    cached = new AzureBlobStore(AWR_BLOB_STORAGE_ACCOUNT, AWR_BLOB_CONTAINER)
  } else {
    cached = new InlineBlobStore()
  }
  return cached
}

/** Test/reset hook. */
export function _resetBlobStoreForTests(): void {
  cached = null
}
