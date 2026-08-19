import { applicationRepo, jobRepo, organizationRepo, roleAssignmentRepo, userRepo } from '../storage/repos/index.js'
import type { StoredUser } from '../storage/repos/user-repo.js'
import type { DepartmentAnalytics, Job, RecruiterAnalytics } from '../../src/types/index.js'
import type { ServerAuthorizationContext } from './authorization.js'

function normalizeKey(value?: string | null): string {
  return value?.trim().toLowerCase() ?? ''
}

function getAnalyticsUsers(users: StoredUser[]): StoredUser[] {
  return users.filter((user) => (
    (user.role === 'recruiter' || user.role === 'admin')
    && Boolean(user.department?.trim())
  ))
}

function buildDepartmentCandidates(users: StoredUser[]): Map<string, StoredUser[]> {
  const candidates = new Map<string, StoredUser[]>()

  for (const user of users) {
    const departmentKey = normalizeKey(user.department)
    if (!departmentKey) continue

    const existing = candidates.get(departmentKey) ?? []
    existing.push(user)
    candidates.set(departmentKey, existing)
  }

  return candidates
}

function resolveRecruiterIdForJob(
  job: Pick<Job, 'createdBy' | 'department'>,
  usersById: Map<string, StoredUser>,
  usersByUsername: Map<string, StoredUser>,
  departmentCandidates: Map<string, StoredUser[]>,
): string | undefined {
  const createdBy = job.createdBy?.trim()

  if (createdBy) {
    const exactIdMatch = usersById.get(createdBy)
    if (exactIdMatch) {
      return exactIdMatch.userId
    }

    const usernameMatch = usersByUsername.get(normalizeKey(createdBy))
    if (usernameMatch) {
      return usernameMatch.userId
    }
  }

  const departmentKey = normalizeKey(job.department)
  const candidates = departmentCandidates.get(departmentKey) ?? []

  if (candidates.length === 1) {
    return candidates[0].userId
  }

  return undefined
}

export async function computeRecruiterAnalytics(context?: ServerAuthorizationContext): Promise<RecruiterAnalytics[]> {
  if (context) return computeEntraRecruiterAnalytics(context)

  const users = getAnalyticsUsers(await userRepo.getAll())
  const jobs = await jobRepo.getAll()

  const usersById = new Map(users.map((user) => [user.userId, user]))
  const usersByUsername = new Map(users.map((user) => [normalizeKey(user.username), user]))
  const departmentCandidates = buildDepartmentCandidates(users)

  const metrics = new Map<string, {
    applicationsInQueue: number
    manualReviewsPerformed: number
    shortlistRecommendations: number
    activeJobs: number
    totalProcessingTime: number
    completedCount: number
  }>()

  for (const user of users) {
    metrics.set(user.userId, {
      applicationsInQueue: 0,
      manualReviewsPerformed: 0,
      shortlistRecommendations: 0,
      activeJobs: 0,
      totalProcessingTime: 0,
      completedCount: 0,
    })
  }

  for (const job of jobs) {
    const recruiterId = resolveRecruiterIdForJob(job, usersById, usersByUsername, departmentCandidates)
    if (!recruiterId) continue

    const recruiterMetrics = metrics.get(recruiterId)
    if (!recruiterMetrics) continue

    if (job.status === 'Active' || job.status === 'Processing') {
      recruiterMetrics.activeJobs++
    }

    const applications = await applicationRepo.getByJobId(job.jobId)
    for (const application of applications) {
      if (application.testRunId) continue

      if (application.status === 'Queued') {
        recruiterMetrics.applicationsInQueue++
      }

      if (application.status === 'NeedsManualReview' || application.flagged === true) {
        recruiterMetrics.manualReviewsPerformed++
      }

      if (application.finalDecision === 'Eligible') {
        recruiterMetrics.shortlistRecommendations++
      }

      const result = await applicationRepo.getAggregatedResult(application.applicationId)
      if (result?.createdAt && application.createdAt) {
        const start = new Date(application.createdAt).getTime()
        const end = new Date(result.createdAt).getTime()
        if (!isNaN(start) && !isNaN(end) && end > start) {
          recruiterMetrics.totalProcessingTime += (end - start) / (1000 * 60 * 60)
          recruiterMetrics.completedCount++
        }
      }
    }
  }

  return users.map((user) => {
    const recruiterMetrics = metrics.get(user.userId)!

    return {
      recruiterId: user.userId,
      recruiterName: user.fullName,
      department: user.department!,
      applicationsInQueue: recruiterMetrics.applicationsInQueue,
      manualReviewsPerformed: recruiterMetrics.manualReviewsPerformed,
      shortlistRecommendations: recruiterMetrics.shortlistRecommendations,
      averageProcessingTime: recruiterMetrics.completedCount > 0
        ? Math.round((recruiterMetrics.totalProcessingTime / recruiterMetrics.completedCount) * 100) / 100
        : undefined,
      activeJobs: recruiterMetrics.activeJobs,
    }
  })
}

