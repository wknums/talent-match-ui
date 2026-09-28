# Customer Implementation Guide: Microsoft Entra ID, Azure RBAC, and Azure SQL

Document date: 22 September 2026

## 1. Purpose

This document defines the identity and access configuration required to deploy
and operate TalentMatch in Azure. It is intended for customer teams that will
manually create or approve:

- Microsoft Entra ID app registrations and enterprise applications.
- Entra security groups and application-role assignments.
- The GitHub Actions deployment identity and federated credentials.
- User-assigned managed identities for the two application stacks.
- Azure role-based access control (RBAC) assignments.
- The Azure SQL Microsoft Entra administrator and database permissions.
- The non-secret identifiers that must be returned to the deployment team for
  the target `.env` file and GitHub environment configuration.

This guide does not require application code changes.

## 2. Scope and Current Deployment Assumptions

The repository deploys two application stacks:

| Stack | Technology | Runtime identity |
| --- | --- | --- |
| Stack A | React and Node.js/Express | One user-assigned managed identity |
| Stack B | Blazor WebAssembly and ASP.NET Core | One user-assigned managed identity |

Both stacks:

- Authenticate workforce users from one Entra tenant.
- Use separate public SPA app registrations.
- Request a delegated scope from one shared protected API registration.
- Validate the same four application roles.
- Connect to the same Azure SQL database with managed identity.
- Can call the AWReason API with managed identity when
  `AWR_AUTH_MODE=entra`.

The active QA profile currently indicates:

- `APP_AUTH_MODE=entra`
- `AWR_AUTH_MODE=entra`
- Existing Azure SQL is reused.
- Automatic SQL bootstrap is disabled.
- The application managed identities are currently configured to be created,
  but can instead be pre-created and reused as described in this guide.
- Entra registrations are currently configured to be created, but the manual
  customer handoff model requires `ENTRA_REUSE=TRUE`.
- VNet integration is enabled.
- Key Vault and API Management are disabled for this environment.

Because the database is reused and SQL bootstrap is disabled, the customer SQL
team must execute the database grants in section 10 from a host that can reach
the private SQL endpoint.

## 3. Required Separation of Duties

Use separate operators where practical. Activate privileged roles through
Privileged Identity Management for the implementation window and remove
temporary access after verification.

| Activity | Minimum practical permission |
| --- | --- |
| Create or update the three app registrations | Owner of the specific applications, Cloud Application Administrator, or Application Administrator |
| Create app roles and expose the API scope | Owner of the protected API application, Cloud Application Administrator, or Application Administrator |
| Grant delegated admin consent to the two SPAs | Cloud Application Administrator or Application Administrator, subject to tenant consent policy |
| Assign users or groups to protected API app roles | Owner of the protected API service principal, Cloud Application Administrator, Application Administrator, or User Administrator |
| Create and manage the application security groups | Owner of the specific groups or Groups Administrator |
| Create user-assigned managed identities | Managed Identity Contributor on the identity resource group, or Contributor on that resource group |
| Assign an existing managed identity to App Service | Managed Identity Operator on the identity plus write access to the App Service |
| Set the Azure SQL Entra administrator | Azure role containing `Microsoft.Sql/servers/administrators/read` and `Microsoft.Sql/servers/administrators/write` at the SQL server scope |
| Create SQL contained users and grant database roles | The configured SQL Entra administrator, or a temporary database principal with equivalent user, schema, and role-management rights |
| Deploy infrastructure into an existing resource group | Contributor on the deployment resource group |
| Create the deployment resource group | Contributor at subscription scope, or a custom role that permits resource-group creation |
| Create or change subnets, private endpoints, and private DNS | Network Contributor on the VNet resource group |
| Read reused resources without modifying them | Reader on each external resource group |
| Deploy application ZIP packages only | Website Contributor on the two target App Services |

Azure RBAC does not grant Entra directory permissions, and Entra directory roles
do not grant Azure resource permissions. Both permission planes must be
configured when the same deployment identity runs the AzureRM and AzureAD
Terraform providers.

Ordinary TalentMatch users require no Azure subscription role, no Azure SQL
login, and no Entra directory role.

## 4. Identity Inventory and Naming

Use environment-specific names. The examples below assume:

- Project: `talentmatch`
- Environment: `test`

