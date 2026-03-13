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
  DesiredCriteria,
  AggregationStrategy,
  RubricSource,
  ScoringPrompt,
  PromptTestRun,
} from '@/types'
import { kv } from '@/lib/spark-client'

const delay = (ms: number) => new Promise(resolve => setTimeout(resolve, ms))

const generateJobCode = (title: string, department: string): string => {
  const titlePart = title.split(' ').map(w => w[0]).join('').slice(0, 3).toUpperCase()
  const deptPart = department.slice(0, 3).toUpperCase()
  const randomPart = Math.random().toString(36).substring(2, 6).toUpperCase()
  return `${titlePart}-${deptPart}-${randomPart}`
}

const calculateJobStats = (jobId: string, longlistThreshold: number, shortlistThreshold: number) => {
  const apps = generateMockApplications(jobId, 50)
  
  const longlistCount = apps.filter(
    a => a.finalDecision === 'Eligible' && a.finalScore && a.finalScore >= longlistThreshold
  ).length
  
  const shortlistCount = apps.filter(
    a => a.finalDecision === 'Eligible' && a.finalScore && a.finalScore >= shortlistThreshold
  ).length
  
  const needsManualReview = apps.filter(
    a => a.finalDecision === 'NeedsManualReview' || a.status === 'NeedsManualReview'
  ).length
  
  const excludedCount = apps.filter(a => a.finalDecision === 'Excluded').length
  const queued = apps.filter(a => a.status === 'Queued').length
  const extracting = apps.filter(a => a.status === 'Extracting').length
  const scoring = apps.filter(a => a.status === 'Scoring').length
  const completed = apps.filter(a => a.status === 'Completed').length
  const failed = apps.filter(a => a.status === 'ExtractionFailed' || a.status === 'ScoringFailed').length
  
  return {
    totalApplications: apps.length,
    queued,
    extracting,
    scoring,
    completed,
    failed,
    needsManualReview,
    longlistCount,
    shortlistCount,
    excludedCount,
  }
}

const getDefaultJobs = (): Job[] => {
  return [
    {
      jobId: 'job-001',
      jobCode: 'SSE-ENG-A1B2',
      title: 'Senior Software Engineer',
      department: 'Engineering',
      organization: 'TechCorp Solutions',
      postingDate: new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString(),
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
        desiredCriteria: [],
        runsPerApplication: 3,
        aggregationStrategy: 'median',
        longlistThreshold: 60,
        shortlistThreshold: 75,
        varianceThreshold: 15,
        rubricApprovalStatus: 'approved',
        rubricSource: 'manual',
        createdAt: new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString(),
      },
    },
    {
      jobId: 'job-002',
      jobCode: 'PM-PRO-C3D4',
      title: 'Product Manager',
      department: 'Product',
      organization: 'TechCorp Solutions',
      postingDate: new Date(Date.now() - 3 * 24 * 60 * 60 * 1000).toISOString(),
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
        desiredCriteria: [],
        runsPerApplication: 3,
        aggregationStrategy: 'median',
        longlistThreshold: 65,
        shortlistThreshold: 80,
        varianceThreshold: 12,
        rubricApprovalStatus: 'approved',
        rubricSource: 'manual',
        createdAt: new Date(Date.now() - 3 * 24 * 60 * 60 * 1000).toISOString(),
      },
    },
    {
      jobId: 'job-003',
      jobCode: 'UD-DES-E5F6',
      title: 'UX Designer',
      department: 'Design',
      organization: 'DesignHub Inc',
      postingDate: new Date(Date.now() - 1 * 24 * 60 * 60 * 1000).toISOString(),
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
        desiredCriteria: [],
        runsPerApplication: 3,
        aggregationStrategy: 'median',
        longlistThreshold: 70,
        shortlistThreshold: 85,
        varianceThreshold: 10,
        rubricApprovalStatus: 'approved',
        rubricSource: 'manual',
        createdAt: new Date(Date.now() - 1 * 24 * 60 * 60 * 1000).toISOString(),
      },
    },
  ]
}

