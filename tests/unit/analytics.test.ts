import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Application, Job } from '../../src/types/index.js'
import type { StoredUser } from '../../server/storage/repos/user-repo.js'

const repoMocks = vi.hoisted(() => ({
  userRepo: { getAll: vi.fn() },
  jobRepo: { getAll: vi.fn() },
  applicationRepo: { getByJobId: vi.fn(), getAggregatedResult: vi.fn() },
}))

vi.mock('../../server/storage/repos/index.js', () => repoMocks)

import { computeDepartmentAnalytics, computeRecruiterAnalytics } from '../../server/services/analytics.js'

function makeUser(overrides: Partial<StoredUser> = {}): StoredUser {
  return {
    userId: overrides.userId ?? 'user-1',
    username: overrides.username ?? 'alice',
    role: overrides.role ?? 'recruiter',
    department: overrides.department ?? 'Engineering',
    fullName: overrides.fullName ?? 'Alice Recruiter',
    email: overrides.email ?? 'alice@example.com',
    createdAt: overrides.createdAt ?? '2026-01-01T00:00:00Z',
    lastLogin: overrides.lastLogin,
    passwordHash: overrides.passwordHash ?? 'hash',
    passwordResetRequired: overrides.passwordResetRequired ?? false,
  }
}

function makeJob(overrides: Partial<Job> = {}): Job {
  const jobId = overrides.jobId ?? 'job-1'

  return {
    jobId,
    jobCode: overrides.jobCode ?? 'JOB-001',
    title: overrides.title ?? 'Software Engineer',
    department: overrides.department ?? 'Engineering',
    organization: overrides.organization ?? 'Acme',
    postingDate: overrides.postingDate ?? '2026-01-01T00:00:00Z',
    createdBy: overrides.createdBy ?? 'user-1',
    createdAt: overrides.createdAt ?? '2026-01-01T00:00:00Z',
    status: overrides.status ?? 'Active',
    currentVersion: overrides.currentVersion ?? {
      versionId: 'version-1',
      jobId,
      rubric: [],
      mustHaves: [],
      desiredCriteria: [],
      runsPerApplication: 3,
      aggregationStrategy: 'median',
      longlistThreshold: 70,
      shortlistThreshold: 85,
      varianceThreshold: 15,
      rubricApprovalStatus: 'draft',
      rubricSource: 'manual',
      createdAt: '2026-01-01T00:00:00Z',
    },
  }
}

function makeApplication(overrides: Partial<Application> = {}): Application {
  return {
    applicationId: overrides.applicationId ?? 'app-1',
    jobId: overrides.jobId ?? 'job-1',
    candidateRef: overrides.candidateRef ?? 'cand-1',
    candidateName: overrides.candidateName,
    candidateEmail: overrides.candidateEmail,
    status: overrides.status ?? 'Queued',
    createdAt: overrides.createdAt ?? '2026-01-01T00:00:00Z',
    documents: overrides.documents ?? [],
    extractionArtifactId: overrides.extractionArtifactId,
    finalScore: overrides.finalScore,
    finalDecision: overrides.finalDecision,
    variance: overrides.variance,
    flagged: overrides.flagged,
    testRunId: overrides.testRunId,
    lastError: overrides.lastError,
  }
}

