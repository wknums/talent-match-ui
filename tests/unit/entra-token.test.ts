import {
  SignJWT,
  createLocalJWKSet,
  exportJWK,
  generateKeyPair,
  type KeyLike,
  type JWTPayload,
} from 'jose'
import { beforeAll, describe, expect, it } from 'vitest'
import {
  EntraTokenError,
  createEntraTokenValidator,
} from '../../server/services/entra-token.js'

const tenantId = '11111111-1111-4111-8111-111111111111'
const objectId = '22222222-2222-4222-8222-222222222222'
const stackAClientId = '33333333-3333-4333-8333-333333333333'
const stackBClientId = '44444444-4444-4444-8444-444444444444'
const audience = '55555555-5555-4555-8555-555555555555'
const groupId = '66666666-6666-4666-8666-666666666666'
const issuer = `https://login.microsoftonline.com/${tenantId}/v2.0`
const now = new Date('2026-07-25T12:00:00.000Z')
const nowSeconds = Math.floor(now.getTime() / 1000)

let privateKey: KeyLike
let otherPrivateKey: KeyLike
let validator: ReturnType<typeof createEntraTokenValidator>

async function issueToken(
  overrides: Partial<JWTPayload> = {},
  signingKey: KeyLike = privateKey,
): Promise<string> {
  const payload: JWTPayload = {
    ver: '2.0',
    tid: tenantId,
    oid: objectId,
    azp: stackAClientId,
    scp: 'openid access_as_user profile',
    roles: ['recruiter'],
    groups: [groupId],
    preferred_username: 'recruiter@example.com',
    name: 'Example Recruiter',
    ...overrides,
  }

  return new SignJWT(payload)
    .setProtectedHeader({ alg: 'RS256', kid: 'test-key' })
    .setIssuer(typeof payload.iss === 'string' ? payload.iss : issuer)
    .setAudience(typeof payload.aud === 'string' ? payload.aud : audience)
    .setIssuedAt(typeof payload.iat === 'number' ? payload.iat : nowSeconds - 60)
    .setNotBefore(typeof payload.nbf === 'number' ? payload.nbf : nowSeconds - 60)
    .setExpirationTime(typeof payload.exp === 'number' ? payload.exp : nowSeconds + 300)
    .sign(signingKey)
}

async function expectTokenError(token: string, code: string): Promise<void> {
  await expect(validator.validate(token)).rejects.toMatchObject<Partial<EntraTokenError>>({
    name: 'EntraTokenError',
    code,
  })
}

beforeAll(async () => {
  const primary = await generateKeyPair('RS256')
  const secondary = await generateKeyPair('RS256')
  privateKey = primary.privateKey
  otherPrivateKey = secondary.privateKey

  const publicJwk = await exportJWK(primary.publicKey)
  publicJwk.kid = 'test-key'
  publicJwk.alg = 'RS256'
  publicJwk.use = 'sig'

  validator = createEntraTokenValidator(
    {
      tenantId,
      audience,
      authorizedClientIds: [stackAClientId, stackBClientId],
      requiredScope: 'access_as_user',
    },
    {
      keyResolver: createLocalJWKSet({ keys: [publicJwk] }),
      now: () => now,
    },
  )
})

describe('Entra access-token validation', () => {
  it('validates signature and normalizes supported claims', async () => {
    const claims = await validator.validate(await issueToken())

    expect(claims).toMatchObject({
      tenantId,
      objectId,
      authorizedClientId: stackAClientId,
      scopes: ['openid', 'access_as_user', 'profile'],
      roles: ['recruiter'],
      groups: [groupId],
      username: 'recruiter@example.com',
      fullName: 'Example Recruiter',
    })
    expect(claims.issuedAt).toEqual(new Date((nowSeconds - 60) * 1000))
  })

  it('rejects a token signed by an unknown key', async () => {
    await expectTokenError(await issueToken({}, otherPrivateKey), 'invalid_token')
  })

  it('rejects the wrong issuer', async () => {
    await expectTokenError(
      await issueToken({ iss: 'https://login.microsoftonline.com/common/v2.0' }),
      'invalid_token',
    )
  })

  it('rejects the wrong tenant', async () => {
    await expectTokenError(
      await issueToken({ tid: '77777777-7777-4777-8777-777777777777' }),
      'wrong_tenant',
    )
  })

  it('rejects the wrong audience', async () => {
    await expectTokenError(
      await issueToken({ aud: '88888888-8888-4888-8888-888888888888' }),
      'invalid_audience',
    )
  })

  it('rejects an unauthorized azp client', async () => {
    await expectTokenError(
      await issueToken({ azp: '99999999-9999-4999-8999-999999999999' }),
      'unauthorized_client',
    )
  })

  it('requires the delegated API scope', async () => {
    await expectTokenError(await issueToken({ scp: 'openid profile' }), 'invalid_token')
  })

  it('enforces nbf and exp lifetime validation', async () => {
    await expectTokenError(
      await issueToken({ nbf: nowSeconds + 120, exp: nowSeconds + 300 }),
      'invalid_token',
    )
    await expectTokenError(
      await issueToken({ nbf: nowSeconds - 600, exp: nowSeconds - 120 }),
      'invalid_token',
    )
  })

  it('rejects tokens older than the 15-minute freshness window', async () => {
    await expectTokenError(
      await issueToken({ iat: nowSeconds - (15 * 60) - 31 }),
      'token_stale',
    )
  })

  it('allows only supported application roles', async () => {
    const supported = await validator.validate(
      await issueToken({ roles: ['admin', 'organization_admin', 'recruiter', 'business_panel'] }),
    )
    expect(supported.roles).toEqual(['admin', 'organization_admin', 'recruiter', 'business_panel'])

    await expectTokenError(await issueToken({ roles: ['Global Administrator'] }), 'role_conflict')
  })

  it('normalizes direct group IDs and rejects group overage without Graph fallback', async () => {
    const claims = await validator.validate(await issueToken({ groups: [groupId] }))
    expect(claims.groups).toEqual([groupId])

    await expectTokenError(
      await issueToken({
        groups: undefined,
        _claim_names: { groups: 'src1' },
        _claim_sources: { src1: { endpoint: 'https://graph.microsoft.com' } },
      }),
      'scope_unmapped',
    )
  })
})