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

export type UserRole = 'admin' | 'recruiter' | 'business_panel'

export interface User {
  userId: string
  username: string
  role: UserRole
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