| Object | Recommended display name | Required output |
| --- | --- | --- |
| Protected API app registration | `talentmatch-test-api` | Application client ID, application object ID, identifier URI |
| Protected API enterprise application | `talentmatch-test-api` | Service principal object ID |
| Stack A SPA registration | `talentmatch-test-stack-a` | Application client ID |
| Stack B SPA registration | `talentmatch-test-stack-b` | Application client ID |
| Stack A managed identity | `id-talentmatch-node-test` | Resource ID, client ID, principal object ID, name |
| Stack B managed identity | `id-talentmatch-blazor-test` | Resource ID, client ID, principal object ID, name |
| GitHub deployment app registration | `talentmatch-test-github-deploy` | Application client ID and service principal object ID |
| Global Admin group | `talentmatch-test-admin` | Group object ID |
| Organization Admin group | `talentmatch-test-<organization>-organization-admin` | Group object ID |
| Recruiter group | `talentmatch-test-<organization>-<department>-recruiter` | Group object ID |
| Business Panel group | `talentmatch-test-<organization>-business-panel` | Group object ID |

Names are presentation values. All handoffs and automated mappings must use
immutable object IDs and application IDs.

Assign at least two durable customer-controlled owners to every app
registration, enterprise application, and security group.

## 5. Create the Protected API App Registration

In the Microsoft Entra admin center:

1. Go to **Entra ID > App registrations > New registration**.
2. Name the registration `talentmatch-<environment>-api`.
3. Select **Accounts in this organizational directory only**.
4. Do not configure a redirect URI for the protected API.
5. Record:
   - Directory tenant ID.
   - Application client ID.
   - Application object ID.
6. Open **Expose an API**.
7. Set an Application ID URI. The recommended value is:

   ```text
   api://<protected-api-application-client-id>
   ```

   A customer-approved custom URI is also supported, but it must be used
   consistently. The applications construct the requested scope from the exact
   identifier URI and scope value.

8. Add one delegated scope:

   | Field | Required value |
   | --- | --- |
   | Scope name | `access_as_user` |
   | Who can consent | Admins only |
   | Admin consent display name | `Access TalentMatch as a user` |
   | Admin consent description | `Allow the application to access TalentMatch as the signed-in user.` |
   | State | Enabled |

9. The repository default scope ID is
   `a3217e97-d4d2-4baa-b9f8-0e9ac6b05f31`. If the portal generates a different
   scope ID, record it in the handoff register. The runtime `.env` file requires
   the scope value, not the scope ID.
10. Under **Manifest**, confirm
    `requestedAccessTokenVersion` is `2`.
11. Do not create a client secret or certificate for this API registration
    unless a separate, approved confidential-client scenario requires one.

### 5.1 Create the Four App Roles

Under **App roles**, create the following enabled roles. Set **Allowed member
types** to **Users/Groups**.

| Display name | Value required by the application | Description | Repository default role ID |
| --- | --- | --- | --- |
| Admin | `admin` | Full platform administration across all organizations. | `52d6f322-c719-49a5-97a4-b7d78fc60af0` |
| Organization Admin | `organization_admin` | Delegated administration within assigned organizations. | `fea2562e-d6f0-49b6-93a5-e666b51310f6` |
| Recruiter | `recruiter` | Recruiting operations within assigned organization and department scopes. | `edc0d4d0-bf46-4e88-b0e1-a2b465252a0c` |
| Business Panel | `business_panel` | Business panel review within assigned organization and department scopes. | `a98b2c62-70a4-41ab-95f4-960ea96ce06d` |

If the app roles are created through the portal and receive different role IDs,
record those generated IDs and use them in the `.env` file. The role values
must match the table exactly.

Do not delete and recreate roles after production use. The immutable role IDs
are persisted deployment inputs.

### 5.2 Record the Enterprise Application

1. Go to **Entra ID > Enterprise applications > All applications**.
2. Open the enterprise application corresponding to the protected API.
3. Record its **Object ID**. This is the service principal object ID, not the
   application client ID and not the app registration object ID.
4. Under **Properties**, keep **Assignment required?** set to **No** to match
   the current deployment definition.

The application still fails closed. A valid token alone does not grant
TalentMatch access; the user must also have an allowed app-role claim and an
active application assignment in the shared database.

## 6. Create the Two SPA App Registrations

Repeat these steps for:

- `talentmatch-<environment>-stack-a`
- `talentmatch-<environment>-stack-b`

