import { describe, it, expect } from 'vitest';
import { computeRecruiterAnalytics, computeDepartmentAnalytics } from '../../server/routes/stats.js';
import type { StorageProvider } from '../../server/storage/types.js';
import type { Job, Application, AggregatedResult } from '../../src/types/index.js';

// Helper to create a mock StorageProvider
function createMockStorage(data: Record<string, unknown>): StorageProvider {
  return {
    get: async <T>(key: string) => (data[key] as T) ?? undefined,
    set: async () => {},
    delete: async () => {},
    keys: async () => Object.keys(data),
    initialize: async () => {},
  };
}

function makeUser(overrides: Partial<{ userId: string; username: string; role: string; department: string; fullName: string }> = {}) {
  return {
    userId: overrides.userId ?? 'user-1',
    username: overrides.username ?? 'jdoe',
    role: overrides.role ?? 'recruiter',
    department: overrides.department ?? 'Engineering',
    fullName: overrides.fullName ?? 'Jane Doe',
    createdAt: '2026-01-01T00:00:00Z',
    passwordHash: 'abc123',
  };
}

function makeJob(overrides: Partial<Job> = {}): Job {
  return {
    jobId: overrides.jobId ?? 'job-1',
    jobCode: 'JC-001',
    title: 'Software Engineer',
    department: 'Engineering',
    organization: 'Acme',
    postingDate: '2026-01-01',
    createdBy: overrides.createdBy ?? 'user-1',
    createdAt: '2026-01-01T00:00:00Z',
    status: overrides.status ?? 'Active',
    currentVersion: { versionId: 'v1', jobId: 'job-1', rubricJson: '', mustHaveCriteriaJson: '', desiredCriteriaJson: '', scoringRunCount: 3, aggregationStrategy: 'average', longlistThreshold: 0.5, shortlistThreshold: 0.75, varianceThreshold: 0.1, createdAt: '2026-01-01T00:00:00Z', createdBy: 'admin' },
  };
}

function makeApp(overrides: Partial<Application> = {}): Application {
  return {
    applicationId: overrides.applicationId ?? 'app-1',
    jobId: overrides.jobId ?? 'job-1',
    candidateRef: 'CAND-001',
    status: overrides.status ?? 'Queued',
    createdAt: overrides.createdAt ?? '2026-01-01T00:00:00Z',
    documents: [],
    finalDecision: overrides.finalDecision,
    flagged: overrides.flagged,
  };
}

