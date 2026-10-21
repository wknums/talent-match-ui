export type JobStatus = 'Draft' | 'Active' | 'Processing' | 'Completed' | 'Archived'
export type ApplicationStatus = 'Queued' | 'Extracting' | 'ExtractionFailed' | 'Scoring' | 'ScoringFailed' | 'Aggregating' | 'Completed' | 'NeedsManualReview'
export type AggregationStrategy = 'median' | 'mean' | 'weighted'
export type Decision = 'Eligible' | 'Excluded' | 'NeedsManualReview'
export type RubricApprovalStatus = 'draft' | 'approved'
export type RubricSource = 'manual' | 'extracted' | 'generated'
export type ExtractionValidationSeverity = 'error' | 'warning'
export type ExtractionInstructionStatus = 'draft' | 'active' | 'retired'
export type ExtractionInstructionValidationStatus = 'unvalidated' | 'valid' | 'invalid'
export type RequirementType = 'must_have' | 'desired' | 'experience' | 'responsibility' | 'other'
export type RubricItemReviewStatus = 'confirmed' | 'needs_review'
export type RubricItemCreatedFrom = 'extracted' | 'manual' | 'legacy_conversion'
export type ReasoningEffort = 'low' | 'medium' | 'high'

export const OPTIONAL_UPLOAD_DEFAULTS = {
  fileConcurrency: 4,
  maxIndividualFileBytes: 4194304,
  maxInFlightBytes: 104857600,
} as const

export const SUPPORTED_UPLOAD_MIME_TYPES = [
  'application/pdf',
  'text/markdown',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'text/plain',
  'image/jpeg',
  'image/png',
] as const

export type SupportedUploadMimeType = typeof SUPPORTED_UPLOAD_MIME_TYPES[number]
export type UploadItemStatus =
  | 'waiting'
  | 'throttled'
  | 'uploading'
  | 'retrying'
  | 'succeeded'
  | 'skipped_duplicate'
  | 'failed'
  | 'interrupted'
export type UploadSessionStatus = 'active' | 'completed'

export interface UploadSettings {
  fileConcurrency: number
  maxIndividualFileBytes: number
  maxInFlightBytes: number
  concurrencyVersion: number
  persisted: boolean
  updatedAt: string | null
  updatedBy: string | null
}

export interface UpdateUploadSettingsRequest {
  fileConcurrency: number
  maxIndividualFileBytes: number
  maxInFlightBytes: number
  expectedConcurrencyVersion: number
}

export interface CreateUploadItem {
  occurrenceKey: string
  ordinal: number
  fileName: string
  mimeType: SupportedUploadMimeType
  rawSizeBytes: number
}

export interface CreateUploadSessionRequest {
  allowDuplicates: boolean
  items: CreateUploadItem[]
}

export interface UploadLimitsSnapshot {
  fileConcurrency: number
  maxIndividualFileBytes: number
  maxInFlightBytes: number
}

export interface UploadAggregateCounts {
  total: number
  waitingOrThrottled: number
  activeOrRetrying: number
  succeeded: number
  skipped: number
  failed: number
  interrupted: number
  terminal: number
}

export interface UploadItem {
  id: string
  sessionId: string
  occurrenceKey: string
  ordinal: number
  fileName: string
  mimeType: SupportedUploadMimeType
  rawSizeBytes: number
  status: UploadItemStatus
  attemptCount: number
  contentFingerprint: string | null
  applicationId: string | null
  outcomeCode: string | null
  outcomeMessage: string | null
  nextRetryAt: string | null
  createdAt: string
  updatedAt: string
  completedAt: string | null
  concurrencyVersion: number
}

export interface UploadSessionSummary {
  id: string
  jobId: string
  status: UploadSessionStatus
  allowDuplicates: boolean
  limits: UploadLimitsSnapshot
  counts: UploadAggregateCounts
  progressPercent: number
  correlationId: string
  createdAt: string
  startedAt: string | null
  lastHeartbeatAt: string
  completedAt: string | null
  concurrencyVersion: number
}

export interface UploadSessionDetail extends UploadSessionSummary {
  items: UploadItem[]
}

export interface UpdateUploadItemStatusRequest {
  occurrenceKey: string
  status: Extract<UploadItemStatus, 'waiting' | 'throttled' | 'retrying' | 'failed' | 'interrupted'>
  expectedConcurrencyVersion: number
  outcomeCode?: string | null
  outcomeMessage?: string | null
  nextRetryAt?: string | null
  transportAttemptCount?: number | null
}

