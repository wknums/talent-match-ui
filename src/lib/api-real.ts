import type {
  Job,
  JobConfigVersion,
  Application,
  ScoringRun,
  AggregatedResult,
  SystemStats,
  DLQItem,
  ExtractionArtifact,
  ProcessingEvent,
  ManualReviewData,
  User,
  PasswordResetRequest,
  ScoringPrompt,
  PromptTestRun,
  PromptTestRunDetail,
  AuthenticationProvider,
  AuthorizationContext,
  CanonicalApiError,
  EntraAccessUser,
  EntraAccessUserPage,
  PutOrganizationAccessRequest,
  UpdateEntraAccessUserRequest,
  CreateOrganizationAdminRequest,
  CreateOrganizationDepartmentRequest,
  GrantOrganizationRoleRequest,
  OrganizationAdminDepartment,
  OrganizationAdminMembership,
  OrganizationAdminOrganization,
  OrganizationAdminRoleAssignment,
  RegisterOrganizationMembershipRequest,
  UpdateOrganizationDepartmentRequest,
} from '@/types'

function normalizeMustHaveResult(raw: any) {
  return {
    passed: raw?.passed ?? true,
    missingCriteria: Array.isArray(raw?.missingCriteria) ? raw.missingCriteria : [],
    details: raw?.details && typeof raw.details === 'object' ? raw.details : {},
  }
}

function mapTestRun(raw: any): PromptTestRun {
  return {
    testRunId: raw.id ?? raw.testRunId,
    jobId: raw.jobId,
    promptId: raw.promptId,
    status: raw.status,
    applicationIds: typeof raw.applicationIdsJson === 'string'
      ? JSON.parse(raw.applicationIdsJson)
      : (raw.applicationIds ?? []),
    createdAt: raw.createdAt,
    completedAt: raw.completedAt,
    reviewedBy: raw.reviewedBy,
    reviewNotes: raw.reviewNotes,
  }
}

function mapScoringRun(raw: any): ScoringRun {
  return {
    runId: raw.id ?? raw.runId,
    applicationId: raw.applicationId,
    versionId: raw.versionId ?? raw.aiModelId ?? '',
    runIndex: raw.runIndex ?? 0,
    modelDeploymentId: raw.modelDeploymentId ?? raw.aiModelId ?? '',
    promptVersionId: raw.promptVersionId ?? raw.promptVersion ?? '',
    overallScore: raw.overallScore ?? raw.totalScore ?? 0,
    subScores: typeof raw.categoryScoresJson === 'string'
      ? JSON.parse(raw.categoryScoresJson)
      : (raw.subScores ?? {}),
    mustHaveResult: normalizeMustHaveResult(
      typeof raw.mustHaveEvaluationJson === 'string'
        ? JSON.parse(raw.mustHaveEvaluationJson)
        : raw.mustHaveResult,
    ),
    evidenceCitations: typeof raw.evidenceCitationsJson === 'string'
      ? JSON.parse(raw.evidenceCitationsJson)
      : (raw.evidenceCitations ?? []),
    rationale: raw.rationale ?? '',
    improvementRecommendations: typeof raw.improvementTipsJson === 'string'
      ? JSON.parse(raw.improvementTipsJson)
      : (raw.improvementRecommendations ?? []),
    createdAt: raw.createdAt,
    durationMs: raw.durationMs ?? 0,
    tokenUsage: raw.inputTokens || raw.outputTokens
      ? { promptTokens: raw.inputTokens ?? 0, completionTokens: raw.outputTokens ?? 0, totalTokens: (raw.inputTokens ?? 0) + (raw.outputTokens ?? 0) }
      : undefined,
    status: raw.status ?? 'Success',
    rawResponseText: raw.rawResponseText,
    rawParsedResponse: raw.rawParsedResponse,
    parserWarnings: raw.parserWarnings ?? [],
    parserConfidence: raw.parserConfidence,
  }
}