export function computeDepartmentAnalytics(recruiterData: RecruiterAnalytics[]): DepartmentAnalytics[] {
  const departmentMap = new Map<string, RecruiterAnalytics[]>()

  for (const recruiter of recruiterData) {
    const existing = departmentMap.get(recruiter.department) ?? []
    existing.push(recruiter)
    departmentMap.set(recruiter.department, existing)
  }

  return Array.from(departmentMap.entries(), ([department, recruiters]) => ({
    department,
    totalRecruiters: recruiters.length,
    applicationsInQueue: recruiters.reduce((sum, recruiter) => sum + recruiter.applicationsInQueue, 0),
    manualReviewsPerformed: recruiters.reduce((sum, recruiter) => sum + recruiter.manualReviewsPerformed, 0),
    shortlistRecommendations: recruiters.reduce((sum, recruiter) => sum + recruiter.shortlistRecommendations, 0),
    activeJobs: recruiters.reduce((sum, recruiter) => sum + recruiter.activeJobs, 0),
    recruiters,
  }))
}

async function computeEntraRecruiterAnalytics(context: ServerAuthorizationContext): Promise<RecruiterAnalytics[]> {
  const allAssignments = await roleAssignmentRepo.getActiveByRole(context.tenantId, 'recruiter')
  const visibleAssignments = context.globalRole === 'admin'
    ? allAssignments
    : allAssignments.filter((assignment) => assignment.userId === context.userId)
  const assignments = [...new Map(visibleAssignments.map((assignment) => [
    `${assignment.userId}:${assignment.organizationId}:${assignment.departmentId}`,
    assignment,
  ])).values()]
  const users = await userRepo.getAll()
  const usersById = new Map(users.filter((user) => user.isActive !== false).map((user) => [user.userId, user]))
  const jobs = await jobRepo.getAll()
  const results: RecruiterAnalytics[] = []

  for (const assignment of assignments) {
    if (!assignment.organizationId || !assignment.departmentId) continue

    const user = usersById.get(assignment.userId)
    if (!user) continue

    const assignedDepartment = await organizationRepo.getDepartment(assignment.organizationId, assignment.departmentId)
    if (!assignedDepartment || assignedDepartment.status !== 'active') continue

    const scopedJobs = jobs.filter((job) => (
      (job.createdBy === assignment.userId || normalizeKey(job.createdBy) === normalizeKey(user.username))
      && job.organizationId === assignment.organizationId
      && job.departmentId === assignment.departmentId
    ))
    results.push(await calculateRecruiterAnalytics(user, assignedDepartment.name, scopedJobs))
  }

  return results
}

async function calculateRecruiterAnalytics(
  user: StoredUser,
  department: string,
  jobs: Job[],
): Promise<RecruiterAnalytics> {
  let applicationsInQueue = 0
  let manualReviewsPerformed = 0
  let shortlistRecommendations = 0
  let activeJobs = 0
  let totalProcessingTime = 0
  let completedCount = 0

  for (const job of jobs) {
    if (job.status === 'Active' || job.status === 'Processing') activeJobs++

    const applications = await applicationRepo.getByJobId(job.jobId)
    for (const application of applications) {
      if (application.testRunId) continue
      if (application.status === 'Queued') applicationsInQueue++
      if (application.status === 'NeedsManualReview' || application.flagged === true) manualReviewsPerformed++
      if (application.finalDecision === 'Eligible') shortlistRecommendations++

      const result = await applicationRepo.getAggregatedResult(application.applicationId)
      if (result?.createdAt && application.createdAt) {
        const start = new Date(application.createdAt).getTime()
        const end = new Date(result.createdAt).getTime()
        if (!isNaN(start) && !isNaN(end) && end > start) {
          totalProcessingTime += (end - start) / (1000 * 60 * 60)
          completedCount++
        }
      }
    }
  }

  return {
    recruiterId: user.userId,
    recruiterName: user.fullName,
    department,
    applicationsInQueue,
    manualReviewsPerformed,
    shortlistRecommendations,
    averageProcessingTime: completedCount > 0
      ? Math.round((totalProcessingTime / completedCount) * 100) / 100
      : undefined,
    activeJobs,
  }
}