export interface UploadValidationError extends CanonicalApiError {
  errors: Record<string, string[]>
}

export interface ReasoningModelOption {
  slot: string
  deployment: string
  isDefault: boolean
}

export interface ReasoningModelsResponse {
  defaultModel: string
  defaultReasoningEffort: ReasoningEffort
  supportedReasoningEfforts: ReasoningEffort[]
  models: ReasoningModelOption[]
}

export interface ExtractionValidationFinding {
  code: string
  severity: ExtractionValidationSeverity
  path: string
  message: string
}

export interface ExtractionInstructionVersion {
  id: string
  versionNumber: number
  instructionText: string
  modelId: string
  reasoningLevel: ReasoningEffort
  protectedContractVersion: string
  status: ExtractionInstructionStatus
  validationStatus: ExtractionInstructionValidationStatus
  changeNote?: string | null
  validationFindings: ExtractionValidationFinding[]
  createdAt: string
  createdBy: string
  validatedAt?: string | null
  validatedBy?: string | null
  activatedAt?: string | null
  activatedBy?: string | null
  concurrencyVersion: number
}

export interface ExtractionInstructionVersionDetail extends ExtractionInstructionVersion {
  protectedContract: Record<string, unknown>
}

export interface RubricCategoryV2 {
  id: string
  name: string
  weight: number
  description?: string | null
  order: number
}

export interface RubricItem {
  id: string
  categoryId: string
  text: string
  requirementType: RequirementType
  order: number
  sourceText?: string | null
  sourceLocation?: string | null
  sourceRequirementId?: string | null
  reviewStatus: RubricItemReviewStatus
  createdFrom: RubricItemCreatedFrom
}

export interface RubricEnvelope {
  schemaVersion: 'rubric-v2'
  legacySourceVersionId?: string | null
  categories: RubricCategoryV2[]
  items: RubricItem[]
}

export interface LegacyRubricState {
  isLegacy: boolean
  canConvert: boolean
  preview?: RubricEnvelope | null
}

export interface JobSpecExtractionRecord {
  id: string
  purpose: 'job_creation' | 'instruction_validation'
  instructionVersionId: string
  protectedContractVersion: string
  sourceFileName: string
  sourceMimeType: string
  sourceSha256: string
  rawResponse: string
  normalizedResponseJson?: string | null
  validationStatus: 'valid' | 'invalid'
  validationFindings: ExtractionValidationFinding[]
  jobId?: string | null
  jobConfigVersionId?: string | null
  createdAt: string
  createdBy: string
  completedAt: string
  correlationId: string
}

export interface ExtractionResult {
  extractionId: string
  instructionVersionId: string
  protectedContractVersion: string
  validationStatus: 'valid'
  validationFindings: ExtractionValidationFinding[]
  title?: string | null
  jobDescription?: string | null
  department?: string | null
  organization?: string | null
  rubric: RubricEnvelope
}

export interface ExtractionFailure {
  extractionId: string
  instructionVersionId: string
  protectedContractVersion: string
  validationStatus: 'invalid'
  validationFindings: ExtractionValidationFinding[]
}

export interface RubricCategory {
  id: string
  name: string
  description: string
  weight: number
}

export interface MustHave {
  id: string
  criterion: string
  description: string
}

export interface DesiredCriteria {
  id: string
  qualification: string
  description: string
}

export interface JobConfigVersion {
  versionId: string
  jobId: string
  rubric: RubricCategory[]
  rubricEnvelope?: RubricEnvelope
  mustHaves: MustHave[]
  desiredCriteria: DesiredCriteria[]
  runsPerApplication: number
  aggregationStrategy: AggregationStrategy
  longlistThreshold: number
  shortlistThreshold: number
  varianceThreshold: number
  rubricApprovalStatus: RubricApprovalStatus
  rubricSource: RubricSource
  rawExtractionResponse?: string
  extractionId?: string
  extractionInstructionVersionId?: string
  extraction?: JobSpecExtractionRecord
  legacyRubricState?: LegacyRubricState
  createdAt: string
}

export interface Job {
  jobId: string
  jobCode: string
  title: string
  department: string
  organization: string
  organizationId?: string
  departmentId?: string
  postingDate: string
  createdBy: string
  createdByName?: string
  createdAt: string
  status: JobStatus
  currentVersion: JobConfigVersion
  jobDescription?: string
  specDocumentId?: string
  rubricDocumentId?: string
  stats?: JobStats
}