function mapAggregatedResult(raw: any): AggregatedResult {
  const recommendations = typeof raw.mergedImprovementTipsJson === 'string'
    ? JSON.parse(raw.mergedImprovementTipsJson)
    : (typeof raw.recommendationsText === 'string'
        ? raw.recommendationsText
            .split(';')
            .map((item: string) => item.trim())
            .filter(Boolean)
        : [])

  return {
    resultId: raw.id ?? raw.resultId,
    applicationId: raw.applicationId,
    versionId: raw.versionId ?? '',
    finalScore: raw.finalScore ?? 0,
    finalSubScores: raw.finalSubScores ?? {},
    confidence: raw.confidence ?? 0,
    variance: raw.variance ?? 0,
    finalDecision: raw.decision ?? raw.finalDecision,
    rationaleText: raw.consolidatedRationale ?? raw.rationaleText ?? '',
    recommendationsText: Array.isArray(recommendations) ? recommendations.join('; ') : '',
    allRuns: [],
    createdAt: raw.createdAt ?? new Date().toISOString(),
  }
}

function mapManualReview(raw: any): ManualReviewData {
  return {
    applicationId: raw.applicationId,
    jobId: raw.jobId,
    rubricScores: typeof raw.rubricScoresJson === 'string'
      ? JSON.parse(raw.rubricScoresJson)
      : (raw.rubricScores ?? {}),
    overallComment: raw.overallComment ?? '',
    adjustedFinalScore: raw.adjustedFinalScore,
    finalDecision: raw.finalDecision,
    auditTrail: typeof raw.auditTrailJson === 'string'
      ? JSON.parse(raw.auditTrailJson)
      : (raw.auditTrail ?? []),
    humanEdited: raw.humanEdited === true || raw.HumanEdited === true || Number(raw.HumanEdited ?? 0) === 1,
    lastModifiedAt: raw.lastModifiedAt ?? new Date().toISOString(),
    lastModifiedBy: raw.lastModifiedBy ?? 'unknown',
  }
}

function mapDLQItem(raw: any): DLQItem {
  return {
    itemId: raw.id ?? raw.itemId,
    applicationId: raw.applicationId ?? (raw.entityType === 'Application' ? raw.entityId : ''),
    jobId: raw.jobId ?? '',
    failureType: raw.failureType ?? 'Scoring',
    failureReason: raw.failureReason ?? '',
    attemptCount: raw.attemptCount ?? raw.retryCount ?? 0,
    firstFailedAt: raw.firstFailedAt ?? raw.createdAt ?? new Date().toISOString(),
    lastAttemptedAt: raw.lastAttemptedAt ?? raw.updatedAt ?? raw.createdAt ?? new Date().toISOString(),
    canRetry: raw.canRetry ?? true,
    notes: raw.notes,
    entityType: raw.entityType ?? 'Application',
    entityId: raw.entityId ?? raw.applicationId ?? '',
    retryCount: raw.retryCount ?? raw.attemptCount ?? 0,
    createdAt: raw.createdAt ?? raw.firstFailedAt ?? new Date().toISOString(),
  }
}

function mapApplication(raw: any): Application {
  return {
    applicationId: raw.id ?? raw.applicationId,
    jobId: raw.jobId,
    candidateRef: raw.candidateRef ?? '',
    candidateName: raw.candidateName,
    candidateEmail: raw.candidateEmail,
    status: raw.status,
    createdAt: raw.createdAt,
    documents: raw.documents ?? [],
    extractionArtifactId: raw.extractionArtifactId,
    finalScore: raw.finalScore,
    finalDecision: raw.finalDecision,
    variance: raw.variance,
    flagged: raw.flagged,
    testRunId: raw.testRunId,
    lastError: raw.lastError,
  }
}

const API_BASE = '/api'

type AccessTokenRequest = { forceRefresh: boolean }
type AccessTokenProvider = (request: AccessTokenRequest) => Promise<string>

