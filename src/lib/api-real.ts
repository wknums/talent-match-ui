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
} from '@/types'

const API_BASE = '/api'

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  })
  if (!res.ok) {
    const err = await res.json().catch(() => ({ message: res.statusText }))
    throw new Error(err.message || `Request failed: ${res.status}`)
  }
  return res.json()
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
    await fetchJSON(`${API_BASE}/auth/logout`, { method: 'POST' })
  },

  async getCurrentUser(): Promise<User | null> {
    try {
      return await fetchJSON(`${API_BASE}/auth/me`)
    } catch {
      return null
    }
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

  async resolvePasswordResetRequest(requestId: string, action: 'approve' | 'reject', newPassword?: string): Promise<void> {
    await fetchJSON(`${API_BASE}/users/reset-requests/${requestId}`, {
      method: 'PUT',
      body: JSON.stringify({ action, newPassword }),
    })
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

  async updateJobRubric(jobId: string, rubricDocumentId: string): Promise<void> {
    // No-op for now; rubric is updated via config
  },

  // Applications
  async getApplications(jobId: string, filters?: {
    status?: string
    minScore?: number
    maxScore?: number
    decision?: string
    list?: string
    sortField?: string
    sortOrder?: string
    varianceMin?: number
    page?: number
    pageSize?: number
  }): Promise<Application[]> {
    const params = new URLSearchParams()
    if (filters?.status) params.set('status', filters.status)
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
      return await fetchJSON(`${API_BASE}/applications/${applicationId}`)
    } catch {
      return null
    }
  },

  async uploadApplications(jobId: string, files: File[]): Promise<{ applicationIds: string[] }> {
    // Convert files to JSON payload since we're using JSON API
    const fileData = await Promise.all(files.map(async (file) => ({
      fileName: file.name,
      content: await file.text(),
      mimeType: file.type || 'application/octet-stream',
      sizeBytes: file.size,
    })))

    return fetchJSON(`${API_BASE}/jobs/${jobId}/applications/upload`, {
      method: 'POST',
      body: JSON.stringify({ files: fileData }),
    })
  },

  // Scoring
  async getScoringRuns(applicationId: string): Promise<ScoringRun[]> {
    return fetchJSON(`${API_BASE}/applications/${applicationId}/runs`)
  },

  async getAggregatedResult(applicationId: string): Promise<AggregatedResult | null> {
    try {
      return await fetchJSON(`${API_BASE}/applications/${applicationId}/result`)
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

  // Manual Review
  async getManualReview(applicationId: string): Promise<ManualReviewData | null> {
    try {
      return await fetchJSON(`${API_BASE}/applications/${applicationId}/manual-review`)
    } catch {
      return null
    }
  },

  async saveManualReview(applicationId: string, reviewData: Partial<ManualReviewData>): Promise<ManualReviewData> {
    return fetchJSON(`${API_BASE}/applications/${applicationId}/manual-review`, {
      method: 'POST',
      body: JSON.stringify(reviewData),
    })
  },

  // DLQ
  async getDLQItems(): Promise<DLQItem[]> {
    return fetchJSON(`${API_BASE}/dlq`)
  },

  async retryDLQItem(itemId: string): Promise<void> {
    await fetchJSON(`${API_BASE}/dlq/${itemId}/retry`, { method: 'POST' })
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
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs`, {
      method: 'POST',
      body: JSON.stringify({ files }),
    })
  },

  async getTestRuns(jobId: string, promptId: string): Promise<PromptTestRun[]> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs`)
  },

  async getTestRun(jobId: string, promptId: string, testRunId: string): Promise<PromptTestRun & { applications?: Application[] }> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs/${testRunId}`)
  },

  async approveTestRun(jobId: string, promptId: string, testRunId: string): Promise<PromptTestRun> {
    return fetchJSON(`${API_BASE}/jobs/${jobId}/prompts/${promptId}/test-runs/${testRunId}/approve`, { method: 'POST' })
  },
}
