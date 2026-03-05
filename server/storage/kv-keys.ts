// KV store key constants
export const AUTH_USERS = 'auth:users';
export const AUTH_CURRENT_USER = 'auth:current-user';
export const AUTH_RESET_REQUESTS = 'auth:reset-requests';
export const JOBS = 'jobs';
export const DLQ = 'dlq';
export const LEDGER = 'ledger';
export const SYSTEM_STATS = 'system:stats';

// Pattern helpers for dynamic keys
export function jobVersionsKey(jobId: string): string {
  return `job:${jobId}:versions`;
}

export function jobApplicationsKey(jobId: string): string {
  return `job:${jobId}:applications`;
}

export function appDocumentsKey(applicationId: string): string {
  return `app:${applicationId}:documents`;
}

export function appExtractionKey(applicationId: string): string {
  return `app:${applicationId}:extraction`;
}

export function appRunsKey(applicationId: string): string {
  return `app:${applicationId}:runs`;
}

export function appResultKey(applicationId: string): string {
  return `app:${applicationId}:result`;
}

export function appManualReviewKey(applicationId: string): string {
  return `app:${applicationId}:manual-review`;
}