interface AuthenticatedTransportConfig {
  authMode: AuthenticationProvider
  acquireAccessToken?: AccessTokenProvider
  apiOrigin?: string
}

let authenticatedTransport: AuthenticatedTransportConfig = { authMode: 'simple' }

export class TalentMatchApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly errorCode?: CanonicalApiError['error'],
    public readonly correlationId?: string,
  ) {
    super(message)
    this.name = 'TalentMatchApiError'
  }
}

export function configureAuthenticatedTransport(config: AuthenticatedTransportConfig): void {
  authenticatedTransport = { ...config }
}

export function resetAuthenticatedTransport(): void {
  authenticatedTransport = { authMode: 'simple' }
}

function isTalentMatchApiUrl(url: string): boolean {
  if (url === API_BASE || url.startsWith(`${API_BASE}/`)) {
    return true
  }

  if (!authenticatedTransport.apiOrigin) {
    return false
  }

  try {
    const configuredOrigin = new URL(authenticatedTransport.apiOrigin).origin
    const target = new URL(url)
    return target.origin === configuredOrigin
      && (target.pathname === API_BASE || target.pathname.startsWith(`${API_BASE}/`))
  } catch {
    return false
  }
}

function isIdempotent(method: string): boolean {
  return method === 'GET' || method === 'HEAD' || method === 'OPTIONS'
}

async function readApiError(response: Response): Promise<TalentMatchApiError> {
  const body = await response.json().catch(() => ({ message: response.statusText })) as {
    error?: CanonicalApiError['error']
    message?: string
    correlationId?: string
  }

  return new TalentMatchApiError(
    body.message || `Request failed: ${response.status}`,
    response.status,
    body.error,
    body.correlationId,
  )
}

export async function fetchWithAuthentication(
  url: string,
  options: RequestInit = {},
  hasRetried = false,
): Promise<Response> {
  const method = (options.method ?? 'GET').toUpperCase()
  const headers = new Headers(options.headers)

  if (
    authenticatedTransport.authMode === 'entra'
    && authenticatedTransport.acquireAccessToken
    && isTalentMatchApiUrl(url)
  ) {
    const accessToken = await authenticatedTransport.acquireAccessToken({
      forceRefresh: hasRetried,
    })
    headers.set('Authorization', `Bearer ${accessToken}`)
  }

  const response = await fetch(url, { ...options, headers })
  if (
    response.status === 401
    && !hasRetried
    && isIdempotent(method)
    && authenticatedTransport.authMode === 'entra'
    && authenticatedTransport.acquireAccessToken
    && isTalentMatchApiUrl(url)
  ) {
    const body = await response.clone().json().catch(() => null) as { error?: string } | null
    if (body?.error === 'token_stale') {
      return fetchWithAuthentication(url, options, true)
    }
  }

  return response
}

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const res = await fetchWithAuthentication(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  })
  if (!res.ok) {
    throw await readApiError(res)
  }
  return res.json()
}

async function fetchVoid(url: string, options?: RequestInit): Promise<void> {
  const res = await fetchWithAuthentication(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  })

  if (!res.ok) {
    throw await readApiError(res)
  }
}