export interface JobStats {
  totalApplications: number
  queued: number
  extracting: number
  scoring: number
  completed: number
  failed: number
  needsManualReview: number
  longlistCount: number
  shortlistCount: number
  excludedCount: number
}

export interface ApplicationDocument {
  documentId: string
  applicationId: string
  fileName: string
  mimeType: string
  sizeBytes: number
  sha256: string
  uploadedAt: string
  blobUri?: string
  contentSha256?: string
  contentUrl?: string
  rawContent?: string
}

export interface Application {
  applicationId: string
  jobId: string
  candidateRef: string
  candidateName?: string
  candidateEmail?: string
  status: ApplicationStatus
  createdAt: string
  documents: ApplicationDocument[]
  extractionArtifactId?: string
  finalScore?: number
  finalDecision?: Decision
  variance?: number
  flagged?: boolean
  testRunId?: string
  lastError?: string
}

export interface ExtractionArtifact {
  artifactId: string
  applicationId: string
  markdown: string
  extractionMetadata: {
    toolVersion: string
    confidence: number
    extractedAt: string
  }
  status: 'Success' | 'Failed'
  createdAt: string
}

export interface MustHaveResult {
  passed: boolean
  missingCriteria: string[]
  details: Record<string, unknown>
}

export interface EvidenceCitation {
  category: string
  snippet: string
  section: string
  confidence: number
}

export interface ScoringRun {
  runId: string
  applicationId: string
  versionId: string
  runIndex: number
  modelDeploymentId: string
  reasoningLevel?: string
  promptVersionId: string
  overallScore: number
  subScores: Record<string, number>
  mustHaveResult: MustHaveResult
  evidenceCitations: EvidenceCitation[]
  rationale: string
  improvementRecommendations: string[]
  createdAt: string
  durationMs: number
  tokenUsage?: {
    promptTokens: number
    completionTokens: number
    totalTokens: number
  }
  status: 'Success' | 'Failed'
  rawResponseText?: string
  rawParsedResponse?: Record<string, any>
  parserWarnings?: string[]
  parserConfidence?: number
}

export interface AggregatedResult {
  resultId: string
  applicationId: string
  versionId: string
  finalScore: number
  finalSubScores: Record<string, number>
  confidence: number
  variance: number
  finalDecision: Decision
  rationaleText: string
  recommendationsText: string
  allRuns: ScoringRun[]
  createdAt: string
}

export interface ProcessingEvent {
  eventId: string
  timestamp: string
  actor: string
  action: string
  entityType: string
  entityId: string
  correlationId: string
  details: Record<string, any>
}

export interface SystemStats {
  totalJobs: number
  activeJobs: number
  totalApplications: number
  queuedApplications: number
  processingApplications: number
  completedApplications: number
  failedApplications: number
  averageThroughputPerHour: number
}

export interface DLQItem {
  itemId: string
  applicationId: string
  jobId: string
  failureType: 'Extraction' | 'Scoring' | 'Aggregation'
  failureReason: string
  attemptCount: number
  firstFailedAt: string
  lastAttemptedAt: string
  canRetry: boolean
  notes?: string
  entityType?: string
  entityId?: string
  retryCount?: number
  createdAt?: string
}

export interface ManualReviewAuditEntry {
  entryId: string
  applicationId: string
  reviewerId: string
  reviewerName: string
  timestamp: string
  changeType: 'score_adjustment' | 'comment_added' | 'points_allocated' | 'decision_override'
  categoryId?: string
  categoryName?: string
  previousValue?: number | string
  newValue?: number | string
  comment?: string
}

export interface ManualReviewRubricEntry {
  score?: number
  points: number
  maxPoints: number
  comment: string
}

export interface ManualReviewData {
  applicationId: string
  jobId: string
  rubricScores: Record<string, ManualReviewRubricEntry>
  overallComment: string
  adjustedFinalScore?: number
  finalDecision?: Decision
  auditTrail: ManualReviewAuditEntry[]
  humanEdited: boolean
  lastModifiedAt: string
  lastModifiedBy: string
}