1. Go to **Entra ID > App registrations > New registration**.
2. Select **Accounts in this organizational directory only**.
3. Under **Authentication**, add the **Single-page application** platform.
4. Add the exact HTTPS origin for that stack as the redirect URI, for example:

   ```text
   https://app-talentmatch-node-test.azurewebsites.net/
   https://app-talentmatch-blazor-test.azurewebsites.net/
   ```

5. Add every approved custom-domain origin separately.
6. Use the exact scheme, host, port, path, and trailing-slash form supplied to
   the deployment team.
7. Do not add a Web platform.
8. Do not create a client secret or certificate. SPAs are public clients and
   use authorization code flow with PKCE.
9. Do not enable the implicit grant checkboxes for access tokens or ID tokens.
10. Under **API permissions**, select **Add a permission > My APIs**, select
    the protected API, choose **Delegated permissions**, and add
    `access_as_user`.
11. Grant admin consent for the tenant for the delegated permission.
12. Confirm the permission status is **Granted for \<tenant\>**.
13. Record each SPA's application client ID.

No Microsoft Graph runtime permissions are required by the TalentMatch SPAs.

## 7. Create and Assign Security Groups

Group-based assignment to enterprise applications requires Microsoft Entra ID
P1 or P2. If the tenant does not have the required license, assign users
directly to the protected API roles and retain the same application database
authorization process.

### 7.1 Group Design

Create security-enabled groups dedicated to TalentMatch. Recommended patterns:

| Access | Group pattern | API role |
| --- | --- | --- |
| Global platform administrators | `talentmatch-<env>-admin` | `admin` |
| Administrators for one organization | `talentmatch-<env>-<org>-organization-admin` | `organization_admin` |
| Recruiters for one department | `talentmatch-<env>-<org>-<department>-recruiter` | `recruiter` |
| Business panel for one organization | `talentmatch-<env>-<org>-business-panel` | `business_panel` |

Requirements:

- Use direct user membership.
- Do not rely on nested groups; nested membership does not cascade for
  enterprise-application assignment.
- Assign each group to exactly one TalentMatch app role.
- Create separate groups where organization or department scope differs.
- Do not add the managed identities to user-role groups.
- Record every group object ID, intended role, organization, and department.

### 7.2 Assign Groups to the API Enterprise Application

For each group:

1. Open **Enterprise applications > talentmatch-\<environment\>-api**.
2. Select **Users and groups > Add user/group**.
3. Select the group.
4. Select exactly one of the four app roles.
5. Select **Assign**.
6. Verify the assignment under **Users and groups**.

The Entra assignment supplies the coarse `roles` token claim. It does not by
itself authorize access to an organization or department. The application
administrator must also map the group object ID to the correct business scope
using the repository's role-management procedure.

After initial bootstrap, routine user and scope administration should be
performed through TalentMatch rather than by granting Azure RBAC or SQL access.

## 8. Configure the Bootstrap Administrator

Select one enabled Entra workforce **Member** user. Do not use a guest account.

The current repository safety contract requires this same user to be:

1. The Azure SQL logical server's Microsoft Entra administrator.
2. Directly assigned the protected API `admin` app role.
3. Identified by `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID`.

Record:

- User principal name.
- Entra user object ID.
- Confirmation that the account is enabled.
- Confirmation that `userType` is `Member`.

The deployment currently expects Terraform to manage the bootstrap user's app
role assignment even when `ENTRA_REUSE=TRUE`. If the customer manually creates
the same assignment before deployment, the deployment team must import that
assignment into the shared Terraform state before applying. Do not create the
same assignment independently without agreeing the Terraform-state ownership.

## 9. Create the Runtime Managed Identities

Create two user-assigned managed identities in the target subscription and
tenant:

```text
id-talentmatch-node-<environment>
id-talentmatch-blazor-<environment>
```

For each identity, record:

- Managed identity resource name.
- Resource group.
- Azure resource ID.
- Application client ID.
- Principal object ID.

The client ID and principal object ID are different values:

- The client ID is used by the application credential selection.
- The principal object ID is used for Azure role assignments and Entra
  application-role assignments.
- The resource ID is used to attach the identity to an App Service.

If these identities are manually pre-created, set `AZ_IDENTITIES_REUSE=TRUE`
and provide their names and resource group in the `.env` file.

The infrastructure deployment attaches the identities to their respective App
Services. A separately operated deployment identity needs Managed Identity
Operator on these identities and sufficient write permission on the App
Services.

