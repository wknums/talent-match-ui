import { beforeEach, describe, expect, it, vi } from 'vitest'

const queries = vi.hoisted(() => [] as string[])
const state = vi.hoisted(() => ({ allowDuplicates: false }))
const stop = new Error('stop-after-boundary')

vi.mock('../../server/storage/db.js', () => {
  class Request {
    input() {
      return this
    }

    async query(text: string) {
      queries.push(text)
      if (text.includes('FROM [talentmatch].[UploadSessions] s WITH (UPDLOCK, HOLDLOCK)')) {
        return {
          recordset: [{
            Id: 'session-1',
            JobId: 'job-1',
            OwnerActorId: 'owner-1',
            AllowDuplicates: state.allowDuplicates ? 1 : 0,
            CorrelationId: 'correlation-1',
          }],
        }
      }
      if (text.includes('FROM [talentmatch].[UploadItems] i WITH (UPDLOCK, HOLDLOCK)')) {
        return {
          recordset: [{
            Id: 'item-1',
            SessionId: 'session-1',
            OccurrenceKey: '11111111-1111-4111-8111-111111111111',
            Status: 'uploading',
            AttemptCount: 1,
            RawSizeBytes: 2,
            ConcurrencyVersion: 1,
          }],
        }
      }
      throw stop
    }
  }

  const transaction = {
    begin: vi.fn(),
    commit: vi.fn(),
    rollback: vi.fn(),
    request: () => new Request(),
  }
  return {
    isAzureSql: true,
    sql: {
      NVarChar: 'NVarChar',
      Int: 'Int',
      BigInt: 'BigInt',
      Bit: 'Bit',
      DateTime2: 'DateTime2',
    },
    getPool: async () => ({ transaction: () => transaction }),
  }
})

import { uploadRepo } from '../../server/storage/repos/upload-repo.js'

const completion = {
  sessionId: 'session-1',
  itemId: 'item-1',
  ownerActorId: 'owner-1',
  occurrenceKey: '11111111-1111-4111-8111-111111111111',
  fingerprint: 'a'.repeat(64),
  fileName: 'candidate.pdf',
  mimeType: 'application/pdf',
  rawSizeBytes: 2,
  contentBase64: 'Y3Y=',
  now: new Date().toISOString(),
}

describe('upload repository provider locking', () => {
  beforeEach(() => {
    queries.length = 0
  })

  it('serializes duplicate-disabled decisions on the Azure SQL job row', async () => {
    state.allowDuplicates = false

    await expect(uploadRepo.completeItem(completion)).rejects.toBe(stop)

    expect(queries).toContainEqual(expect.stringContaining(
      'FROM [talentmatch].[Jobs] WITH (UPDLOCK, HOLDLOCK)',
    ))
  })

  it('does not serialize intentional duplicate-enabled completions on the duplicate boundary', async () => {
    state.allowDuplicates = true

    await expect(uploadRepo.completeItem(completion)).rejects.toBe(stop)

    expect(queries).not.toContainEqual(expect.stringContaining(
      'FROM [talentmatch].[Jobs] WITH (UPDLOCK, HOLDLOCK)',
    ))
  })
})