const generateMockJobs = async (): Promise<Job[]> => {
  const storedJobs = await kv.get<Job[]>('jobs') || getDefaultJobs()
  
  return storedJobs.map(job => ({
    ...job,
    get stats() {
      return calculateJobStats(job.jobId, job.currentVersion.longlistThreshold, job.currentVersion.shortlistThreshold)
    },
  }))
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

const generateMockScoringRuns = (applicationId: string, rubricCategories: RubricCategory[], mustHaves: MustHave[], count: number = 3): ScoringRun[] => {
  const subScores: Record<string, number> = {}
  rubricCategories.forEach(cat => {
    subScores[cat.name] = 65 + Math.random() * 30
  })

  const mustHaveDetails: Record<string, boolean> = {}
  mustHaves.forEach(mh => {
    mustHaveDetails[mh.criterion] = Math.random() > 0.2
  })

  const firstCategory = rubricCategories[0]?.name || 'Skills'
  const secondCategory = rubricCategories[1]?.name || 'Experience'

  return Array.from({ length: count }, (_, i) => ({
    runId: `run-${applicationId}-${i + 1}`,
    applicationId,
    versionId: 'v1-001',
    runIndex: i + 1,
    modelDeploymentId: 'gpt-4o-deployment-001',
    promptVersionId: 'prompt-v2.3',
    overallScore: 70 + Math.random() * 25,
    subScores,
    mustHaveResult: {
      passed: Object.values(mustHaveDetails).every(v => v),
      missingCriteria: mustHaves.filter(mh => !mustHaveDetails[mh.criterion]).map(mh => mh.criterion),
      details: mustHaveDetails,
    },
    evidenceCitations: [
      {
        category: firstCategory,
        snippet: `Strong demonstration of ${firstCategory.toLowerCase()} through documented experience and achievements...`,
        section: 'Skills',
        confidence: 0.92,
      },
      {
        category: secondCategory,
        snippet: `Excellent ${secondCategory.toLowerCase()} as evidenced by career progression and accomplishments...`,
        section: 'Work Experience',
        confidence: 0.88,
      },
    ],
    rationale: 'Strong background with proven capabilities. Demonstrates excellent abilities through documented projects and achievements.',
    improvementRecommendations: [
      'Consider highlighting leadership experience more prominently',
      'Add specific metrics for project impact',
      'Include more details about key decisions and outcomes',
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
    const jobs = await generateMockJobs()
    const activeJobs = jobs.filter(j => j.status === 'Active' || j.status === 'Processing').length
    
    return {
      totalJobs: jobs.length,
      activeJobs,
      totalApplications: 4523,
      queuedApplications: 234,
      processingApplications: 156,
      completedApplications: 4098,
      failedApplications: 35,
      averageThroughputPerHour: 187,
    }
  },

  async extractJobSpec(_fileName: string, _content: string, _mimeType: string): Promise<any> {
    await delay(800)
    return {
      title: 'Senior Software Engineer',
      department: 'Engineering',
      organization: 'TechCorp Solutions',
      jobDescription: 'We are seeking a Senior Software Engineer to join our Engineering team.',
      mustHaves: [
        { criterion: 'Bachelor\'s degree in Computer Science', description: 'Educational requirement' },
        { criterion: '5+ years of professional experience', description: 'Experience requirement' },
      ],
      desiredCriteria: [
        { qualification: 'Experience with cloud platforms (AWS, Azure)', description: 'Cloud experience preferred' },
        { qualification: 'Open-source contributions', description: 'Community involvement' },
      ],
      rubric: [],
    }
  },

  async extractRubric(_fileName: string, _content: string, _mimeType: string): Promise<any> {
    await delay(800)
    return {
      title: 'Senior Software Engineer',
      categories: [
        { name: 'Technical Skills', description: 'Programming languages, frameworks, tools', weight: 0.35 },
        { name: 'Experience', description: 'Years and relevant projects', weight: 0.25 },
        { name: 'Problem Solving', description: 'Analytical thinking', weight: 0.20 },
        { name: 'Communication', description: 'Written and verbal skills', weight: 0.10 },
        { name: 'Cultural Fit', description: 'Company values alignment', weight: 0.10 },
      ],
    }
  },

  async getJobs(): Promise<Job[]> {
    await delay(400)
    return await generateMockJobs()
  },

  async getJob(jobId: string): Promise<Job | null> {
    await delay(200)
    const jobs = await generateMockJobs()
    return jobs.find((j) => j.jobId === jobId) || null
  },

  async createJob(data: {
    title: string
    department: string
    organization: string
    postingDate: string
    rubric: RubricCategory[]
    mustHaves: MustHave[]
    desiredCriteria?: DesiredCriteria[]
    jobDescription?: string
    runsPerApplication: number
    aggregationStrategy: AggregationStrategy
    longlistThreshold: number
    shortlistThreshold: number
    specDocumentId?: string
    rubricDocumentId?: string
    jobCode?: string
    rubricSource?: RubricSource
    rawExtractionResponse?: string
  }): Promise<Job> {
    await delay(500)
    const jobId = `job-${Date.now()}`
    const jobCode = data.jobCode || generateJobCode(data.title, data.department)
    const rubricSource = data.rubricSource || 'manual'
    const newJob: Job = {
      jobId,
      jobCode,
      title: data.title,
      department: data.department,
      organization: data.organization,
      postingDate: data.postingDate,
      createdBy: 'current.user@company.com',
      createdAt: new Date().toISOString(),
      status: 'Active',
      specDocumentId: data.specDocumentId,
      rubricDocumentId: data.rubricDocumentId,
      jobDescription: data.jobDescription,
      currentVersion: {
        versionId: `v1-${Date.now()}`,
        jobId,
        rubric: data.rubric,
        mustHaves: data.mustHaves,
        desiredCriteria: data.desiredCriteria || [],
        runsPerApplication: data.runsPerApplication,
        aggregationStrategy: data.aggregationStrategy,
        longlistThreshold: data.longlistThreshold,
        shortlistThreshold: data.shortlistThreshold,
        varianceThreshold: 15,
        rubricApprovalStatus: rubricSource === 'manual' ? 'approved' : 'draft',
        rubricSource,
        rawExtractionResponse: data.rawExtractionResponse,
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
    
    const existingJobs = await kv.get<Job[]>('jobs') || getDefaultJobs()
    await kv.set('jobs', [...existingJobs, newJob])
    
    return newJob
  },

  async updateJob(jobId: string, data: {
    title: string
    department: string
    organization: string
    postingDate: string
    rubric: RubricCategory[]
    mustHaves: MustHave[]
    desiredCriteria?: DesiredCriteria[]
    jobDescription?: string
    runsPerApplication: number
    aggregationStrategy: AggregationStrategy
    longlistThreshold: number
    shortlistThreshold: number
    specDocumentId?: string
    rubricDocumentId?: string
    jobCode?: string
    rubricSource?: RubricSource
    rawExtractionResponse?: string
  }): Promise<Job> {
    await delay(500)
    const jobs = await kv.get<Job[]>('jobs') || getDefaultJobs()
    const jobIndex = jobs.findIndex(j => j.jobId === jobId)
    
    if (jobIndex === -1) {
      throw new Error('Job not found')
    }
    
    const existingJob = jobs[jobIndex]
    const updatedJob: Job = {
      ...existingJob,
      jobCode: data.jobCode || existingJob.jobCode,
      title: data.title,
      department: data.department,
      organization: data.organization,
      postingDate: data.postingDate,
      specDocumentId: data.specDocumentId,
      rubricDocumentId: data.rubricDocumentId,
      jobDescription: data.jobDescription,
      currentVersion: {
        versionId: `v${Date.now()}`,
        jobId,
        rubric: data.rubric,
        mustHaves: data.mustHaves,
        desiredCriteria: data.desiredCriteria || [],
        runsPerApplication: data.runsPerApplication,
        aggregationStrategy: data.aggregationStrategy,
        longlistThreshold: data.longlistThreshold,
        shortlistThreshold: data.shortlistThreshold,
        varianceThreshold: 15,
        rubricApprovalStatus: 'draft',
        rubricSource: data.rubricSource || 'manual',
        rawExtractionResponse: data.rawExtractionResponse,
        createdAt: new Date().toISOString(),
      },
    }
    
    jobs[jobIndex] = updatedJob
    await kv.set('jobs', jobs)
    
    return updatedJob
  },

  async updateJobRubric(jobId: string, rubricDocumentId: string): Promise<void> {
    await delay(300)
    const jobs = await kv.get<Job[]>('jobs') || getDefaultJobs()
    const jobIndex = jobs.findIndex(j => j.jobId === jobId)
    
    if (jobIndex !== -1) {
      jobs[jobIndex].rubricDocumentId = rubricDocumentId
      await kv.set('jobs', jobs)
    }
  },

  async updateRubricApproval(jobId: string, status: 'approved' | 'draft'): Promise<{ versionId: string; rubricApprovalStatus: string; updatedAt: string }> {
    await delay(300)
    const jobs = await kv.get<Job[]>('jobs') || getDefaultJobs()
    const jobIndex = jobs.findIndex(j => j.jobId === jobId)
    
    if (jobIndex === -1) {
      throw new Error('Job not found')
    }
    
    const currentVersion = jobs[jobIndex].currentVersion
    currentVersion.rubricApprovalStatus = status
    jobs[jobIndex] = { ...jobs[jobIndex], currentVersion }
    await kv.set('jobs', jobs)

    return {
      versionId: currentVersion.versionId,
      rubricApprovalStatus: status,
      updatedAt: new Date().toISOString(),
    }
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
    const jobs = await generateMockJobs()
    for (const job of jobs) {
      const apps = generateMockApplications(job.jobId, 50)
      const found = apps.find((a) => a.applicationId === applicationId)
      if (found) return found
    }
    return null
  },

  async getScoringRuns(applicationId: string): Promise<ScoringRun[]> {
    await delay(400)
    
    const jobs = await generateMockJobs()
    let job: Job | undefined
    
    for (const j of jobs) {
      const apps = generateMockApplications(j.jobId, 50)
      if (apps.find(a => a.applicationId === applicationId)) {
        job = j
        break
      }
    }
    
    if (!job) {
      const defaultJob = jobs[0]
      return generateMockScoringRuns(applicationId, defaultJob.currentVersion.rubric, defaultJob.currentVersion.mustHaves)
    }
    
    return generateMockScoringRuns(applicationId, job.currentVersion.rubric, job.currentVersion.mustHaves)
  },

  async getAggregatedResult(applicationId: string): Promise<AggregatedResult | null> {
    await delay(300)
    
    const jobs = await generateMockJobs()
    let job: Job | undefined
    
    for (const j of jobs) {
      const apps = generateMockApplications(j.jobId, 50)
      if (apps.find(a => a.applicationId === applicationId)) {
        job = j
        break
      }
    }
    
    if (!job) {
      const defaultJob = jobs[0]
      job = defaultJob
    }
    
    const runs = generateMockScoringRuns(applicationId, job.currentVersion.rubric, job.currentVersion.mustHaves)
    const scores = runs.map((r) => r.overallScore)
    const finalScore = scores.reduce((a, b) => a + b, 0) / scores.length
    const variance = Math.sqrt(scores.reduce((sum, s) => sum + Math.pow(s - finalScore, 2), 0) / scores.length)

    const finalSubScores: Record<string, number> = {}
    job.currentVersion.rubric.forEach(cat => {
      const catScores = runs.map(r => r.subScores[cat.name] || 0)
      finalSubScores[cat.name] = catScores.reduce((a, b) => a + b, 0) / catScores.length
    })

    return {
      resultId: `result-${applicationId}`,
      applicationId,
      versionId: job.currentVersion.versionId,
      finalScore,
      finalSubScores,
      confidence: 0.85,
      variance,
      finalDecision: finalScore >= 75 ? 'Eligible' : finalScore >= 60 ? 'Eligible' : 'Excluded',
      rationaleText: `Consolidated assessment across ${runs.length} scoring runs shows consistent performance across evaluation criteria.`,
      recommendationsText: 'Focus on quantifying impact in future applications. Add more detail about key decisions and team contributions.',
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

  async getRecruiterAnalytics(): Promise<import('@/types').RecruiterAnalytics[]> {
    await delay(500)
    return [
      {
        recruiterId: 'user-rec-001',
        recruiterName: 'Sarah Johnson',
        department: 'Engineering',
        applicationsInQueue: 45,
        manualReviewsPerformed: 23,
        shortlistRecommendations: 18,
        averageProcessingTime: 2.5,
        activeJobs: 3,
      },
      {
        recruiterId: 'user-rec-002',
        recruiterName: 'Michael Chen',
        department: 'Engineering',
        applicationsInQueue: 32,
        manualReviewsPerformed: 15,
        shortlistRecommendations: 12,
        averageProcessingTime: 3.1,
        activeJobs: 2,
      },
      {
        recruiterId: 'user-rec-003',
        recruiterName: 'Emily Rodriguez',
        department: 'Product',
        applicationsInQueue: 28,
        manualReviewsPerformed: 19,
        shortlistRecommendations: 14,
        averageProcessingTime: 2.8,
        activeJobs: 2,
      },
      {
        recruiterId: 'user-rec-004',
        recruiterName: 'David Park',
        department: 'Design',
        applicationsInQueue: 15,
        manualReviewsPerformed: 10,
        shortlistRecommendations: 8,
        averageProcessingTime: 2.2,
        activeJobs: 1,
      },
      {
        recruiterId: 'user-rec-005',
        recruiterName: 'Lisa Thompson',
        department: 'Product',
        applicationsInQueue: 22,
        manualReviewsPerformed: 12,
        shortlistRecommendations: 9,
        averageProcessingTime: 2.9,
        activeJobs: 2,
      },
    ]
  },

  async getDepartmentAnalytics(): Promise<import('@/types').DepartmentAnalytics[]> {
    await delay(500)
    const recruiterData = await this.getRecruiterAnalytics()
    
    const departments = Array.from(new Set(recruiterData.map(r => r.department))) as string[]
    
    return departments.map(dept => {
      const deptRecruiters = recruiterData.filter(r => r.department === dept)
      return {
        department: dept,
        totalRecruiters: deptRecruiters.length,
        applicationsInQueue: deptRecruiters.reduce((sum, r) => sum + r.applicationsInQueue, 0),
        manualReviewsPerformed: deptRecruiters.reduce((sum, r) => sum + r.manualReviewsPerformed, 0),
        shortlistRecommendations: deptRecruiters.reduce((sum, r) => sum + r.shortlistRecommendations, 0),
        activeJobs: deptRecruiters.reduce((sum, r) => sum + r.activeJobs, 0),
        recruiters: deptRecruiters,
      }
    })
  },

  // Scoring Prompts (US3a)
  async getPrompts(jobId: string): Promise<ScoringPrompt[]> {
    await delay(300)
    return [{
      promptId: `mock-prompt-${jobId}-1`, jobId, versionNumber: 1,
      promptText: 'Evaluate the candidate based on the provided rubric categories...',
      status: 'draft', createdAt: new Date().toISOString(), lastModifiedAt: new Date().toISOString(),
      author: 'admin', rating: 4, comments: 'Initial draft prompt', source: 'manual',
    }]
  },

  async createPrompt(jobId: string, data: { promptText: string; source: string; generationMetadata?: Record<string, any> }): Promise<ScoringPrompt> {
    await delay(500)
    return {
      promptId: `mock-prompt-${Date.now()}`, jobId, versionNumber: 1, promptText: data.promptText,
      status: 'draft', createdAt: new Date().toISOString(), lastModifiedAt: new Date().toISOString(),
      author: 'admin', source: data.source as ScoringPrompt['source'], generationMetadata: data.generationMetadata,
    }
  },

  async getPrompt(jobId: string, promptId: string): Promise<ScoringPrompt> {
    await delay(200)
    return {
      promptId, jobId, versionNumber: 1, promptText: 'Mock prompt text...', status: 'draft',
      createdAt: new Date().toISOString(), lastModifiedAt: new Date().toISOString(), author: 'admin', source: 'manual',
    }
  },

  async editPrompt(jobId: string, promptId: string, data: { promptText: string }): Promise<ScoringPrompt> {
    await delay(500)
    return {
      promptId: `mock-prompt-${Date.now()}`, jobId, versionNumber: 2, promptText: data.promptText,
      status: 'draft', createdAt: new Date().toISOString(), lastModifiedAt: new Date().toISOString(), author: 'admin', source: 'manual',
    }
  },

  async activatePrompt(jobId: string, promptId: string): Promise<ScoringPrompt> {
    await delay(300)
    return {
      promptId, jobId, versionNumber: 1, promptText: 'Activated prompt...', status: 'active',
      createdAt: new Date().toISOString(), lastModifiedAt: new Date().toISOString(), author: 'admin', source: 'manual',
    }
  },

  async ratePrompt(jobId: string, promptId: string, data: { rating: number; comments?: string }): Promise<ScoringPrompt> {
    await delay(300)
    return {
      promptId, jobId, versionNumber: 1, promptText: 'Rated prompt...', status: 'active',
      createdAt: new Date().toISOString(), lastModifiedAt: new Date().toISOString(), author: 'admin',
      rating: data.rating, comments: data.comments, source: 'manual',
    }
  },

  async generatePrompt(jobId: string): Promise<{ promptText: string; generationMetadata: Record<string, any> }> {
    await delay(2000)
    return {
      promptText: 'You are evaluating a candidate for a position. Score each rubric category from 0-100 based on evidence from their documents.',
      generationMetadata: { source: 'mock', timestamp: new Date().toISOString() },
    }
  },

  async approvePromptForProduction(jobId: string, promptId: string): Promise<ScoringPrompt> {
    await delay(500)
    return {
      promptId, jobId, versionNumber: 1, promptText: 'Production-approved prompt...', status: 'production-approved',
      createdAt: new Date().toISOString(), lastModifiedAt: new Date().toISOString(), author: 'admin', source: 'manual',
    }
  },

  async createTestRun(jobId: string, promptId: string, files: Array<{ fileName: string; content: string; mimeType: string; sizeBytes: number }>): Promise<PromptTestRun> {
    await delay(1000)
    return {
      testRunId: `mock-test-run-${Date.now()}`, jobId, promptId, status: 'pending_review',
      applicationIds: files.map((_, i) => `mock-test-app-${i}`), createdAt: new Date().toISOString(),
    }
  },

  async getTestRuns(jobId: string, promptId: string): Promise<PromptTestRun[]> {
    await delay(300)
    return []
  },

  async getTestRun(jobId: string, promptId: string, testRunId: string): Promise<PromptTestRun & { applications?: Application[] }> {
    await delay(300)
    return { testRunId, jobId, promptId, status: 'pending_review', applicationIds: [], createdAt: new Date().toISOString() }
  },

  async approveTestRun(jobId: string, promptId: string, testRunId: string): Promise<PromptTestRun> {
    await delay(500)
    return { testRunId, jobId, promptId, status: 'approved', applicationIds: [], createdAt: new Date().toISOString(), completedAt: new Date().toISOString() }
  },
}