## 10. Configure Azure SQL Entra Authentication and Permissions

### 10.1 Set the SQL Logical Server Entra Administrator

On the reused Azure SQL logical server:

1. Open the SQL server in the Azure portal.
2. Select **Microsoft Entra ID** or **Microsoft Entra admin**.
3. Select the bootstrap administrator defined in section 8.
4. Save the configuration.
5. Verify the configured administrator object ID exactly matches
   `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID`.

The control-plane operator needs a role containing:

```text
Microsoft.Sql/servers/administrators/read
Microsoft.Sql/servers/administrators/write
```

Scope this permission to the SQL logical server where possible.

### 10.2 Connect from an Approved Bootstrap Host

The active environment uses a private SQL endpoint and has
`AZ_SQL_BOOTSTRAP_ENABLED=FALSE`. Run the following from a VNet-connected host
that:

- Resolves the SQL private DNS name.
- Can connect to TCP 1433.
- Is signed in as the configured SQL Entra administrator.
- Can acquire a token for `https://database.windows.net/.default`.

Do not use the SQL administrator for application runtime connections.

### 10.3 Create the Managed Identity Database Users

Connect to the application database, not `master`, and run the following
one-time pattern using the exact managed identity display names:

```sql
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'talentmatch')
    EXEC(N'CREATE SCHEMA [talentmatch]');

IF NOT EXISTS (
    SELECT 1 FROM sys.database_principals
    WHERE name = N'id-talentmatch-node-test'
)
    CREATE USER [id-talentmatch-node-test] FROM EXTERNAL PROVIDER;

ALTER ROLE [db_datareader] ADD MEMBER [id-talentmatch-node-test];
ALTER ROLE [db_datawriter] ADD MEMBER [id-talentmatch-node-test];
ALTER ROLE [db_ddladmin] ADD MEMBER [id-talentmatch-node-test];

IF NOT EXISTS (
    SELECT 1 FROM sys.database_principals
    WHERE name = N'id-talentmatch-blazor-test'
)
    CREATE USER [id-talentmatch-blazor-test] FROM EXTERNAL PROVIDER;

ALTER ROLE [db_datareader] ADD MEMBER [id-talentmatch-blazor-test];
ALTER ROLE [db_datawriter] ADD MEMBER [id-talentmatch-blazor-test];
ALTER ROLE [db_ddladmin] ADD MEMBER [id-talentmatch-blazor-test];
```

For repeatable execution, use the repository helper. It performs membership
checks before each `ALTER ROLE` and is preferred when Node.js dependencies are
available:

```bash
export SQL_SERVER_FQDN="<sql-server>.database.windows.net"
export SQL_DATABASE_NAME="<application-database>"
export STACK_A_IDENTITY_NAME="id-talentmatch-node-<environment>"
export STACK_B_IDENTITY_NAME="id-talentmatch-blazor-<environment>"

node infra/scripts/bootstrap-sql-entra-users.mjs
```

The required application database roles are:

| Role | Reason |
| --- | --- |
| `db_datareader` | Read application data |
| `db_datawriter` | Insert, update, and delete application data |
| `db_ddladmin` | Create and migrate the `talentmatch` schema at application startup |

Do not grant either runtime managed identity:

- `db_owner`
- SQL server administrator
- Azure SQL Contributor
- Subscription or resource-group Contributor
- A SQL username or password

The temporary bootstrap operator can use the configured Entra administrator or
`db_owner` on this database only. Remove temporary database grants after the
managed identity users have been verified.

### 10.4 Verify SQL Permissions

Run:

```sql
SELECT
    member_principal.name AS principal_name,
    role_principal.name AS database_role
FROM sys.database_role_members AS drm
JOIN sys.database_principals AS role_principal
    ON role_principal.principal_id = drm.role_principal_id
JOIN sys.database_principals AS member_principal
    ON member_principal.principal_id = drm.member_principal_id
WHERE member_principal.name IN (
    N'id-talentmatch-node-test',
    N'id-talentmatch-blazor-test'
)
ORDER BY member_principal.name, role_principal.name;
```

Expected result: three rows per managed identity for `db_datareader`,
`db_datawriter`, and `db_ddladmin`.

If `CREATE USER ... FROM EXTERNAL PROVIDER` cannot resolve the managed
identities, confirm:

- The SQL server and identities belong to the same tenant.
- The managed identity names are correct and unambiguous.
- The operator is the configured Entra administrator.
- The SQL server identity has the Microsoft Graph read permissions required by
  the customer's Azure SQL identity-resolution policy.

For automated service-principal execution of `CREATE USER`, Microsoft
recommends a SQL server identity with the minimum Microsoft Graph permissions
needed to resolve users, groups, and applications. Directory Readers is a
broader fallback and should be approved only when the least-privilege Graph
permissions cannot be used.

## 11. Configure AWReason API Access

This section applies because the active environment uses
`AWR_AUTH_MODE=entra`.

The customer must provide:

- AWReason API application client ID.
- AWReason API identifier URI or token audience.
- Enterprise application object ID.
- Required application-role value, normally `TalentMatch.Access`.

The AWReason app role must allow **Applications** as a member type. Assign the
role directly to:

- Stack A managed identity principal object ID.
- Stack B managed identity principal object ID.

Do not grant this application role through a group. Entra does not emit an app
role claim to a managed identity merely because its service principal is a
member of a group that has the role.

The current Terraform deployment creates these two app-role assignments when
`AWR_API_CLIENT_ID` is populated. If the customer creates them manually first,
coordinate Terraform import before apply to avoid duplicate ownership.

Populate:

```ini
AWR_AUTH_MODE=entra
AWR_AAD_AUDIENCE=api://<awreason-api-client-id>/.default
AWR_API_CLIENT_ID=<awreason-api-application-client-id>
AWR_API_APP_ROLE_VALUE=TalentMatch.Access
```

Use the actual AWReason Application ID URI in
`AWR_AAD_AUDIENCE` if it is not `api://<client-id>`.

## 12. Configure the GitHub Actions Deployment Identity

The GitHub workflow uses OpenID Connect. Do not create a client secret.

### 12.1 Create the Deployment App and Service Principal

1. Create a single-tenant app registration named
   `talentmatch-<environment>-github-deploy`.
2. Ensure the corresponding enterprise application exists.
3. Record:
   - Application client ID.
   - Service principal object ID.
   - Tenant ID.
   - Subscription ID.

This deployment app is separate from the TalentMatch protected API and both
SPA registrations.

### 12.2 Add Federated Identity Credentials

The repository uses GitHub environments named:

- `development`
- `staging`
- `production`

Create one federated credential for each environment that the identity may
deploy:

| Field | Value |
| --- | --- |
| Issuer | `https://token.actions.githubusercontent.com` |
| Audience | `api://AzureADTokenExchange` |
| Subject for development | `repo:wknums/talent-match-ui:environment:development` |
| Subject for staging | `repo:wknums/talent-match-ui:environment:staging` |
| Subject for production | `repo:wknums/talent-match-ui:environment:production` |

Create separate deployment identities per environment if required by customer
policy. This is preferred for production isolation.

### 12.3 Azure RBAC for the Deployment Identity

For the full current Terraform workflow, grant:

| Scope | Role | When required |
| --- | --- | --- |
| Target deployment resource group | Contributor | Create and update App Service, plan, identities, and selected shared resources |
| Subscription | Contributor or approved custom role | Only if Terraform must create the resource group |
| Existing resource groups containing reused resources | Reader | Terraform data-source lookup |
| Existing VNet resource group | Reader | Reuse-only lookup |
| Existing VNet resource group | Network Contributor | Create/configure integration subnet, SQL private endpoint, or private DNS |
| Managed identity resources | Managed Identity Operator | Required when identity assignment is separated from broader Contributor access |
| Target App Services | Website Contributor | Sufficient for code-only ZIP deployment after infrastructure exists |

Do not grant Owner unless the deployment must create Azure RBAC role
assignments. The current Terraform in this repository does not create Azure
RBAC role-assignment resources.

### 12.4 Entra Permissions for the Deployment Identity

When `ENTRA_REUSE=TRUE`, Terraform does not create the three TalentMatch
registrations, but the shared apply still expects to create:

- The bootstrap user's `admin` assignment on the protected API enterprise
  application.
- The two managed identity assignments on the AWReason enterprise application
  when `AWR_AUTH_MODE=entra`.

The noninteractive deployment identity therefore needs customer-approved
Microsoft Graph application permissions for those writes, normally:

- `Application.Read.All`
- `AppRoleAssignment.ReadWrite.All`