export const realAPI = {
  // Auth
  async login(username: string, password: string): Promise<User> {
    return fetchJSON(`${API_BASE}/auth/login`, {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    })
  },

  async logout(): Promise<void> {
    await fetchVoid(`${API_BASE}/auth/logout`, { method: 'POST' })
  },

  async getCurrentUser(): Promise<User | null> {
    try {
      return await fetchJSON(`${API_BASE}/auth/me`)
    } catch {
      return null
    }
  },

  async getAuthorizationContext(): Promise<AuthorizationContext> {
    return fetchJSON(`${API_BASE}/auth/me`)
  },

  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    await fetchJSON(`${API_BASE}/auth/change-password`, {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    })
  },

  // User management
  async getAllUsers(): Promise<User[]> {
    return fetchJSON(`${API_BASE}/users`)
  },

  async createUser(username: string, role: string, department: string, password: string, fullName?: string, email?: string): Promise<User> {
    return fetchJSON(`${API_BASE}/users`, {
      method: 'POST',
      body: JSON.stringify({ username, role, department, password, fullName, email }),
    })
  },

  async updateUser(userId: string, fullName: string, email: string, role: string, department: string): Promise<User> {
    return fetchJSON(`${API_BASE}/users/${userId}`, {
      method: 'PUT',
      body: JSON.stringify({ fullName, email, role, department }),
    })
  },

  async deleteUser(userId: string): Promise<void> {
    await fetchJSON(`${API_BASE}/users/${userId}`, { method: 'DELETE' })
  },

  async resetUserPassword(userId: string, newPassword: string): Promise<void> {
    await fetchJSON(`${API_BASE}/users/${userId}/reset-password`, {
      method: 'POST',
      body: JSON.stringify({ newPassword }),
    })
  },

  async getPasswordResetRequests(): Promise<PasswordResetRequest[]> {
    return fetchJSON(`${API_BASE}/users/reset-requests`)
  },

  async requestPasswordReset(reason?: string): Promise<void> {
    await fetchJSON(`${API_BASE}/users/reset-requests`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    })
  },

  async requestPasswordResetFromLogin(username: string, reason?: string): Promise<void> {
    await fetchJSON(`${API_BASE}/auth/request-password-reset`, {
      method: 'POST',
      body: JSON.stringify({ username, reason }),
    })
  },

  async resolvePasswordResetRequest(requestId: string, action: 'approve' | 'reject', newPassword?: string): Promise<void> {
    await fetchJSON(`${API_BASE}/users/reset-requests/${requestId}`, {
      method: 'PUT',
      body: JSON.stringify({ action, newPassword }),
    })
  },

  // Entra access management
  async listEntraAccessUsers(options: {
    search?: string
    organizationId?: string
    status?: 'pending' | 'active' | 'disabled'
    cursor?: string
    limit?: number
  } = {}): Promise<EntraAccessUserPage> {
    const query = new URLSearchParams()
    if (options.search) query.set('search', options.search)
    if (options.organizationId) query.set('organizationId', options.organizationId)
    if (options.status) query.set('status', options.status)
    if (options.cursor) query.set('cursor', options.cursor)
    if (options.limit !== undefined) query.set('limit', String(options.limit))
    const suffix = query.size > 0 ? `?${query}` : ''
    return fetchJSON(`${API_BASE}/access-management/users${suffix}`)
  },

  async getEntraAccessUser(objectId: string): Promise<EntraAccessUser> {
    return fetchJSON(`${API_BASE}/access-management/users/${encodeURIComponent(objectId)}`)
  },

  async updateEntraAccessUser(
    objectId: string,
    request: UpdateEntraAccessUserRequest,
  ): Promise<EntraAccessUser> {
    return fetchJSON(`${API_BASE}/access-management/users/${encodeURIComponent(objectId)}`, {
      method: 'PATCH',
      body: JSON.stringify(request),
    })
  },

  async putEntraOrganizationAccess(
    objectId: string,
    organizationId: string,
    request: PutOrganizationAccessRequest,
  ): Promise<EntraAccessUser> {
    return fetchJSON(
      `${API_BASE}/access-management/users/${encodeURIComponent(objectId)}/organizations/${encodeURIComponent(organizationId)}`,
      { method: 'PUT', body: JSON.stringify(request) },
    )
  },

  async revokeEntraRoleAssignment(
    objectId: string,
    organizationId: string,
    assignmentId: string,
    expectedVersion: number,
  ): Promise<EntraAccessUser> {
    return fetchJSON(
      `${API_BASE}/access-management/users/${encodeURIComponent(objectId)}/organizations/${encodeURIComponent(organizationId)}/role-assignments/${encodeURIComponent(assignmentId)}?expectedVersion=${expectedVersion}`,
      { method: 'DELETE' },
    )
  },

  // Organization administration
  async createOrganization(request: CreateOrganizationAdminRequest): Promise<OrganizationAdminOrganization> {
    return fetchJSON(`${API_BASE}/organizations`, {
      method: 'POST',
      body: JSON.stringify(request),
    })
  },

  async createOrganizationDepartment(
    organizationId: string,
    request: CreateOrganizationDepartmentRequest,
  ): Promise<OrganizationAdminDepartment> {
    return fetchJSON(`${API_BASE}/organizations/${encodeURIComponent(organizationId)}/departments`, {
      method: 'POST',
      body: JSON.stringify(request),
    })
  },

  async updateOrganizationDepartment(
    organizationId: string,
    departmentId: string,
    request: UpdateOrganizationDepartmentRequest,
  ): Promise<OrganizationAdminDepartment> {
    return fetchJSON(
      `${API_BASE}/organizations/${encodeURIComponent(organizationId)}/departments/${encodeURIComponent(departmentId)}`,
      { method: 'PATCH', body: JSON.stringify(request) },
    )
  },

  async registerOrganizationMembership(
    organizationId: string,
    request: RegisterOrganizationMembershipRequest,
  ): Promise<OrganizationAdminMembership> {
    return fetchJSON(`${API_BASE}/organizations/${encodeURIComponent(organizationId)}/memberships`, {
      method: 'POST',
      body: JSON.stringify(request),
    })
  },

  async grantOrganizationRole(
    organizationId: string,
    request: GrantOrganizationRoleRequest,
  ): Promise<OrganizationAdminRoleAssignment> {
    return fetchJSON(`${API_BASE}/organizations/${encodeURIComponent(organizationId)}/role-assignments`, {
      method: 'POST',
      body: JSON.stringify(request),
    })
  },

  async revokeOrganizationRole(organizationId: string, assignmentId: string): Promise<void> {
    await fetchVoid(
      `${API_BASE}/organizations/${encodeURIComponent(organizationId)}/role-assignments/${encodeURIComponent(assignmentId)}`,
      { method: 'DELETE' },
    )
  },

  // Jobs
  async getJobs(): Promise<Job[]> {
    return fetchJSON(`${API_BASE}/jobs`)
  },

  async getJob(jobId: string): Promise<Job | null> {
    try {
      return await fetchJSON(`${API_BASE}/jobs/${jobId}`)
    } catch {
      return null
    }
  },

  async deleteJob(jobId: string): Promise<void> {
    await fetchVoid(`${API_BASE}/jobs/${jobId}`, { method: 'DELETE' })
  },

  async extractJobSpec(fileName: string, content: string, mimeType: string): Promise<any> {
    return fetchJSON(`${API_BASE}/jobs/extract-spec`, {
      method: 'POST',
      body: JSON.stringify({ fileName, content, mimeType }),
    })
  },

  async extractRubric(fileName: string, content: string, mimeType: string): Promise<any> {
    return fetchJSON(`${API_BASE}/jobs/extract-rubric`, {
      method: 'POST',
      body: JSON.stringify({ fileName, content, mimeType }),
    })
  },

  async createJob(data: {
    title: string
    department: string
    organization: string
    postingDate: string
    rubric: import('@/types').RubricCategory[]
    mustHaves: import('@/types').MustHave[]
    desiredCriteria?: import('@/types').DesiredCriteria[]
    jobDescription?: string
    runsPerApplication: number
    aggregationStrategy: import('@/types').AggregationStrategy
    longlistThreshold: number
    shortlistThreshold: number
    specDocumentId?: string
    rubricDocumentId?: string
    jobCode?: string
    rubricSource?: import('@/types').RubricSource
    rawExtractionResponse?: string
  }): Promise<Job> {
    return fetchJSON(`${API_BASE}/jobs`, {
      method: 'POST',
      body: JSON.stringify(data),
    })
  },

  async updateJob(jobId: string, data: {
    title: string
    department: string
    organization: string
    postingDate: string
    rubric: import('@/types').RubricCategory[]
    mustHaves: import('@/types').MustHave[]
    desiredCriteria?: import('@/types').DesiredCriteria[]
    jobDescription?: string
    runsPerApplication: number
    aggregationStrategy: import('@/types').AggregationStrategy
    longlistThreshold: number
    shortlistThreshold: number
    specDocumentId?: string
    rubricDocumentId?: string
    jobCode?: string
    rubricSource?: import('@/types').RubricSource
    rawExtractionResponse?: string
    rubricApprovalStatus?: import('@/types').RubricApprovalStatus
  }): Promise<Job> {
    // Update job config creates a new version
    await fetchJSON(`${API_BASE}/jobs/${jobId}/config`, {
      method: 'PUT',
      body: JSON.stringify(data),
    })
    // Return updated job
    const job = await this.getJob(jobId)
    return job!
  },

  async updateJobConfig(jobId: string, configData: Partial<JobConfigVersion>): Promise<JobConfigVersion> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/config`, {
      method: 'PUT',
      body: JSON.stringify(configData),
    })
  },

  async updateJobRubric(_jobId: string, _rubricDocumentId: string): Promise<void> {
    // No-op for now; rubric is updated via config
  },

  async updateRubricApproval(jobId: string, status: 'approved' | 'draft'): Promise<{ versionId: string; rubricApprovalStatus: string; updatedAt: string }> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/rubric-approval`, {
      method: 'PUT',
      body: JSON.stringify({ status }),
    })
  },

  // Applications
  async getApplications(jobId: string, filters?: {
    status?: string
    minScore?: number
    maxScore?: number
    decision?: string
    applicantName?: string
    list?: string
    sortField?: string
    sortOrder?: string
    varianceMin?: number
    page?: number
    pageSize?: number
  }): Promise<Application[]> {
    const params = new URLSearchParams()
    if (filters?.status) params.set('status', filters.status)
    if (filters?.applicantName) params.set('applicantName', filters.applicantName)
    if (filters?.list) params.set('list', filters.list)
    if (filters?.sortField) params.set('sortField', filters.sortField)
    if (filters?.sortOrder) params.set('sortOrder', filters.sortOrder)
    if (filters?.varianceMin != null) params.set('varianceMin', String(filters.varianceMin))
    if (filters?.page) params.set('page', String(filters.page))
    if (filters?.pageSize) params.set('pageSize', String(filters.pageSize))
    const qs = params.toString()
    const result = await fetchJSON<{ applications: Application[]; total: number }>(`${API_BASE}/jobs/${jobId}/applications${qs ? `?${qs}` : ''}`)
    return result.applications
  },

  async getApplication(applicationId: string): Promise<Application | null> {
    try {
      const raw = await fetchJSON<any>(`${API_BASE}/applications/${applicationId}`)
      return mapApplication(raw)
    } catch {
      return null
    }
  },

  async uploadApplications(jobId: string, files: File[]): Promise<{ applicationIds: string[] }> {
    // Convert files to base64 JSON payload for binary-safe transport
    const fileData = await Promise.all(files.map(async (file) => {
      const arrayBuffer = await file.arrayBuffer()
      const bytes = new Uint8Array(arrayBuffer)
      let binary = ''
      for (let i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i])
      }
      return {
        fileName: file.name,
        content: btoa(binary),
        mimeType: file.type || 'application/octet-stream',
        sizeBytes: file.size,
      }
    }))

    return fetchJSON(`${API_BASE}/jobs/${jobId}/applications/upload`, {
      method: 'POST',
      body: JSON.stringify({ files: fileData }),
    })
  },

  // Scoring
  async getScoringRuns(applicationId: string): Promise<ScoringRun[]> {
    const raw = await fetchJSON<any[]>(`${API_BASE}/applications/${applicationId}/runs`)
    return raw.map(mapScoringRun)
  },

  async getAggregatedResult(applicationId: string): Promise<AggregatedResult | null> {
    try {
      const raw = await fetchJSON<any>(`${API_BASE}/applications/${applicationId}/result`)
      return mapAggregatedResult(raw)
    } catch {
      return null
    }
  },

  async getExtractionArtifact(applicationId: string): Promise<ExtractionArtifact | null> {
    try {
      return await fetchJSON(`${API_BASE}/applications/${applicationId}/extraction`)
    } catch {
      return null
    }
  },

  getDocumentContentUrl(applicationId: string, documentId: string): string {
    return `${API_BASE}/applications/${applicationId}/documents/${documentId}/content`
  },

  async getDocumentContent(applicationId: string, documentId: string): Promise<ArrayBuffer> {
    const res = await fetch(`${API_BASE}/applications/${applicationId}/documents/${documentId}/content`)
    if (!res.ok) throw new Error(`Failed to fetch document content: ${res.status}`)
    return res.arrayBuffer()
  },

  // Manual Review
  async getManualReview(applicationId: string): Promise<ManualReviewData | null> {
    try {
      const raw = await fetchJSON<any>(`${API_BASE}/applications/${applicationId}/manual-review`)
      return raw ? mapManualReview(raw) : null
    } catch {
      return null
    }
  },

  async saveManualReview(applicationId: string, reviewData: Partial<ManualReviewData>): Promise<ManualReviewData> {
    const raw = await fetchJSON<any>(`${API_BASE}/applications/${applicationId}/manual-review`, {
      method: 'POST',
      body: JSON.stringify(reviewData),
    })
    return mapManualReview(raw)
  },

  // DLQ
  async getDLQItems(): Promise<DLQItem[]> {
    const raw = await fetchJSON<any[]>(`${API_BASE}/dlq`)
    return raw.map(mapDLQItem)
  },

  async retryDLQItem(itemId: string): Promise<void> {
    await fetchJSON(`${API_BASE}/dlq/${itemId}/retry`, { method: 'POST' })
  },

  async bulkRetryDLQItems(itemIds: string[]): Promise<{ succeeded: number; total: number }> {
    return fetchJSON(`${API_BASE}/dlq/bulk-retry`, {
      method: 'POST',
      body: JSON.stringify({ ids: itemIds }),
    })
  },

  async bulkDeleteDLQItems(itemIds: string[]): Promise<{ deleted: number; total: number }> {
    return fetchJSON(`${API_BASE}/dlq/bulk-delete`, {
      method: 'POST',
      body: JSON.stringify({ ids: itemIds }),
    })
  },

  // Stats
  async getSystemStats(): Promise<SystemStats> {
    return fetchJSON(`${API_BASE}/stats`)
  },

  // Audit
  async getAuditEvents(filters?: {
    entityType?: string
    entityId?: string
    startDate?: string
    endDate?: string
    page?: number
    pageSize?: number
  }): Promise<ProcessingEvent[]> {
    const params = new URLSearchParams()
    if (filters?.entityType) params.set('entityType', filters.entityType)
    if (filters?.entityId) params.set('entityId', filters.entityId)
    if (filters?.startDate) params.set('startDate', filters.startDate)
    if (filters?.endDate) params.set('endDate', filters.endDate)
    if (filters?.page) params.set('page', String(filters.page))
    if (filters?.pageSize) params.set('pageSize', String(filters.pageSize))
    const qs = params.toString()
    const result = await fetchJSON<{ events: ProcessingEvent[]; total: number }>(`${API_BASE}/audit${qs ? `?${qs}` : ''}`)
    return result.events
  },

  // Analytics
  async getRecruiterAnalytics(): Promise<import('@/types').RecruiterAnalytics[]> {
    return fetchJSON(`${API_BASE}/stats/recruiters`)
  },

  async getDepartmentAnalytics(): Promise<import('@/types').DepartmentAnalytics[]> {
    return fetchJSON(`${API_BASE}/stats/departments`)
  },

  // Pipeline
  async processJob(jobId: string): Promise<void> {
    await fetchJSON(`${API_BASE}/jobs/${jobId}/process`, { method: 'POST' })
  },

  async retryFailedApplications(jobId: string): Promise<{ retriedCount: number }> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/retry-failed`, { method: 'POST' })
  },

  async reaggregateJob(jobId: string): Promise<{ updated: number; total: number }> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/reaggregate`, { method: 'POST' })
  },

  async rescoreApplication(jobId: string, applicationId: string): Promise<void> {
    await fetchJSON(`${API_BASE}/jobs/${jobId}/applications/${applicationId}/rescore`, { method: 'POST' })
  },

  // Scoring Prompts (US3a)
  async getPrompts(jobId: string): Promise<ScoringPrompt[]> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts`)
  },

  async createPrompt(jobId: string, data: { promptText: string; source: string; generationMetadata?: Record<string, any> }): Promise<ScoringPrompt> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts`, {
      method: 'POST',
      body: JSON.stringify(data),
    })
  },

  async getPrompt(jobId: string, promptId: string): Promise<ScoringPrompt> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}`)
  },

  async editPrompt(jobId: string, promptId: string, data: { promptText: string }): Promise<ScoringPrompt> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    })
  },

  async activatePrompt(jobId: string, promptId: string): Promise<ScoringPrompt> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/activate`, { method: 'POST' })
  },

  async ratePrompt(jobId: string, promptId: string, data: { rating: number; comments?: string }): Promise<ScoringPrompt> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/rate`, {
      method: 'POST',
      body: JSON.stringify(data),
    })
  },

  async generatePrompt(jobId: string): Promise<{ promptText: string; generationMetadata: Record<string, any> }> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/generate`, { method: 'POST' })
  },

  async approvePromptForProduction(jobId: string, promptId: string): Promise<ScoringPrompt> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/approve-production`, { method: 'POST' })
  },

  async createTestRun(jobId: string, promptId: string, files: Array<{ fileName: string; content: string; mimeType: string; sizeBytes: number }>): Promise<PromptTestRun> {
    const raw = await fetchJSON<any>(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs`, {
      method: 'POST',
      body: JSON.stringify({ files }),
    })
    return mapTestRun(raw.testRun ?? raw)
  },

  async getTestRuns(jobId: string, promptId: string): Promise<PromptTestRun[]> {
    const raw = await fetchJSON<any[]>(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs`)
    return raw.map(mapTestRun)
  },

  async getTestRun(jobId: string, promptId: string, testRunId: string): Promise<PromptTestRunDetail> {
    const raw = await fetchJSON<any>(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs/${testRunId}`)
    const testRun = mapTestRun(raw.testRun ?? raw)
    const applications = (raw.applications ?? []).map((a: any) => ({
      application: mapApplication(a.application ?? a),
      scoringRuns: (a.scoringRuns ?? []).map(mapScoringRun),
      aggregatedResult: a.aggregatedResult ? mapAggregatedResult(a.aggregatedResult) : null,
      manualReview: a.manualReview ? mapManualReview(a.manualReview) : null,
    }))
    return { ...testRun, applications }
  },

  async approveTestRun(jobId: string, promptId: string, testRunId: string): Promise<PromptTestRun> {
    const raw = await fetchJSON<any>(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs/${testRunId}/approve`, {
      method: 'POST',
    })
    return mapTestRun(raw)
  },

  async rescoreTestRun(jobId: string, promptId: string, testRunId: string): Promise<void> {
    await fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs/${testRunId}/rescore`, {
      method: 'POST',
    })
  },
}