export type AuthenticationProvider = 'simple' | 'entra'
export type UserRole = 'admin' | 'organization_admin' | 'recruiter' | 'business_panel'
export type MembershipStatus = 'active' | 'revoked'
export type OrganizationStatus = 'active' | 'retired'
export type RoleAssignmentSource = 'group' | 'delegated' | 'bootstrap'
export type RoleAssignmentStatus = 'active' | 'revoked'

export interface Organization {
  organizationId: string
  name: string
  status: OrganizationStatus
  createdAt: string
  updatedAt: string
  updatedBy: string
}

export interface Department {
  departmentId: string
  organizationId: string
  name: string
  status: OrganizationStatus
  createdAt: string
  updatedAt: string
  updatedBy: string
}

export interface OrganizationMembership {
  membershipId: string
  userId: string
  organizationId: string
  defaultDepartmentMembershipId?: string
  defaultDepartmentId?: string
  status: MembershipStatus
  effectiveAt: string
  revokedAt?: string
  updatedBy: string
}

export interface DepartmentMembership {
  membershipId: string
  userId: string
  organizationId: string
  departmentId: string
  status: MembershipStatus
  effectiveAt: string
  revokedAt?: string
  updatedBy: string
}

export interface RoleGroupMapping {
  mappingId: string
  tenantId: string
  groupObjectId: string
  role: UserRole
  organizationId?: string
  departmentId?: string
  enabled: boolean
  createdAt: string
  updatedAt: string
  updatedBy: string
}

export interface RoleAssignment {
  assignmentId: string
  userId: string
  tenantId: string
  userObjectId: string
  role: UserRole
  organizationId?: string
  departmentId?: string
  roleGroupMappingId?: string
  source: RoleAssignmentSource
  status: RoleAssignmentStatus
  effectiveAt: string
  revokedAt?: string
  createdAt: string
  updatedAt: string
  updatedBy: string
}

export interface AuthorizationContext {
  userId: string
  tenantId: string
  objectId: string
  username: string
  fullName: string
  email?: string
  globalRole: 'admin' | null
  authorizationVersion: number
  memberships: Array<{
    organizationId: string
    organizationName: string
    defaultDepartmentId: string
    departments: Array<{ departmentId: string; departmentName: string }>
  }>
  authorizations: Array<{
    role: UserRole
    roleLabel: string
    organizationId: string | null
    departmentId: string | null
    assignmentSource: RoleAssignmentSource
  }>
  tokenIssuedAt: string
  refreshRequiredAt: string
}

export type AuthErrorCode =
  | 'auth_required' | 'invalid_token' | 'wrong_tenant' | 'invalid_audience'
  | 'unauthorized_client' | 'token_stale' | 'role_missing' | 'role_conflict'
  | 'assignment_missing' | 'assignment_revoked' | 'scope_unmapped'
  | 'membership_missing' | 'invalid_job_scope' | 'identity_disabled'
  | 'invalid_navigation_action' | 'navigation_audit_unavailable'

export interface EntraAccessRoleAssignment {
  id: string
  role: Exclude<UserRole, 'admin'>
  organizationId: string
  departmentId: string | null
  source: Exclude<RoleAssignmentSource, 'bootstrap'>
  status: RoleAssignmentStatus
}

export interface EntraOrganizationAccess {
  organizationId: string
  status: MembershipStatus
  departmentIds: string[]
  defaultDepartmentId: string | null
  roleAssignments: EntraAccessRoleAssignment[]
}

export interface EntraAccessUser {
  objectId: string
  username: string
  fullName: string
  email: string | null
  isActive: boolean
  authorizationVersion: number
  organizations: EntraOrganizationAccess[]
}

export interface EntraAccessUserPage {
  items: EntraAccessUser[]
  nextCursor: string | null
}

export interface EntraProfileInput {
  username: string
  fullName: string
  email: string | null
}

export interface DesiredDelegatedRole {
  role: Exclude<UserRole, 'admin'>
  departmentId: string | null
}

export interface PutOrganizationAccessRequest {
  expectedVersion: number
  profile: EntraProfileInput
  membership: {
    status: MembershipStatus
    departmentIds: string[]
    defaultDepartmentId: string | null
  }
  roleAssignments: DesiredDelegatedRole[]
}

export interface UpdateEntraAccessUserRequest {
  expectedVersion: number
  profile?: EntraProfileInput
  isActive?: boolean
}