Admin consent is required for application permissions.

If Terraform will create app registrations with `ENTRA_REUSE=FALSE`, it also
requires application lifecycle permission, such as
`Application.ReadWrite.OwnedBy` for objects owned by the deployment identity,
or the broader `Application.ReadWrite.All` if approved by customer policy.

If Terraform will create groups, it additionally requires
`Group.ReadWrite.All`. The manual group model in this document uses
`ENTRA_CREATE_ROLE_GROUPS=FALSE`, so that permission is not required.

Where the customer will not grant Graph write permissions to the deployment
identity, the relevant app-role assignments must be created manually and
imported into the shared Terraform state before apply. Manual creation without
state import is not a supported handoff because Terraform will attempt to own
the same assignments.

### 12.5 GitHub Environment Secrets and Variables

Configure these secrets in each applicable GitHub environment:

```text
AZURE_CLIENT_ID=<github-deployment-app-client-id>
AZURE_TENANT_ID=<tenant-id>
AZURE_SUBSCRIPTION_ID=<subscription-id>
```

`AZURE_CLIENT_ID` in GitHub means the GitHub deployment app client ID.
`AZURE_CLIENT_ID` in each running App Service means that stack's user-assigned
managed identity client ID. These values have the same setting name in
different security contexts and must not be confused.

The packaging workflow also reads these non-secret GitHub environment
variables:

```text
APP_AUTH_MODE=entra
ENTRA_STACK_A_CLIENT_ID=<stack-a-spa-client-id>
ENTRA_API_APP_CLIENT_ID=<protected-api-client-id>
ENTRA_API_IDENTIFIER_URI=<protected-api-identifier-uri>
ENTRA_API_SCOPE=access_as_user
```

## 13. Environment File Handoff

The customer identity and SQL teams must return the following values to the
deployment team. The file is non-secret for the fields shown below, but it
still contains controlled environment metadata and should use the customer's
approved configuration-management process.

### 13.1 Recommended Manual-Preprovisioning Template

```ini
# Tenant and subscription
AZURE_TENANT_ID=<directory-tenant-id>
AZURE_SUBSCRIPTION_ID=<subscription-id>

# Application authentication
APP_AUTH_MODE=entra

# Customer pre-created Entra registrations
ENTRA_REUSE=TRUE
ENTRA_CREATE_ROLE_GROUPS=FALSE
ENTRA_API_APP_CLIENT_ID=<protected-api-application-client-id>
ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID=<protected-api-enterprise-app-object-id>
ENTRA_API_IDENTIFIER_URI=api://<protected-api-application-client-id>
ENTRA_API_SCOPE=access_as_user

# Exact comma-separated SPA redirect URIs
ENTRA_STACK_A_REDIRECT_URIS=https://<stack-a-host>/
ENTRA_STACK_B_REDIRECT_URIS=https://<stack-b-host>/

# Public SPA client IDs
ENTRA_STACK_A_CLIENT_ID=<stack-a-spa-application-client-id>
ENTRA_STACK_B_CLIENT_ID=<stack-b-spa-application-client-id>

# App-role IDs from the protected API registration
ENTRA_ADMIN_APP_ROLE_ID=<admin-app-role-id>
ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID=<organization-admin-app-role-id>
ENTRA_RECRUITER_APP_ROLE_ID=<recruiter-app-role-id>
ENTRA_BUSINESS_PANEL_APP_ROLE_ID=<business-panel-app-role-id>

# First administrator
ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID=<enabled-member-user-object-id>
ENTRA_BOOTSTRAP_ORGANIZATION_NAME=<initial-organization-name>
ENTRA_BOOTSTRAP_DEPARTMENT_NAME=<initial-department-name>

# Customer pre-created runtime managed identities
AZ_IDENTITIES_REUSE=TRUE
AZ_IDENTITY_STACK_A_NAME=id-talentmatch-node-<environment>
AZ_IDENTITY_STACK_B_NAME=id-talentmatch-blazor-<environment>
AZ_IDENTITIES_RG=<managed-identity-resource-group>

# Reused Azure SQL
AZ_SQL_REUSE=TRUE
AZ_SQL_BOOTSTRAP_ENABLED=FALSE
SQL_SERVER_NAME=<logical-sql-server-name>
SQL_DATABASE_NAME=<application-database-name>
SQL_RG=<sql-resource-group>
SQL_AAD_ADMIN_LOGIN=<bootstrap-admin-user-principal-name>
SQL_AAD_ADMIN_OBJECT_ID=<same-value-as-ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID>

# AWReason managed-identity authentication
AWR_AUTH_MODE=entra
AWR_AAD_AUDIENCE=api://<awreason-api-client-id>/.default
AWR_API_CLIENT_ID=<awreason-api-application-client-id>
AWR_API_APP_ROLE_VALUE=TalentMatch.Access
```