describe('Analytics Computation', () => {
  describe('computeRecruiterAnalytics', () => {
    it('counts applicationsInQueue for Queued status', async () => {
      const storage = createMockStorage({
        'auth:users': [makeUser()],
        'jobs': [makeJob()],
        'job:job-1:applications': [
          makeApp({ applicationId: 'a1', status: 'Queued' }),
          makeApp({ applicationId: 'a2', status: 'Queued' }),
          makeApp({ applicationId: 'a3', status: 'Completed' }),
        ],
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result).toHaveLength(1);
      expect(result[0].applicationsInQueue).toBe(2);
    });

    it('counts manualReviewsPerformed for NeedsManualReview or flagged', async () => {
      const storage = createMockStorage({
        'auth:users': [makeUser()],
        'jobs': [makeJob()],
        'job:job-1:applications': [
          makeApp({ applicationId: 'a1', status: 'NeedsManualReview' }),
          makeApp({ applicationId: 'a2', status: 'Completed', flagged: true }),
          makeApp({ applicationId: 'a3', status: 'Queued' }),
        ],
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result[0].manualReviewsPerformed).toBe(2);
    });

    it('counts shortlistRecommendations for Eligible decisions', async () => {
      const storage = createMockStorage({
        'auth:users': [makeUser()],
        'jobs': [makeJob()],
        'job:job-1:applications': [
          makeApp({ applicationId: 'a1', finalDecision: 'Eligible' }),
          makeApp({ applicationId: 'a2', finalDecision: 'Excluded' }),
          makeApp({ applicationId: 'a3', finalDecision: 'Eligible' }),
        ],
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result[0].shortlistRecommendations).toBe(2);
    });

    it('counts activeJobs for Active and Processing status', async () => {
      const storage = createMockStorage({
        'auth:users': [makeUser()],
        'jobs': [
          makeJob({ jobId: 'j1', status: 'Active' }),
          makeJob({ jobId: 'j2', status: 'Processing' }),
          makeJob({ jobId: 'j3', status: 'Closed' }),
        ],
        'job:j1:applications': [],
        'job:j2:applications': [],
        'job:j3:applications': [],
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result[0].activeJobs).toBe(2);
    });

    it('computes averageProcessingTime from timestamps', async () => {
      const storage = createMockStorage({
        'auth:users': [makeUser()],
        'jobs': [makeJob()],
        'job:job-1:applications': [
          makeApp({ applicationId: 'a1', createdAt: '2026-01-01T00:00:00Z' }),
          makeApp({ applicationId: 'a2', createdAt: '2026-01-01T00:00:00Z' }),
        ],
        'app:a1:result': { resultId: 'r1', applicationId: 'a1', createdAt: '2026-01-01T02:00:00Z' } as AggregatedResult,
        'app:a2:result': { resultId: 'r2', applicationId: 'a2', createdAt: '2026-01-01T04:00:00Z' } as AggregatedResult,
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result[0].averageProcessingTime).toBe(3); // (2+4)/2 = 3 hours
    });

    it('returns undefined averageProcessingTime when no completions', async () => {
      const storage = createMockStorage({
        'auth:users': [makeUser()],
        'jobs': [makeJob()],
        'job:job-1:applications': [
          makeApp({ applicationId: 'a1', status: 'Queued' }),
        ],
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result[0].averageProcessingTime).toBeUndefined();
    });

    it('excludes users without a department', async () => {
      const storage = createMockStorage({
        'auth:users': [
          makeUser({ userId: 'u1', department: 'Engineering' }),
          { ...makeUser({ userId: 'u2' }), department: undefined },
        ],
        'jobs': [],
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result).toHaveLength(1);
      expect(result[0].recruiterId).toBe('u1');
    });

    it('excludes business_panel users', async () => {
      const storage = createMockStorage({
        'auth:users': [
          makeUser({ userId: 'u1', role: 'recruiter' }),
          makeUser({ userId: 'u2', role: 'business_panel' }),
        ],
        'jobs': [],
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result).toHaveLength(1);
      expect(result[0].recruiterId).toBe('u1');
    });

    it('returns empty array when no jobs or users exist', async () => {
      const storage = createMockStorage({
        'auth:users': [],
        'jobs': [],
      });

      const result = await computeRecruiterAnalytics(storage);
      expect(result).toEqual([]);
    });
  });

  describe('computeDepartmentAnalytics', () => {
    it('groups recruiters by department and sums metrics', () => {
      const recruiters = [
        { recruiterId: 'u1', recruiterName: 'Alice', department: 'Engineering', applicationsInQueue: 10, manualReviewsPerformed: 5, shortlistRecommendations: 3, activeJobs: 2, averageProcessingTime: 1.5 },
        { recruiterId: 'u2', recruiterName: 'Bob', department: 'Engineering', applicationsInQueue: 8, manualReviewsPerformed: 3, shortlistRecommendations: 2, activeJobs: 1 },
        { recruiterId: 'u3', recruiterName: 'Carol', department: 'Product', applicationsInQueue: 5, manualReviewsPerformed: 2, shortlistRecommendations: 1, activeJobs: 1 },
      ];

      const result = computeDepartmentAnalytics(recruiters);
      expect(result).toHaveLength(2);

      const eng = result.find(d => d.department === 'Engineering')!;
      expect(eng.totalRecruiters).toBe(2);
      expect(eng.applicationsInQueue).toBe(18);
      expect(eng.manualReviewsPerformed).toBe(8);
      expect(eng.shortlistRecommendations).toBe(5);
      expect(eng.activeJobs).toBe(3);
      expect(eng.recruiters).toHaveLength(2);

      const prod = result.find(d => d.department === 'Product')!;
      expect(prod.totalRecruiters).toBe(1);
      expect(prod.applicationsInQueue).toBe(5);
    });

    it('returns empty array for empty input', () => {
      const result = computeDepartmentAnalytics([]);
      expect(result).toEqual([]);
    });
  });
});