export interface CanonicalApiError {
  error:
    | AuthErrorCode
    | 'invalid_scope'
    | 'forbidden'
    | 'not_found'
    | 'conflict'
    | 'version_conflict'
    | 'stale_version'
    | 'validation_error'
    | 'validation_failed'
    | 'already_converted'
    | 'internal_error'
  message: string
  correlationId: string
}

export interface OrganizationAdminDepartment {
  id: string
  organizationId: string
  name: string
  status: OrganizationStatus
}

export interface OrganizationAdminOrganization {
  id: string
  name: string
  status: OrganizationStatus
  departments: OrganizationAdminDepartment[]
}

export interface OrganizationAdminMembership {
  userObjectId: string
  organizationId: string
  departmentIds: string[]
  defaultDepartmentId: string
}

export interface OrganizationAdminRoleAssignment {
  id: string
  userObjectId: string
  role: Exclude<UserRole, 'admin'>
  organizationId: string
  departmentId: string | null
  source: 'delegated'
  status: RoleAssignmentStatus
}

export interface CreateOrganizationAdminRequest {
  name: string
  initialDepartmentName: string
}

export interface CreateOrganizationDepartmentRequest {
  name: string
}

export interface UpdateOrganizationDepartmentRequest {
  name?: string
  status?: OrganizationStatus
}

export interface RegisterOrganizationMembershipRequest {
  userObjectId: string
  departmentIds: string[]
  defaultDepartmentId: string
}

export interface GrantOrganizationRoleRequest {
  userObjectId: string
  role: Exclude<UserRole, 'admin'>
  departmentId: string | null
}

export type NavigationAction = 'collapse' | 'expand'

export interface NavigationShellState {
  desktopCollapsed: boolean
  compactOverlayOpen: boolean
  isCompact: boolean
}

export interface NavigationAuditRequest {
  action: NavigationAction
  correlationId: string
  requestedAt: string
}

export interface NavigationAuditOutcome {
  action: NavigationAction
  correlationId: string
  actorObjectId: string
  recordedAt: string
}

export interface User {
  userId: string
  username: string
  role: UserRole
  authenticationProvider?: AuthenticationProvider
  entraTenantId?: string
  entraObjectId?: string
  isActive?: boolean
  authorizationVersion?: number
  department?: string
  fullName: string
  email?: string
  createdAt: string
  lastLogin?: string
  passwordResetRequired?: boolean
}

export interface RecruiterAnalytics {
  recruiterId: string
  recruiterName: string
  department: string
  applicationsInQueue: number
  manualReviewsPerformed: number
  shortlistRecommendations: number
  averageProcessingTime?: number
  activeJobs: number
}

export interface DepartmentAnalytics {
  department: string
  totalRecruiters: number
  applicationsInQueue: number
  manualReviewsPerformed: number
  shortlistRecommendations: number
  activeJobs: number
  recruiters: RecruiterAnalytics[]
}

export interface PasswordResetRequest {
  requestId: string
  userId: string
  username: string
  fullName: string
  requestedAt: string
  status: 'pending' | 'completed' | 'rejected'
  resolvedAt?: string
  resolvedBy?: string
}

// US3a: Scoring Prompt Management
export type PromptStatus = 'draft' | 'active' | 'inactive' | 'production-approved'
export type PromptSource = 'manual' | 'imported' | 'generated'
export type TestRunStatus = 'pending_scoring' | 'scoring' | 'scoring_failed' | 'pending_review' | 'approved' | 'rejected'

export interface ScoringPrompt {
  promptId: string
  jobId: string
  versionNumber: number
  promptText: string
  status: PromptStatus
  createdAt: string
  lastModifiedAt: string
  author: string
  rating?: number
  comments?: string
  source: PromptSource
  generationMetadata?: Record<string, any>
  generationInstructionVersionId?: string
  modelId?: string
  reasoningLevel?: string
  approvedModelId?: string
  approvedReasoningLevel?: string
  approvedTestRunId?: string
}

export interface PromptTestRun {
  testRunId: string
  jobId: string
  promptId: string
  status: TestRunStatus
  applicationIds: string[]
  createdAt: string
  completedAt?: string
  reviewedBy?: string
  reviewNotes?: string
  modelId?: string
  reasoningLevel?: string
  approvedModelId?: string
  approvedReasoningLevel?: string
}

export interface PromptGenerationInstruction {
  id: string
  jobId?: string
  versionNumber: number
  instructionText: string
  modelId: string
  reasoningLevel: ReasoningEffort
  status: 'draft' | 'active' | 'inactive'
  changeNote?: string
  createdAt: string
  createdBy: string
  activatedAt?: string
  activatedBy?: string
}

