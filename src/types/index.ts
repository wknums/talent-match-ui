export type JobStatus = 'Draft' | 'Active' | 'Processing' | 'Completed' | 'Archived'
export type ApplicationStatus = 'Queued' | 'Extracting' | 'ExtractionFailed' | 'Scoring' | 'ScoringFailed' | 'Aggregating' | 'Completed' | 'NeedsManualReview'
export type AggregationStrategy = 'median' | 'mean' | 'weighted'
export type Decision = 'Eligible' | 'Excluded' | 'NeedsManualReview'

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

export interface JobConfigVersion {
  versionId: string
  jobId: string
  rubric: RubricCategory[]
  mustHaves: MustHave[]
  runsPerApplication: number
  aggregationStrategy: AggregationStrategy
  longlistThreshold: number
  shortlistThreshold: number
  varianceThreshold: number
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
  createdAt: string
  status: JobStatus
  currentVersion: JobConfigVersion
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
  contentUrl?: string
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
  details: Record<string, boolean>
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
}

export interface ManualReviewAuditEntry {
  entryId: string
  applicationId: string
  reviewerId: string
  reviewerName: string
  timestamp: string
  changeType: 'score_adjustment' | 'comment_added' | 'points_allocated'
  categoryId?: string
  categoryName?: string
  previousValue?: number | string
  newValue?: number | string
  comment?: string
}

export interface ManualReviewData {
  applicationId: string
  jobId: string
  rubricScores: Record<string, { points: number; maxPoints: number; comment: string }>
  overallComment: string
  adjustedFinalScore?: number
  auditTrail: ManualReviewAuditEntry[]
  lastModifiedAt: string
  lastModifiedBy: string
}
