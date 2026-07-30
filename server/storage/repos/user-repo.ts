import { getPool, sql } from '../db.js'
import { T } from '../table-names.js'

export interface StoredUser {
  userId: string
  username: string
  role: 'admin' | 'organization_admin' | 'recruiter' | 'business_panel'
  authenticationProvider?: 'simple' | 'entra'
  entraTenantId?: string
  entraObjectId?: string
  isActive?: boolean
  department?: string
  fullName: string
  email?: string
  createdAt: string
  lastLogin?: string
  passwordHash?: string
  passwordResetRequired?: boolean
}

export interface PasswordResetRequestRow {
  requestId: string
  userId: string
  username: string
  fullName: string
  requestedAt: string
  status: 'pending' | 'completed' | 'rejected'
  resolvedAt?: string
  resolvedBy?: string
}

function rowToUser(r: any): StoredUser {
  return {
    userId: r.Id,
    username: r.Username,
    role: r.Role,
    authenticationProvider: r.AuthenticationProvider ?? 'simple',
    entraTenantId: r.EntraTenantId ?? undefined,
    entraObjectId: r.EntraObjectId ?? undefined,
    isActive: r.IsActive === undefined ? true : r.IsActive === true || r.IsActive === 1,
    department: r.Department || undefined,
    fullName: r.FullName,
    email: r.Email || undefined,
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    lastLogin: r.LastLogin?.toISOString?.() ?? r.LastLogin ?? undefined,
    passwordHash: r.PasswordHash ?? undefined,
    passwordResetRequired: r.PasswordResetRequired === true || r.PasswordResetRequired === 1,
  }
}

function rowToResetRequest(r: any): PasswordResetRequestRow {
  return {
    requestId: r.Id,
    userId: r.UserId,
    username: r.Username,
    fullName: r.FullName ?? '',
    requestedAt: r.RequestedAt?.toISOString?.() ?? r.RequestedAt ?? r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    status: r.Status,
    resolvedAt: r.ResolvedAt?.toISOString?.() ?? r.ResolvedAt ?? undefined,
    resolvedBy: r.ResolvedBy ?? undefined,
  }
}