export interface PromptProfileStatus {
  promptId: string
  modelId: string
  reasoningLevel: string
  currentModelId: string
  currentReasoningLevel: string
  isMatch: boolean
  hasExactProfileApprovedTest: boolean
  approvedTestRunId?: string
  mismatchMessage?: string
}

export interface TestRunApplicationDetail {
  application: Application
  scoringRuns: ScoringRun[]
  aggregatedResult?: AggregatedResult | null
  manualReview?: ManualReviewData | null
}

export interface PromptTestRunDetail extends PromptTestRun {
  applications: TestRunApplicationDetail[]
}

// Delegated Microsoft 365 bulk scoring (OpenAPI 3.1.0).
// These are public wire contracts only. Source credentials and internal fingerprints have no DTO.
export type BulkScoringSourceKind = 'sharepoint' | 'onedrive_business'
export type MicrosoftCommercialCloud = 'microsoftCommercial'
export type SourceConnectionState =
  | 'authorizing'
  | 'authorized'
  | 'verified'
  | 'consumed'
  | 'expired'
  | 'abandoned'
  | 'failed'
export type BulkScoringAuthorizationState =
  | 'pending'
  | 'active'
  | 'lapsed'
  | 'revoked'
  | 'accessLost'
  | 'discarded'
  | 'unavailableHistorical'
export type BulkScoringRequestStatus = 'draft' | 'active' | 'completed'
export type BulkScoringRequestOutcome = 'all_succeeded' | 'partial' | 'failed' | 'cancelled'
export type BulkScoringDiscoveryStatus = 'more_available' | 'complete' | 'limit_reached'
export type BulkScoringItemStatus =
  | 'waiting'
  | 'importing'
  | 'converting'
  | 'scoring'
  | 'completed'
  | 'failed'
  | 'skipped'
  | 'cancelled'
export type BulkScoringReasonCode =
  | 'unsupported_type'
  | 'file_too_large'
  | 'limit_exceeded'
  | 'empty_file'
  | 'not_convertible'
  | 'access_denied'
  | 'authorization_lapsed'
  | 'source_missing'
  | 'source_changed'
  | 'duplicate_in_request'
  | 'duplicate_in_job'
  | 'conversion_failed'
  | 'conversion_timeout'
  | 'converted_too_large'
  | 'conversion_page_mismatch'
  | 'conversion_dimension_mismatch'
  | 'conversion_ssim_below_threshold'
  | 'conversion_blank_page'
  | 'conversion_cropped_page'
  | 'scoring_failed'
  | 'cancelled_by_user'
  | 'transient_exhausted'
export type BulkScoringErrorCode =
  | 'source_authorization_required'
  | 'source_admin_consent_required'
  | 'source_authorization_expired'
  | 'source_authorization_revoked'
  | 'source_access_denied'
  | 'source_connection_invalid_or_expired'
  | 'source_connection_limit_reached'
  | 'source_connection_rate_limited'
  | 'source_callback_rate_limited'
  | 'source_unsupported_account'
  | 'source_unsupported_cloud'
  | 'source_folder_invalid_or_unavailable'
  | 'source_connector_unavailable'
  | 'draft_review_changed'
  | 'invalid_request_state'
  | 'concurrency_conflict'
  | 'validation_failed'
  | 'not_found'
  | 'forbidden'

export interface BulkScoringProblem {
  type: string
  title: string
  status: number
  detail?: string
  code: BulkScoringErrorCode
  correlationId: string
  adminConsentUrl?: string | null
}

export interface BeginSourceConnectionRequest {
  folderUrl: string
  retryOfRequestId?: string | null
}

export interface BeginSourceConnectionResponse {
  connectionId: string
  status: 'authorizing'
  authorizationUrl: string
  expiresAt: string
}

export interface SourceConnectionStatus {
  connectionId: string
  status: SourceConnectionState
  expiresAt: string
  sourceKind?: BulkScoringSourceKind
  sourceAccountDisplay?: string | null
  folderName?: string | null
  adminConsentRequired?: boolean
  adminConsentUrl?: string | null
  safeFailureCode?: BulkScoringErrorCode | null
}

export interface BulkScoringCapabilities {
  enabled: boolean
  supportedSources: BulkScoringSourceKind[]
  supportedCloud: MicrosoftCommercialCloud
  maxDiscoveryStepFiles: number
  rasterizationAvailable: boolean
}

