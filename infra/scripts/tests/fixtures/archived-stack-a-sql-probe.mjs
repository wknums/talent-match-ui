import assert from 'node:assert/strict'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

const [directory, operation] = process.argv.slice(2)
assert.ok(directory, 'The verified extracted Stack A directory is required.')
assert.ok(['seed', 'verify', 'restart'].includes(operation), 'Unknown compatibility operation.')
assert.ok(process.env.AZURE_SQL_CONNECTION_STRING, 'The disposable SQL connection is required.')

const load = path => import(pathToFileURL(resolve(directory, 'server', ...path.split('/'))).href)
const database = await load('storage/db.js')
assert.equal(database.isAzureSql, true, 'Never fall back to SQLite for SQL Server compatibility.')
const pool = await database.getPool()
try {
  const { userRepo } = await load('storage/repos/user-repo.js')
  const { roleAssignmentRepo } = await load('storage/repos/role-assignment-repo.js')
  const { jobRepo } = await load('storage/repos/job-repo.js')
  const { applicationRepo } = await load('storage/repos/application-repo.js')
  const tenant = '10000000-0000-4000-8000-000000000000'
  const administrator = '10000000-0000-4000-8000-000000000001'

  if (operation === 'seed') {
    await database.initializeDatabase()
    await pool.request().query(`
      INSERT INTO talentmatch.Organizations (Id,Name,UpdatedBy) VALUES ('compat-org','Compatibility','fixture');
      INSERT INTO talentmatch.Departments (Id,OrganizationId,Name,UpdatedBy)
        VALUES ('compat-dept','compat-org','Compatibility','fixture');
      INSERT INTO talentmatch.Jobs (Id,Title,Department,PostingDate,OrganizationId,DepartmentId)
        VALUES ('compat-job','Retained job','Compatibility',SYSUTCDATETIME(),'compat-org','compat-dept');
      INSERT INTO talentmatch.JobConfigVersions (Id,JobId,RunsPerApplication)
        VALUES ('compat-config','compat-job',5);
      UPDATE talentmatch.Jobs SET CurrentConfigVersionId='compat-config' WHERE Id='compat-job';
      INSERT INTO talentmatch.ProcessingEvents (Id,Actor,Action,EntityType,EntityId,DetailsJson,CorrelationId)
        VALUES ('compat-audit','fixture','compatibility.seed','Job','compat-job','{}','compat-seed');
    `)
    for (const [userId, objectId, isActive] of [
      ['compat-admin', administrator, true],
      ['compat-disabled', '10000000-0000-4000-8000-000000000002', false],
    ]) {
      await userRepo.create({
        userId, username: `${userId}@example.test`, fullName: userId, role: 'recruiter',
        authenticationProvider: 'entra', entraTenantId: tenant, entraObjectId: objectId,
        isActive, authorizationVersion: 7, createdAt: new Date().toISOString(),
      })
    }
    await roleAssignmentRepo.activate({
      assignmentId: 'compat-bootstrap', userId: 'compat-admin', tenantId: tenant,
      userObjectId: administrator, role: 'admin', source: 'bootstrap', updatedBy: 'fixture',
    })
    await applicationRepo.create({
      applicationId: 'compat-application', jobId: 'compat-job', candidateRef: 'retained-candidate',
      candidateName: 'Original candidate', status: 'Completed', createdAt: new Date().toISOString(),
    })
    await applicationRepo.updateStatus('compat-application', 'Completed', {
      finalScore: 81, finalDecision: 'Eligible', variance: 2,
    })
  } else if (operation === 'restart') {
    await database.initializeDatabase()
  }

  const admin = await userRepo.getByEntraIdentity(tenant, administrator)
  assert.equal(admin.userId, 'compat-admin')
  assert.equal(admin.isActive, true)
  assert.equal(admin.authorizationVersion, 7)
  assert.equal((await userRepo.getById('compat-disabled')).isActive, false)
  const grants = await roleAssignmentRepo.getActiveForIdentity(tenant, administrator)
  assert.equal(grants.length, 1)
  assert.equal(grants[0].assignmentId, 'compat-bootstrap')
  assert.equal(grants[0].role, 'admin')
  assert.equal(grants[0].source, 'bootstrap')
  assert.equal((await jobRepo.getById('compat-job')).title, 'Retained job')
  const application = await applicationRepo.getById('compat-application')
  assert.equal(application.finalScore, 81)
  assert.equal(application.status, 'Completed')
  assert.equal(application.candidateName, operation === 'seed' ? 'Original candidate' : 'Updated by Stack B')

  const id = `compat-crud-${operation}`
  await applicationRepo.create({
    applicationId: id, jobId: 'compat-job', candidateRef: id, status: 'Queued', createdAt: new Date().toISOString(),
  })
  await applicationRepo.updateStatus(id, 'Completed', { finalScore: 72, finalDecision: 'Eligible' })
  assert.equal((await applicationRepo.getById(id)).finalScore, 72)
  await applicationRepo.delete(id)
  assert.equal(await applicationRepo.getById(id), undefined)
  console.log(`COMPATIBILITY_OK:${operation}`)
} finally {
  await pool.close()
}
