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
  RubricCategory,
  MustHave,
  AggregationStrategy,
} from '@/types'

const delay = (ms: number) => new Promise(resolve => setTimeout(resolve, ms))

const generateMockJobs = (): Job[] => {
  return [
    {
      jobId: 'job-001',
      title: 'Senior Software Engineer',
      department: 'Engineering',
      createdBy: 'recruiter@company.com',
      createdAt: new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString(),
      status: 'Processing',
      currentVersion: {
        versionId: 'v1-001',
        jobId: 'job-001',
        rubric: [
          { id: 'r1', name: 'Technical Skills', description: 'Programming languages, frameworks, tools', weight: 0.35 },
          { id: 'r2', name: 'Experience', description: 'Years of experience and relevant projects', weight: 0.25 },
          { id: 'r3', name: 'Problem Solving', description: 'Analytical and critical thinking abilities', weight: 0.20 },
          { id: 'r4', name: 'Communication', description: 'Written and verbal communication skills', weight: 0.10 },
          { id: 'r5', name: 'Cultural Fit', description: 'Alignment with company values', weight: 0.10 },
        ],
        mustHaves: [
          { id: 'm1', criterion: 'Bachelor\'s degree in Computer Science or related field', description: 'Educational requirement' },
          { id: 'm2', criterion: '5+ years of professional software development experience', description: 'Experience requirement' },
          { id: 'm3', criterion: 'Proficiency in React and TypeScript', description: 'Technical requirement' },
        ],
        runsPerApplication: 3,
        aggregationStrategy: 'median',
        longlistThreshold: 60,
        shortlistThreshold: 75,
        varianceThreshold: 15,
        createdAt: new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString(),
      },
      stats: {
        totalApplications: 342,
        queued: 12,
        extracting: 8,
        scoring: 45,
        completed: 265,
        failed: 5,
        needsManualReview: 7,
        longlistCount: 89,
        shortlistCount: 34,
        excludedCount: 219,
      },
    },
    {
      jobId: 'job-002',
      title: 'Product Manager',
      department: 'Product',
      createdBy: 'hiring.manager@company.com',
      createdAt: new Date(Date.now() - 3 * 24 * 60 * 60 * 1000).toISOString(),
      status: 'Active',
      currentVersion: {
        versionId: 'v1-002',
        jobId: 'job-002',
        rubric: [
          { id: 'r1', name: 'Product Vision', description: 'Strategic thinking and roadmap planning', weight: 0.30 },
          { id: 'r2', name: 'Stakeholder Management', description: 'Cross-functional collaboration', weight: 0.25 },
          { id: 'r3', name: 'Data-Driven Decision Making', description: 'Analytics and metrics expertise', weight: 0.20 },
          { id: 'r4', name: 'User Empathy', description: 'Customer-centric mindset', weight: 0.15 },
          { id: 'r5', name: 'Execution', description: 'Ability to ship products', weight: 0.10 },
        ],
        mustHaves: [
          { id: 'm1', criterion: '3+ years of product management experience', description: 'Experience requirement' },
          { id: 'm2', criterion: 'Experience with B2B SaaS products', description: 'Industry requirement' },
        ],
        runsPerApplication: 3,
        aggregationStrategy: 'median',
        longlistThreshold: 65,
        shortlistThreshold: 80,
        varianceThreshold: 12,
        createdAt: new Date(Date.now() - 3 * 24 * 60 * 60 * 1000).toISOString(),
      },
      stats: {
        totalApplications: 156,
        queued: 78,
        extracting: 23,
        scoring: 31,
        completed: 22,
        failed: 1,
        needsManualReview: 1,
        longlistCount: 8,
        shortlistCount: 3,
        excludedCount: 14,
      },
    },
    {
      jobId: 'job-003',
      title: 'UX Designer',
      department: 'Design',
      createdBy: 'design.lead@company.com',
      createdAt: new Date(Date.now() - 1 * 24 * 60 * 60 * 1000).toISOString(),
      status: 'Active',
      currentVersion: {
        versionId: 'v1-003',
        jobId: 'job-003',
        rubric: [
          { id: 'r1', name: 'Design Skills', description: 'UI/UX design proficiency', weight: 0.40 },
          { id: 'r2', name: 'Portfolio Quality', description: 'Case studies and design work', weight: 0.30 },
          { id: 'r3', name: 'User Research', description: 'Research methodologies', weight: 0.20 },
          { id: 'r4', name: 'Collaboration', description: 'Working with engineers and PMs', weight: 0.10 },
        ],
        mustHaves: [
          { id: 'm1', criterion: 'Portfolio demonstrating UX process', description: 'Portfolio requirement' },
          { id: 'm2', criterion: 'Experience with Figma or similar tools', description: 'Technical requirement' },
        ],
        runsPerApplication: 3,
        aggregationStrategy: 'median',
        longlistThreshold: 70,
        shortlistThreshold: 85,
        varianceThreshold: 10,
        createdAt: new Date(Date.now() - 1 * 24 * 60 * 60 * 1000).toISOString(),
      },
      stats: {
        totalApplications: 89,
        queued: 62,
        extracting: 15,
        scoring: 8,
        completed: 3,
        failed: 1,
        needsManualReview: 0,
        longlistCount: 1,
        shortlistCount: 0,
        excludedCount: 2,
      },
    },
  ]
}