export interface BulkScoringLimits {
  maxFilesPerRequest: number
  maxBytesPerFile: number
  maxTotalBytes: number
  discoveryStepMaxFiles: number
}

export interface BulkScoringCounts {
  discovered: number
  excluded: number
  inaccessible: number
  waiting: number
  importing: number
  converting: number
  scoring: number
  completed: number
  failed: number
  skipped: number
  cancelled: number
}

export interface BulkScoringTerminalCounts {
  completed: number
  failed: number
  skipped: number
  cancelled: number
}

export interface BulkScoringPreviewFile {
  itemId: string
  name: string
  relativePath: string
  sizeBytes: number
  excludedWhenRasterized?: boolean
}

export interface BulkScoringPreviewResponse {
  requestId: string
  connectionId: string
  sourceKind: BulkScoringSourceKind
  sourceAccountDisplay?: string
  folderName: string
  authorizationState: 'pending'
  discoveryStatus: BulkScoringDiscoveryStatus
  reviewSnapshotVersion: number
  counts: BulkScoringCounts
  excludedByReason?: Partial<Record<BulkScoringReasonCode, number>>
  entriesScanned?: number
  totalEligibleBytes: number
  limits: BulkScoringLimits
  draftItemsPath?: string
}

export interface BulkScoringDraftItemPage {
  items: BulkScoringPreviewFile[]
  totalItems: number
  reviewSnapshotVersion: number
  nextReviewCursor: string | null
}

export interface BulkScoringConfirmRequest {
  convertToRasterPdf: boolean
  allowDuplicates: boolean
  expectedConcurrencyVersion: number
}

export interface BulkScoringRequestSummary {
  requestId: string
  jobId: string
  status: BulkScoringRequestStatus
  outcome?: BulkScoringRequestOutcome | null
  sourceKind: BulkScoringSourceKind
  sourceAccountDisplay?: string
  folderName: string
  convertToRasterPdf: boolean
  counts: BulkScoringCounts
  authorizationState: BulkScoringAuthorizationState
  capturedScoringConfigVersionId: string | null
  currentScoringConfigVersionId: string | null
  configChangedSinceAcceptance: boolean
  retryOfRequestId?: string | null
  createdAt: string
  terminalAt?: string | null
}

/**
 * Detail-only actor projection. The API resolves a display name solely from the recorded product
 * actor ID and returns that recorded ID itself when the record or display name is unavailable.
 * There is intentionally no username field or username fallback contract.
 */
export type RecordedActorDisplay = string

export interface BulkScoringRequestDetail extends BulkScoringRequestSummary {
  submittedBy: RecordedActorDisplay
  submittedAt: string
  cancelledBy: RecordedActorDisplay | null
  cancelledAt: string | null
  sourceWebUrl?: string
  excludedByReason?: Partial<Record<BulkScoringReasonCode, number>>
  totalEligibleBytes?: number
  allowDuplicates?: boolean
  limits: BulkScoringLimits
  concurrencyVersion: number
}

export interface BulkScoringNotification {
  requestId: string
  outcome: BulkScoringRequestOutcome
  counts: BulkScoringTerminalCounts
  requestDetailPath: string
  terminalAt: string
}

export interface BulkScoringItem {
  itemId: string
  name: string
  relativePath: string
  sizeBytes: number
  status: BulkScoringItemStatus
  reasonCode?: BulkScoringReasonCode | null
  reasonMessage?: string | null
  scoredVersion?: 'original' | 'converted' | null
  pageCount: number | null
  scoringConfigVersionId: string | null
  applicationId?: string | null
  hasRetainedOriginal: boolean
}

export interface BulkScoringRequestPage {
  items: BulkScoringRequestSummary[]
  nextCursor: string | null
}

export interface BulkScoringItemPage {
  items: BulkScoringItem[]
  nextCursor: string | null
}

export interface BulkScoringSettings {
  maxIndividualFileBytes: number
  maxInFlightUploadBytes: number
  displayUnit: 'MiB'
  maxFilesPerRequest: number
  concurrencyVersion: number
}

export interface UpdateBulkScoringSettingsRequest {
  maxIndividualFileBytes: number
  maxInFlightUploadBytes: number
  maxFilesPerRequest: number
  expectedConcurrencyVersion: number
}