export const userRepo = {
  async getAll(): Promise<StoredUser[]> {
    const pool = await getPool()
    const result = await pool.request().query(`SELECT * FROM ${T('Users')} ORDER BY CreatedAt`)
    return result.recordset.map(rowToUser)
  },

  async getByUsername(username: string): Promise<StoredUser | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('username', sql.NVarChar, username)
      .query(`SELECT * FROM ${T('Users')} WHERE Username = @username`)
    return result.recordset[0] ? rowToUser(result.recordset[0]) : undefined
  },

  async getById(userId: string): Promise<StoredUser | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, userId)
      .query(`SELECT * FROM ${T('Users')} WHERE Id = @id`)
    return result.recordset[0] ? rowToUser(result.recordset[0]) : undefined
  },

  async getByEntraIdentity(tenantId: string, objectId: string): Promise<StoredUser | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('tenantId', sql.NVarChar, tenantId)
      .input('objectId', sql.NVarChar, objectId)
      .query(`SELECT * FROM ${T('Users')} WHERE AuthenticationProvider = 'entra' AND EntraTenantId = @tenantId AND EntraObjectId = @objectId`)
    return result.recordset[0] ? rowToUser(result.recordset[0]) : undefined
  },

  async create(user: StoredUser): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, user.userId)
      .input('username', sql.NVarChar, user.username)
      .input('role', sql.NVarChar, user.role)
      .input('fullName', sql.NVarChar, user.fullName)
      .input('email', sql.NVarChar, user.email || '')
      .input('department', sql.NVarChar, user.department || '')
      .input('passwordHash', sql.NVarChar, user.passwordHash ?? null)
      .input('createdAt', sql.DateTime2, new Date(user.createdAt))
      .input('passwordResetRequired', sql.Bit, user.passwordResetRequired ? 1 : 0)
      .input('authenticationProvider', sql.NVarChar, user.authenticationProvider ?? 'simple')
      .input('entraTenantId', sql.NVarChar, user.entraTenantId ?? null)
      .input('entraObjectId', sql.NVarChar, user.entraObjectId ?? null)
      .input('isActive', sql.Bit, user.isActive === false ? 0 : 1)
      .query(`INSERT INTO ${T('Users')} (Id, Username, Role, FullName, Email, Department, PasswordHash, CreatedAt, PasswordResetRequired, AuthenticationProvider, EntraTenantId, EntraObjectId, IsActive)
        VALUES (@id, @username, @role, @fullName, @email, @department, @passwordHash, @createdAt, @passwordResetRequired, @authenticationProvider, @entraTenantId, @entraObjectId, @isActive)`)
  },

  async upsertEntraProfile(user: Required<Pick<StoredUser, 'userId' | 'username' | 'fullName' | 'createdAt'>> & Pick<StoredUser, 'email' | 'role' | 'entraTenantId' | 'entraObjectId'>): Promise<StoredUser> {
    if (!user.entraTenantId || !user.entraObjectId) throw new Error('Entra tenant and object IDs are required.')
    const existing = await this.getByEntraIdentity(user.entraTenantId, user.entraObjectId)
    if (existing) {
      await this.update(existing.userId, { fullName: user.fullName, email: user.email })
      return { ...existing, username: user.username, fullName: user.fullName, email: user.email }
    }
    const created: StoredUser = { ...user, role: user.role ?? 'recruiter', authenticationProvider: 'entra', passwordHash: undefined, passwordResetRequired: false, isActive: true }
    await this.create(created)
    return created
  },

  async update(userId: string, fields: Partial<Pick<StoredUser, 'fullName' | 'email' | 'role' | 'department' | 'passwordHash' | 'lastLogin' | 'passwordResetRequired' | 'isActive'>>): Promise<void> {
    const pool = await getPool()
    const sets: string[] = []
    const req = pool.request().input('id', sql.NVarChar, userId)
    if (fields.fullName !== undefined) { sets.push('FullName = @fullName'); req.input('fullName', sql.NVarChar, fields.fullName) }
    if (fields.email !== undefined) { sets.push('Email = @email'); req.input('email', sql.NVarChar, fields.email) }
    if (fields.role !== undefined) { sets.push('Role = @role'); req.input('role', sql.NVarChar, fields.role) }
    if (fields.department !== undefined) { sets.push('Department = @department'); req.input('department', sql.NVarChar, fields.department) }
    if (fields.passwordHash !== undefined) { sets.push('PasswordHash = @passwordHash'); req.input('passwordHash', sql.NVarChar, fields.passwordHash) }
    if (fields.lastLogin !== undefined) { sets.push('LastLogin = @lastLogin'); req.input('lastLogin', sql.DateTime2, new Date(fields.lastLogin)) }
    if (fields.passwordResetRequired !== undefined) { sets.push('PasswordResetRequired = @pwr'); req.input('pwr', sql.Bit, fields.passwordResetRequired ? 1 : 0) }
    if (fields.isActive !== undefined) { sets.push('IsActive = @isActive'); req.input('isActive', sql.Bit, fields.isActive ? 1 : 0) }
    if (sets.length === 0) return
    await req.query(`UPDATE ${T('Users')} SET ${sets.join(', ')} WHERE Id = @id`)
  },

  async delete(userId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, userId)
      .query(`DELETE FROM ${T('Users')} WHERE Id = @id`)
  },

  async count(): Promise<number> {
    const pool = await getPool()
    const result = await pool.request().query(`SELECT COUNT(*) AS cnt FROM ${T('Users')}`)
    return result.recordset[0].cnt
  },

  // Password reset requests
  async getResetRequests(statusFilter?: string): Promise<PasswordResetRequestRow[]> {
    const pool = await getPool()
    let query = `SELECT * FROM ${T('PasswordResetRequests')}`
    const req = pool.request()
    if (statusFilter) {
      query += ' WHERE Status = @status'
      req.input('status', sql.NVarChar, statusFilter)
    }
    query += ' ORDER BY RequestedAt DESC'
    const result = await req.query(query)
    return result.recordset.map(rowToResetRequest)
  },

  async createResetRequest(request: PasswordResetRequestRow): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, request.requestId)
      .input('userId', sql.NVarChar, request.userId)
      .input('username', sql.NVarChar, request.username)
      .input('fullName', sql.NVarChar, request.fullName)
            .input('reason', sql.NVarChar, request.fullName)
      .input('status', sql.NVarChar, request.status)
      .input('requestedAt', sql.DateTime2, new Date(request.requestedAt))
            .query(`INSERT INTO ${T('PasswordResetRequests')} (Id, UserId, Username, FullName, Reason, Status, RequestedAt, CreatedAt)
              VALUES (@id, @userId, @username, @fullName, @reason, @status, @requestedAt, @requestedAt)`)
  },

  async updateResetRequest(requestId: string, fields: { status: string; resolvedAt?: string; resolvedBy?: string }): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, requestId)
      .input('status', sql.NVarChar, fields.status)
      .input('resolvedAt', sql.DateTime2, fields.resolvedAt ? new Date(fields.resolvedAt) : null)
      .input('resolvedBy', sql.NVarChar, fields.resolvedBy || null)
      .query(`UPDATE ${T('PasswordResetRequests')} SET Status = @status, ResolvedAt = @resolvedAt, ResolvedBy = @resolvedBy WHERE Id = @id`)
  },
}