const generateMockApplications = (jobId: string, count: number = 20): Application[] => {
  const statuses: Application['status'][] = [
    'Completed',
    'Completed',
    'Completed',
    'Completed',
    'Scoring',
    'Scoring',
    'Extracting',
    'Queued',
    'NeedsManualReview',
    'ExtractionFailed',
  ]

  return Array.from({ length: count }, (_, i) => ({
    applicationId: `app-${jobId}-${String(i + 1).padStart(3, '0')}`,
    jobId,
    candidateRef: `candidate-${String(i + 1).padStart(5, '0')}`,
    candidateName: `Candidate ${i + 1}`,
    candidateEmail: `candidate${i + 1}@email.com`,
    status: statuses[i % statuses.length],
    createdAt: new Date(Date.now() - (count - i) * 60 * 60 * 1000).toISOString(),
    documents: [
      {
        documentId: `doc-${i}-1`,
        applicationId: `app-${jobId}-${String(i + 1).padStart(3, '0')}`,
        fileName: `Resume_Candidate_${i + 1}.pdf`,
        mimeType: 'application/pdf',
        sizeBytes: 245000 + i * 1000,
        sha256: `sha256-${i}-resume`,
        uploadedAt: new Date(Date.now() - (count - i) * 60 * 60 * 1000).toISOString(),
      },
      {
        documentId: `doc-${i}-2`,
        applicationId: `app-${jobId}-${String(i + 1).padStart(3, '0')}`,
        fileName: `CoverLetter_Candidate_${i + 1}.pdf`,
        mimeType: 'application/pdf',
        sizeBytes: 78000 + i * 500,
        sha256: `sha256-${i}-cover`,
        uploadedAt: new Date(Date.now() - (count - i) * 60 * 60 * 1000).toISOString(),
      },
    ],
    finalScore: statuses[i % statuses.length] === 'Completed' ? 55 + Math.random() * 40 : undefined,
    finalDecision:
      statuses[i % statuses.length] === 'Completed'
        ? Math.random() > 0.5
          ? 'Eligible'
          : 'Excluded'
        : statuses[i % statuses.length] === 'NeedsManualReview'
        ? 'NeedsManualReview'
        : undefined,
    variance: statuses[i % statuses.length] === 'Completed' ? Math.random() * 20 : undefined,
    flagged: statuses[i % statuses.length] === 'NeedsManualReview' || Math.random() > 0.9,
  }))
}

const generateMockScoringRuns = (applicationId: string, count: number = 3): ScoringRun[] => {
  return Array.from({ length: count }, (_, i) => ({
    runId: `run-${applicationId}-${i + 1}`,
    applicationId,
    versionId: 'v1-001',
    runIndex: i + 1,
    modelDeploymentId: 'gpt-4o-deployment-001',
    promptVersionId: 'prompt-v2.3',
    overallScore: 70 + Math.random() * 25,
    subScores: {
      'Technical Skills': 75 + Math.random() * 20,
      Experience: 68 + Math.random() * 25,
      'Problem Solving': 72 + Math.random() * 23,
      Communication: 80 + Math.random() * 15,
      'Cultural Fit': 65 + Math.random() * 30,
    },
    mustHaveResult: {
      passed: true,
      missingCriteria: [],
      details: {
        "Bachelor's degree in Computer Science or related field": true,
        '5+ years of professional software development experience': true,
        'Proficiency in React and TypeScript': true,
      },
    },
    evidenceCitations: [
      {
        category: 'Technical Skills',
        snippet: 'Extensive experience with React, TypeScript, and modern web development practices...',
        section: 'Skills',
        confidence: 0.92,
      },
      {
        category: 'Experience',
        snippet: 'Led development of enterprise SaaS platform for 5 years at TechCorp...',
        section: 'Work Experience',
        confidence: 0.88,
      },
    ],
    rationale: 'Strong technical background with proven experience in modern web technologies. Demonstrates excellent problem-solving abilities through documented projects.',
    improvementRecommendations: [
      'Consider highlighting leadership experience more prominently',
      'Add specific metrics for project impact',
      'Include more details about system architecture decisions',
    ],
    createdAt: new Date(Date.now() - (3 - i) * 5 * 60 * 1000).toISOString(),
    durationMs: 12000 + Math.random() * 8000,
    tokenUsage: {
      promptTokens: 3200 + Math.floor(Math.random() * 500),
      completionTokens: 850 + Math.floor(Math.random() * 200),
      totalTokens: 4050 + Math.floor(Math.random() * 700),
    },
    status: 'Success',
  }))
}