If the protected API uses a custom identifier URI, replace both
`ENTRA_API_IDENTIFIER_URI` and the corresponding scope configuration with that
exact URI.

If the identities remain Terraform-created, keep
`AZ_IDENTITIES_REUSE=FALSE` and omit the three `AZ_IDENTITY_*`/`AZ_IDENTITIES_RG`
reuse coordinates.

### 13.2 Values Produced but Not Stored Directly in the `.env` File

Retain these values in the customer handoff register:

| Output | Use |
| --- | --- |
| Protected API app registration object ID | Application ownership and audit |
| Protected API delegated scope ID | Permission audit and API configuration verification |
| Stack A and Stack B app registration object IDs | Application ownership and audit |
| Stack A and Stack B enterprise application object IDs | Consent and enterprise-app administration |
| Security group object IDs | TalentMatch group-to-business-scope mappings |
| Managed identity resource IDs | Attach identities to App Services |
| Managed identity client IDs | Per-stack App Service `AZURE_CLIENT_ID` setting |
| Managed identity principal object IDs | AWReason app-role and Azure RBAC assignments |
| GitHub deployment service principal object ID | Azure RBAC assignments |

Terraform obtains and wires the managed identity client IDs to the App Services.
The resulting runtime settings are:

```text
AZURE_SQL_AUTH_MODE=entra
AZURE_SQL_SERVER_FQDN=<sql-server>.database.windows.net
AZURE_SQL_DATABASE_NAME=<application-database>
AZURE_CLIENT_ID=<that-stack-managed-identity-client-id>
```

Do not place managed identity principal object IDs in `AZURE_CLIENT_ID`.

## 14. Required Implementation Order

Use this order to avoid circular dependencies and partial authorization:

1. Confirm tenant, subscription, environment, names, URLs, and responsible
   operators.
2. Create the protected API registration, delegated scope, and four app roles.
3. Create both SPA registrations, add redirect URIs, request the API delegated
   scope, and grant admin consent.
4. Create the application security groups and assign them to protected API
   roles.
5. Select the bootstrap administrator and configure it as the SQL Entra
   administrator.
6. Create the two runtime user-assigned managed identities, or authorize
   Terraform to create them.
7. Create the GitHub deployment app, federated credentials, Azure RBAC, and
   required Graph application permissions.
8. Populate the `.env` and GitHub environment settings.
9. Run a reviewed shared Terraform plan.
10. Apply shared infrastructure so the App Services and identity attachments
    exist.
11. Assign both runtime managed identities to the AWReason API application
    role, either through Terraform or through the agreed manual-and-import
    process.
12. From the approved VNet-connected host, create the two Azure SQL contained
    users and grant the three database roles.
13. Run the bootstrap administrator check/apply/check workflow.
14. Deploy both application packages.
15. Test sign-in, token claims, database connectivity, AWReason token
    acquisition, and access revocation.
16. Remove temporary privileged access and retain the approved handoff record.

## 15. Acceptance Checklist

### Entra Applications

- [ ] Protected API is single-tenant.
- [ ] API access-token version is 2.
- [ ] API identifier URI exactly matches `ENTRA_API_IDENTIFIER_URI`.
- [ ] Enabled delegated scope value is exactly `access_as_user`.
- [ ] Four enabled app roles have the exact required values.
- [ ] Protected API enterprise application object ID is recorded.
- [ ] Stack A and Stack B are SPA/public-client registrations.
- [ ] SPA redirect URIs exactly match the deployed origins.
- [ ] No SPA client secret or certificate exists.
- [ ] Both SPAs have the delegated API permission.
- [ ] Admin consent is granted for both SPAs.
- [ ] At least two durable owners exist on each application.

### Groups and Application Roles

- [ ] Security groups are security-enabled and application-specific.
- [ ] Membership is direct, not nested.
- [ ] Each group has exactly one protected API app-role assignment.
- [ ] Group object IDs and business scopes are recorded.
- [ ] Bootstrap user is directly assigned the `admin` role.
- [ ] Group/user role assignments and Terraform ownership do not conflict.