describe('analytics service', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    repoMocks.applicationRepo.getAggregatedResult.mockResolvedValue(undefined)
  })

  it('matches jobs created by recruiter username', async () => {
    repoMocks.userRepo.getAll.mockResolvedValue([
      makeUser({ userId: 'user-1', username: 'alice' }),
    ])
    repoMocks.jobRepo.getAll.mockResolvedValue([
      makeJob({ jobId: 'job-1', createdBy: 'alice', status: 'Active' }),
    ])
    repoMocks.applicationRepo.getByJobId.mockResolvedValue([
      makeApplication({ applicationId: 'app-1', jobId: 'job-1', status: 'Queued' }),
    ])

    const result = await computeRecruiterAnalytics()

    expect(result).toHaveLength(1)
    expect(result[0]).toMatchObject({
      recruiterId: 'user-1',
      applicationsInQueue: 1,
      activeJobs: 1,
    })
  })

  it('assigns blank createdBy jobs to the only recruiter in the department', async () => {
    repoMocks.userRepo.getAll.mockResolvedValue([
      makeUser({ userId: 'eng-1', username: 'alice', department: 'Engineering' }),
      makeUser({ userId: 'prod-1', username: 'bob', department: 'Product' }),
    ])
    repoMocks.jobRepo.getAll.mockResolvedValue([
      makeJob({ jobId: 'job-1', createdBy: '', department: 'Engineering', status: 'Processing' }),
      makeJob({ jobId: 'job-2', createdBy: '', department: 'Product', status: 'Closed' }),
    ])
    repoMocks.applicationRepo.getByJobId.mockImplementation(async (jobId: string) => {
      if (jobId === 'job-1') {
        return [makeApplication({ applicationId: 'app-1', jobId, status: 'Queued' })]
      }

      return [makeApplication({ applicationId: 'app-2', jobId, finalDecision: 'Eligible', status: 'Completed' })]
    })

    const result = await computeRecruiterAnalytics()

    expect(result.find((item) => item.recruiterId === 'eng-1')).toMatchObject({
      applicationsInQueue: 1,
      activeJobs: 1,
    })
    expect(result.find((item) => item.recruiterId === 'prod-1')).toMatchObject({
      shortlistRecommendations: 1,
      activeJobs: 0,
    })
  })

  it('does not guess blank createdBy jobs when multiple recruiters share a department', async () => {
    repoMocks.userRepo.getAll.mockResolvedValue([
      makeUser({ userId: 'eng-1', username: 'alice', department: 'Engineering' }),
      makeUser({ userId: 'eng-2', username: 'betty', department: 'Engineering' }),
    ])
    repoMocks.jobRepo.getAll.mockResolvedValue([
      makeJob({ jobId: 'job-1', createdBy: '', department: 'Engineering', status: 'Active' }),
    ])
    repoMocks.applicationRepo.getByJobId.mockResolvedValue([
      makeApplication({ applicationId: 'app-1', jobId: 'job-1', status: 'Queued' }),
    ])

    const result = await computeRecruiterAnalytics()

    expect(result).toHaveLength(2)
    expect(result.every((item) => item.activeJobs === 0 && item.applicationsInQueue === 0)).toBe(true)
  })

  it('excludes prompt test run applications from recruiter metrics', async () => {
    repoMocks.userRepo.getAll.mockResolvedValue([
      makeUser({ userId: 'user-1', username: 'alice' }),
    ])
    repoMocks.jobRepo.getAll.mockResolvedValue([
      makeJob({ jobId: 'job-1', createdBy: 'user-1' }),
    ])
    repoMocks.applicationRepo.getByJobId.mockResolvedValue([
      makeApplication({ applicationId: 'app-1', jobId: 'job-1', status: 'Queued' }),
      makeApplication({ applicationId: 'app-2', jobId: 'job-1', status: 'Queued', testRunId: 'test-run-1' }),
    ])

    const result = await computeRecruiterAnalytics()

    expect(result[0].applicationsInQueue).toBe(1)
  })

  it('groups recruiter metrics by department', () => {
    const result = computeDepartmentAnalytics([
      {
        recruiterId: 'user-1',
        recruiterName: 'Alice',
        department: 'Engineering',
        applicationsInQueue: 3,
        manualReviewsPerformed: 1,
        shortlistRecommendations: 2,
        averageProcessingTime: 1.5,
        activeJobs: 1,
      },
      {
        recruiterId: 'user-2',
        recruiterName: 'Bob',
        department: 'Engineering',
        applicationsInQueue: 4,
        manualReviewsPerformed: 2,
        shortlistRecommendations: 1,
        averageProcessingTime: undefined,
        activeJobs: 2,
      },
    ])

    expect(result).toEqual([
      {
        department: 'Engineering',
        totalRecruiters: 2,
        applicationsInQueue: 7,
        manualReviewsPerformed: 3,
        shortlistRecommendations: 3,
        activeJobs: 3,
        recruiters: expect.any(Array),
      },
    ])
  })
})