export const mockAPI = {
  async getSystemStats(): Promise<SystemStats> {
    await delay(300)
    return {
      totalJobs: 12,
      activeJobs: 5,
      totalApplications: 4523,
      queuedApplications: 234,
      processingApplications: 156,
      completedApplications: 4098,
      failedApplications: 35,
      averageThroughputPerHour: 187,
    }
  },

  async getJobs(): Promise<Job[]> {
    await delay(400)
    return generateMockJobs()
  },

  async getJob(jobId: string): Promise<Job | null> {
    await delay(200)
    const jobs = generateMockJobs()
    return jobs.find((j) => j.jobId === jobId) || null
  },

  async createJob(data: {
    title: string
    department: string
    rubric: RubricCategory[]
    mustHaves: MustHave[]
    runsPerApplication: number
    aggregationStrategy: AggregationStrategy
    longlistThreshold: number
    shortlistThreshold: number
  }): Promise<Job> {
    await delay(500)
    const newJob: Job = {
      jobId: `job-${Date.now()}`,
      title: data.title,
      department: data.department,
      createdBy: 'current.user@company.com',
      createdAt: new Date().toISOString(),
      status: 'Draft',
      currentVersion: {
        versionId: `v1-${Date.now()}`,
        jobId: `job-${Date.now()}`,
        rubric: data.rubric,
        mustHaves: data.mustHaves,
        runsPerApplication: data.runsPerApplication,
        aggregationStrategy: data.aggregationStrategy,
        longlistThreshold: data.longlistThreshold,
        shortlistThreshold: data.shortlistThreshold,
        varianceThreshold: 15,
        createdAt: new Date().toISOString(),
      },
      stats: {
        totalApplications: 0,
        queued: 0,
        extracting: 0,
        scoring: 0,
        completed: 0,
        failed: 0,
        needsManualReview: 0,
        longlistCount: 0,
        shortlistCount: 0,
        excludedCount: 0,
      },
    }
    return newJob
  },

  async getApplications(jobId: string, filters?: {
    status?: string
    minScore?: number
    maxScore?: number
    decision?: string
  }): Promise<Application[]> {
    await delay(500)
    let apps = generateMockApplications(jobId, 50)
    
    if (filters?.status) {
      apps = apps.filter(a => a.status === filters.status)
    }
    if (filters?.decision) {
      apps = apps.filter(a => a.finalDecision === filters.decision)
    }
    if (filters?.minScore !== undefined) {
      const minScore = filters.minScore
      apps = apps.filter(a => a.finalScore !== undefined && a.finalScore >= minScore)
    }
    if (filters?.maxScore !== undefined) {
      const maxScore = filters.maxScore
      apps = apps.filter(a => a.finalScore !== undefined && a.finalScore <= maxScore)
    }
    
    return apps
  },

  async getApplication(applicationId: string): Promise<Application | null> {
    await delay(300)
    const apps = generateMockApplications('job-001', 50)
    return apps.find((a) => a.applicationId === applicationId) || null
  },

  async getScoringRuns(applicationId: string): Promise<ScoringRun[]> {
    await delay(400)
    return generateMockScoringRuns(applicationId)
  },

  async getAggregatedResult(applicationId: string): Promise<AggregatedResult | null> {
    await delay(300)
    const runs = generateMockScoringRuns(applicationId)
    const scores = runs.map((r) => r.overallScore)
    const finalScore = scores.reduce((a, b) => a + b, 0) / scores.length
    const variance = Math.sqrt(scores.reduce((sum, s) => sum + Math.pow(s - finalScore, 2), 0) / scores.length)

    return {
      resultId: `result-${applicationId}`,
      applicationId,
      versionId: 'v1-001',
      finalScore,
      finalSubScores: {
        'Technical Skills': 78,
        Experience: 72,
        'Problem Solving': 75,
        Communication: 82,
        'Cultural Fit': 70,
      },
      confidence: 0.85,
      variance,
      finalDecision: finalScore >= 75 ? 'Eligible' : finalScore >= 60 ? 'Eligible' : 'Excluded',
      rationaleText: 'Consolidated assessment across 3 scoring runs shows consistent strong performance in technical skills and communication.',
      recommendationsText: 'Focus on quantifying impact in future applications. Add more detail about architectural decisions and team leadership.',
      allRuns: runs,
      createdAt: new Date().toISOString(),
    }
  },

  async getExtractionArtifact(applicationId: string): Promise<ExtractionArtifact | null> {
    await delay(200)
    return {
      artifactId: `extract-${applicationId}`,
      applicationId,
      markdown: `# Resume: John Doe\n\n## Professional Summary\nExperienced software engineer with 7+ years building scalable web applications...\n\n## Skills\n- React, TypeScript, Node.js\n- Python, Go\n- AWS, Docker, Kubernetes\n\n## Experience\n\n### Senior Software Engineer | TechCorp Inc.\n*2019 - Present*\n\n- Led development of microservices architecture serving 1M+ users\n- Reduced API latency by 40% through optimization\n- Mentored junior engineers\n\n### Software Engineer | StartupCo\n*2016 - 2019*\n\n- Built MVP for SaaS product\n- Implemented CI/CD pipeline\n\n## Education\n\n**B.S. Computer Science** | State University | 2016`,
      extractionMetadata: {
        toolVersion: 'azure-doc-intelligence-v4.0',
        confidence: 0.94,
        extractedAt: new Date(Date.now() - 30 * 60 * 1000).toISOString(),
      },
      status: 'Success',
      createdAt: new Date(Date.now() - 30 * 60 * 1000).toISOString(),
    }
  },

  async getDLQItems(): Promise<DLQItem[]> {
    await delay(400)
    return [
      {
        itemId: 'dlq-001',
        applicationId: 'app-job-001-042',
        jobId: 'job-001',
        failureType: 'Extraction',
        failureReason: 'Corrupted PDF file - unable to extract text content',
        attemptCount: 3,
        firstFailedAt: new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString(),
        lastAttemptedAt: new Date(Date.now() - 30 * 60 * 1000).toISOString(),
        canRetry: true,
      },
      {
        itemId: 'dlq-002',
        applicationId: 'app-job-001-089',
        jobId: 'job-001',
        failureType: 'Scoring',
        failureReason: 'Model timeout - exceeded 60 second limit',
        attemptCount: 2,
        firstFailedAt: new Date(Date.now() - 1 * 60 * 60 * 1000).toISOString(),
        lastAttemptedAt: new Date(Date.now() - 15 * 60 * 1000).toISOString(),
        canRetry: true,
        notes: 'Large document size (5MB+) may be causing timeout',
      },
    ]
  },

  async retryDLQItem(itemId: string): Promise<void> {
    await delay(500)
  },

  async uploadApplications(jobId: string, files: File[]): Promise<{ applicationIds: string[] }> {
    await delay(2000)
    return {
      applicationIds: files.map((_, i) => `app-${jobId}-new-${i + 1}`),
    }
  },

  async getAuditEvents(filters?: {
    entityType?: string
    entityId?: string
    startDate?: string
    endDate?: string
  }): Promise<ProcessingEvent[]> {
    await delay(400)
    return [
      {
        eventId: 'evt-001',
        timestamp: new Date(Date.now() - 10 * 60 * 1000).toISOString(),
        actor: 'recruiter@company.com',
        action: 'job.created',
        entityType: 'Job',
        entityId: 'job-001',
        correlationId: 'corr-123',
        details: { title: 'Senior Software Engineer' },
      },
      {
        eventId: 'evt-002',
        timestamp: new Date(Date.now() - 5 * 60 * 1000).toISOString(),
        actor: 'system',
        action: 'application.scored',
        entityType: 'Application',
        entityId: 'app-job-001-001',
        correlationId: 'corr-124',
        details: { score: 78, decision: 'Eligible' },
      },
    ]
  },
}
