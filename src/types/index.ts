export type JobStatus = 'Draft' | 'Active' | 'Processing' | 'Completed' | 'Archived'
export type ApplicationStatus = 'Queued' | 'Extracting' | 'ExtractionFailed' | 'Scoring' | 'ScoringFailed' | 'Aggregating' | 'Completed' | 'NeedsManualReview'
export type AggregationStrategy = 'median' | 'mean' | 'weighted'
export type Decision = 'Eligible' | 'Excluded' | 'NeedsManualReview'
export type RubricApprovalStatus = 'draft' | 'approved'
export type RubricSource = 'manual' | 'extracted' | 'generated'

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
  error: AuthErrorCode | 'invalid_scope' | 'forbidden' | 'not_found' | 'conflict' | 'version_conflict'
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