### Managed Identities and AWReason

- [ ] Both user-assigned managed identities exist in the target tenant.
- [ ] Resource IDs, client IDs, and principal object IDs are recorded
  separately.
- [ ] Each identity is attached only to its intended App Service.
- [ ] Both identities have a direct `TalentMatch.Access` assignment on the
  AWReason enterprise application.
- [ ] Neither identity has a client secret.

### Azure SQL

- [ ] SQL Entra administrator is the enabled Member bootstrap user.
- [ ] SQL Entra administrator object ID equals
  `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID`.
- [ ] Bootstrap host resolves and reaches the private SQL endpoint.
- [ ] Both managed identities exist as contained users in the application
  database.
- [ ] Both users are members of `db_datareader`, `db_datawriter`, and
  `db_ddladmin`.
- [ ] Neither runtime identity is `db_owner`.
- [ ] Both applications can connect without a SQL password.

### Deployment Identity and RBAC

- [ ] GitHub deployment app is separate from runtime applications.
- [ ] OIDC federated credential issuer, audience, and subjects are exact.
- [ ] No deployment client secret is used.
- [ ] GitHub environment secrets contain the deployment app client ID, tenant
  ID, and subscription ID.
- [ ] Azure RBAC is scoped to required resource groups/resources.
- [ ] Network Contributor is granted only where network changes are required.
- [ ] Required Graph application permissions have admin consent.
- [ ] Temporary directory and Azure privileges are removed after handoff.

### Environment Handoff

- [ ] `ENTRA_REUSE=TRUE` is set for manually created registrations.
- [ ] All four mandatory reused-registration IDs are populated.
- [ ] All four app-role IDs match the protected API manifest.
- [ ] Both redirect URI lists are populated.
- [ ] Managed identity reuse coordinates match the created identities.
- [ ] Reused SQL coordinates are correct.
- [ ] AWReason audience, client ID, and role value are correct.
- [ ] No passwords, tokens, refresh tokens, or SPA secrets are present.

## 16. Security Notes

- Use managed identities and OIDC instead of client secrets.
- Treat redirect URIs as exact security boundaries.
- Grant application permissions and admin consent only after review.
- Prefer object ownership or resource-scoped roles over standing tenant-wide
  administrator roles.
- Use PIM and time-bound assignments for elevated implementation tasks.
- Do not grant application users Azure RBAC or direct SQL access.
- Do not infer authorization from Entra group names. Use immutable group object
  IDs and explicit application mappings.
- Do not assume an Entra role claim alone grants application access. TalentMatch
  also requires active organization and department authorization in the shared
  database.
- Store no client secrets for the SPAs, managed identities, or GitHub OIDC
  deployment.

## 17. Repository References

- [Entra authorization operations](./ENTRA_AUTHORIZATION.md)
- [Authentication behavior](../AUTHENTICATION.md)
- [Infrastructure deployment runbook](../infra/README.md)
- [Azure SQL managed identity bootstrap helper](../infra/scripts/bootstrap-sql-entra-users.mjs)
- [Entra Terraform module](../infra/terraform/modules/foundation/entra/main.tf)
- [Managed identity Terraform module](../infra/terraform/modules/foundation/identities/main.tf)
- [Azure SQL Terraform module](../infra/terraform/modules/foundation/sql/main.tf)
- [GitHub deployment workflow](../.github/workflows/selective-azure-deploy.yml)

## 18. Microsoft References

- [Add app roles to an application and receive them in the token](https://learn.microsoft.com/entra/identity-platform/howto-add-app-roles-in-apps)
- [Manage users and groups assignment to an application](https://learn.microsoft.com/entra/identity/enterprise-apps/assign-user-or-group-access-portal)
- [Single-page application registration and configuration](https://learn.microsoft.com/entra/identity-platform/scenario-spa-app-registration)
- [Deploy to Azure App Service by using GitHub Actions](https://learn.microsoft.com/azure/app-service/deploy-github-actions)
- [Connect GitHub and Azure with OpenID Connect](https://learn.microsoft.com/azure/developer/github/connect-from-azure)
- [Microsoft Entra service principals with Azure SQL](https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-service-principal)
- [Managed identities for Azure resources](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/overview)
- [Azure built-in roles](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles)
