import type { organizationRepo } from './repos/organization-repo.js'
import type { roleAssignmentRepo } from './repos/role-assignment-repo.js'
import type { userRepo } from './repos/user-repo.js'
import type { accessManagementRepo } from './repos/access-management-repo.js'
import type { extractionInstructionRepo } from './repos/extraction-instruction-repo.js'
import type { jobSpecExtractionRepo } from './repos/job-spec-extraction-repo.js'
import type {
  CanonicalApiError,
  CreateUploadItem,
  UploadItem,
  UploadSessionDetail,
  UploadSessionSummary,
  UploadSettings,
  UpdateUploadItemStatusRequest,
  UpdateUploadSettingsRequest,
  EntraAccessUser,
  EntraAccessUserPage,
  NavigationAuditOutcome,
  NavigationAuditRequest,
  PutOrganizationAccessRequest,
  UpdateEntraAccessUserRequest,
} from '../../src/types/index.js'
import type { uploadRepo } from './repos/upload-repo.js'

export type {
  CanonicalApiError,
  EntraAccessUser,
  EntraAccessUserPage,
  NavigationAuditOutcome,
  NavigationAuditRequest,
  PutOrganizationAccessRequest,
  UpdateEntraAccessUserRequest,
}

export interface EntraAccessSearchOptions {
  search?: string
  organizationId?: string
  status?: 'pending' | 'active' | 'disabled'
  cursor?: string
  limit: number
}

export interface AccessMutationContext {
  actorObjectId: string
  correlationId: string
}

export interface AccessMutationResult {
  aggregate: EntraAccessUser
  changed: boolean
}

export interface StorageQueryResult {
  // Repository rows are driver-shaped until mapped at the storage boundary.
   
  recordset: any[]
  rowsAffected?: number[]
}

export interface StorageRequest {
  input(name: string, type: unknown, value: unknown): StorageRequest
  query(sqlText: string): Promise<StorageQueryResult>
}

export interface StorageExecutor {
  request(): StorageRequest
}

export interface StorageProvider {
  users: typeof userRepo
  organizations: typeof organizationRepo
  roleAssignments: typeof roleAssignmentRepo
  accessManagement: typeof accessManagementRepo
  extractionInstructions: typeof extractionInstructionRepo
  jobSpecExtractions: typeof jobSpecExtractionRepo
  uploads: typeof uploadRepo
}

export interface CreateStoredUploadSession {
  id: string
  jobId: string
  ownerActorId: string
  ownerDisplayName: string | null
  allowDuplicates: boolean
  fileConcurrency: number
  maxIndividualFileBytes: number
  maxInFlightBytes: number
  settingsConcurrencyVersion: number
  correlationId: string
  createdAt: string
}

export interface CreateStoredUploadItem extends CreateUploadItem {
  id: string
  sessionId: string
  status: UploadItem['status']
  outcomeCode: string | null
  outcomeMessage: string | null
  completedAt: string | null
  createdAt: string
}

export interface CompleteStoredUploadItem {
  sessionId: string
  itemId: string
  ownerActorId: string
  occurrenceKey: string
  fingerprint: string
  fileName: string
  mimeType: string
  rawSizeBytes: number
  contentBase64: string
  now: string
}

export interface SaveUploadSettingsInput extends UpdateUploadSettingsRequest {
  actorId: string
  now: string
}

export interface UploadRepository {
  getSettings(): Promise<UploadSettings>
  saveSettings(input: SaveUploadSettingsInput): Promise<UploadSettings>
  createSession(
    session: CreateStoredUploadSession,
    items: CreateStoredUploadItem[],
  ): Promise<UploadSessionDetail>
  getOwnedSession(
    sessionId: string,
    ownerActorId: string,
    includeItems?: boolean,
  ): Promise<UploadSessionDetail | undefined>
  listOwnedSessions(
    ownerActorId: string,
    jobId?: string,
    includeTerminal?: boolean,
  ): Promise<UploadSessionSummary[]>
  getOwnedItem(
    sessionId: string,
    itemId: string,
    ownerActorId: string,
  ): Promise<UploadItem | undefined>
  startItemAttempt(
    sessionId: string,
    itemId: string,
    ownerActorId: string,
    occurrenceKey: string,
    now: string,
  ): Promise<UploadItem>
  completeItem(input: CompleteStoredUploadItem): Promise<UploadItem>
  recordItemOutcome(
    sessionId: string,
    itemId: string,
    ownerActorId: string,
    status: Extract<UploadItem['status'], 'retrying' | 'failed' | 'interrupted'>,
    outcomeCode: string,
    outcomeMessage: string,
    now: string,
    lastHttpStatus?: number | null,
    nextRetryAt?: string | null,
  ): Promise<UploadItem>
  updateItemStatus(
    sessionId: string,
    itemId: string,
    ownerActorId: string,
    request: UpdateUploadItemStatusRequest,
    now: string,
  ): Promise<UploadItem>
  heartbeat(
    sessionId: string,
    ownerActorId: string,
    expectedConcurrencyVersion: number,
    now: string,
  ): Promise<UploadSessionSummary>
  reconcileStale(
    sessionId: string,
    ownerActorId: string,
    staleBefore: string,
    now: string,
  ): Promise<UploadSessionDetail | undefined>
}