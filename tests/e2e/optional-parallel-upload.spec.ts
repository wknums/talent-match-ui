import { expect, test } from '@playwright/test'

test.describe('optional parallel upload browser lifetime', () => {
  test('continues after dialog close and navigation without duplicating the session', async ({ page }) => {
    let createCalls = 0
    const sessionId = '11111111-1111-4111-8111-111111111111'
    const now = new Date().toISOString()
    const user = {
      userId: 'user-1',
      username: 'recruiter',
      role: 'recruiter',
      authenticationProvider: 'simple',
      isActive: true,
      department: 'Engineering',
      fullName: 'Test Recruiter',
      createdAt: now,
      passwordResetRequired: false,
    }
    const job = {
      jobId: 'job-1',
      jobCode: 'TEST-1',
      title: 'Test Engineer',
      department: 'Engineering',
      organization: 'Test Organization',
      postingDate: now,
      createdBy: user.userId,
      createdAt: now,
      status: 'Active',
      currentVersion: {
        versionId: 'version-1',
        jobId: 'job-1',
        rubric: [],
        scoringRules: [],
        createdAt: now,
        createdBy: user.userId,
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

    await page.route('**/api/config', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ apiMode: 'real' }),
    }))
    await page.route('**/api/auth/me', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(user),
    }))
    await page.route('**/api/jobs', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([job]),
    }))
    await page.route('**/api/stats', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        totalJobs: 1,
        activeJobs: 1,
        totalApplications: 0,
        queuedApplications: 0,
        processingApplications: 0,
        completedApplications: 0,
        failedApplications: 0,
        averageThroughputPerHour: 0,
      }),
    }))
    await page.route('**/api/upload-sessions?**', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: '[]',
    }))
    await page.route('**/api/jobs/*/upload-sessions', async route => {
      createCalls += 1
      await route.fulfill({
        status: 201,
        contentType: 'application/json',
        body: JSON.stringify({
          id: sessionId,
          jobId: 'job-1',
          status: 'active',
          allowDuplicates: false,
          limits: { fileConcurrency: 1, maxIndividualFileBytes: 4194304, maxInFlightBytes: 4194304 },
          counts: { total: 1, waitingOrThrottled: 1, activeOrRetrying: 0, succeeded: 0, skipped: 0, failed: 0, interrupted: 0, terminal: 0 },
          progressPercent: 0,
          correlationId: 'correlation-1',
          createdAt: new Date().toISOString(),
          startedAt: null,
          lastHeartbeatAt: new Date().toISOString(),
          completedAt: null,
          concurrencyVersion: 1,
          items: [{
            id: 'item-1',
            sessionId,
            occurrenceKey: '22222222-2222-4222-8222-222222222222',
            ordinal: 0,
            fileName: 'candidate.pdf',
            mimeType: 'application/pdf',
            rawSizeBytes: 2,
            status: 'waiting',
            attemptCount: 0,
            contentFingerprint: null,
            applicationId: null,
            outcomeCode: null,
            outcomeMessage: null,
            nextRetryAt: null,
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            completedAt: null,
            concurrencyVersion: 1,
          }],
        }),
      })
    })
    await page.route('**/api/upload-sessions/*/items/*/content', async route => {
      await new Promise(resolve => setTimeout(resolve, 500))
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          id: 'item-1',
          sessionId,
          occurrenceKey: '22222222-2222-4222-8222-222222222222',
          ordinal: 0,
          fileName: 'candidate.pdf',
          mimeType: 'application/pdf',
          rawSizeBytes: 2,
          status: 'succeeded',
          attemptCount: 1,
          contentFingerprint: 'a'.repeat(64),
          applicationId: 'application-1',
          outcomeCode: null,
          outcomeMessage: null,
          nextRetryAt: null,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          completedAt: new Date().toISOString(),
          concurrencyVersion: 2,
        }),
      })
    })

    await page.goto('http://127.0.0.1:4173/')
    await expect(page.getByText('Talent Matching Platform')).toBeVisible()
    // The authenticated fixture opens a job upload dialog through the existing dashboard action.
    await page.getByRole('button', { name: /upload apps/i }).first().click()
    await page.getByRole('checkbox', { name: /allow parallel individual uploads/i }).click()
    await page.getByLabel(/drop files here/i).setInputFiles({
      name: 'candidate.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.from('cv'),
    })
    await page.getByRole('button', { name: 'Upload 1 file(s)' }).click()
    await expect(page.getByText(/optional uploads/i)).toBeVisible()
    await page.getByRole('button', { name: /view analytics/i }).click()
    await expect(page.getByText(/optional uploads/i)).toBeVisible()
    expect(createCalls).toBe(1)
  })
})
