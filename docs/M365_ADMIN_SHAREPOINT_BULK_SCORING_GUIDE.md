# Microsoft 365 Administrator Guide: SharePoint Folder Bulk Scoring

**Document date:** 21 October 2026  
**Audience:** Microsoft 365, Microsoft Entra, and SharePoint Online administrators  
**Deployment assumption:** The person running Terraform has no Microsoft 365 or
Entra tenant-administrator role. Tenant approval and consent are performed
separately by authorized administrators.

## 1. Purpose and current availability

This guide explains what TalentMatch reads from SharePoint, which permissions
its Microsoft 365 connector needs, and the manual administrator actions required
before recruiters can import a folder for bulk scoring. The same connector
supports OneDrive for Business folders.

It applies to **Stack B: ASP.NET Core and Blazor**, in Microsoft commercial
cloud. Complete tenant setup and pilot validation, but obtain the release team's
approval before production rollout.

Product sign-in, TalentMatch role assignments, Azure SQL, and Azure deployment
permissions are separate prerequisites covered by the
[manual identity and RBAC implementation guide](./MANUAL_ENTRA_ID_RBAC_IMPLEMENTATION_GUIDE.md).
This document does not ask the Terraform operator to become a tenant
administrator or the recruiter to receive an administrator role.

## 2. How TalentMatch uses SharePoint

1. A recruiter signs in to TalentMatch and opens a job they are authorized to
   change.
2. They supply a SharePoint or OneDrive for Business **folder link** and start a
   separate Microsoft 365 connection.
3. Microsoft Entra authenticates the selected work or school account. The
   connector uses authorization code flow with PKCE; consent must already exist
   or be permitted by the source tenant.
4. The server uses Microsoft Graph to resolve the exact folder and check that
   the connected account can access it. It discovers accessible files,
   including descendants, for the recruiter to review before confirmation.
5. Once confirmed, a background worker downloads eligible documents into
   TalentMatch-controlled storage and submits them to the configured scoring
   pipeline. Supported conversion paths may retain both original and converted
   documents. The recruiter need not keep the browser open.
6. Background Graph calls continue with that user's delegated authorization,
   using an encrypted connection/request-bound token cache. Managed identity
   authenticates the confidential connector; it is **not** a substitute
   SharePoint reader.

TalentMatch does not write, rename, move, or delete source documents. This is an
import-and-score workflow, not continuous synchronization with SharePoint.
Personal Microsoft accounts, consumer OneDrive, and sovereign-cloud deployments
are not supported by this connector.

### Permission boundary administrators must understand

The current workflow checks and binds a request to the confirmed folder, but
**the OAuth consent itself is not folder-scoped**. Its delegated scopes allow
the connector to read content the connected user can access, potentially across
multiple sites and drives. Folder binding is enforced by TalentMatch, not by
an Entra selected-folder permission.

Consent does not make the user a site member or override SharePoint access
controls. Conversely, the folder link alone does not grant the connector access.
There is no application-only reader, `Sites.Selected` grant, deployment-time
site/host allow-list, product-token on-behalf-of exchange, or fallback account.
If your organization requires an OAuth grant technically limited to selected
sites or folders, the current connector does not meet that requirement. Do not
replace its scopes with selected-site permissions and assume it will work.

### Data leaves the Microsoft 365 source

Read-only access still permits **copying candidate documents out of SharePoint**.
The data owner must approve TalentMatch storage, the configured scoring service,
data residency, access controls, and retention before consent is used.
Do not assume SharePoint permissions, retention labels, sensitivity labels,
or subsequent source deletion automatically govern or remove imported copies.
Already imported documents can continue through conversion and scoring after
source authorization lapses. Product-side cleanup and job deletion are separate
from revoking Microsoft 365 access.

## 3. Who performs each action

The **hosting tenant** owns the connector registration and Azure runtime managed
identity. A **source tenant** owns the SharePoint or OneDrive content. They can
be the same tenant; if different, each source tenant approves the same connector
client ID in its own directory.

| Responsible party | Required action and authority |
| --- | --- |
| Hosting-tenant Entra application administrator | Create/review the connector registration, Web callbacks, publisher details, owners, and federated credentials. Use Application Administrator, Cloud Application Administrator, or appropriately scoped application ownership/custom permissions. |
| Source-tenant consent administrator | Review and grant delegated tenant-wide consent where required. Cloud Application Administrator or Application Administrator can consent to these delegated Graph permissions; an application owner or SharePoint Administrator alone does not necessarily have consent authority. |
| SharePoint administrator or site/folder owner | Give the intended source account appropriate folder/file access and check sharing, download, and protection policies. Folder ownership is not required. |
| Azure deployment team | Create/attach the user-assigned managed identity, provide its principal ID, configure Key Vault/networking, deploy the app, and manage Terraform state. Azure RBAC does not supply Entra permissions. |
| TalentMatch administrator | Give the recruiter an active product assignment and the required organization/department/job access. This is separate from connector consent. |
| Recruiter | Connect an authorized work or school account and confirm the folder import. No Azure subscription role, directory role, or direct SQL access is required. |

Use the least privileged approved role and time-bound elevation where available.
Do not use an administrator account as the routine source account for scoring.

## 4. Permissions requested by the connector

Create a **separate confidential Web application** for the Microsoft 365 source.
Do not add these permissions to the TalentMatch SPA or protected API registration.

### Microsoft Graph delegated permissions

| Permission | Type | Purpose | Public delegated permission ID |
| --- | --- | --- | --- |
| `Files.Read.All` | Delegated | Read files the connected user can access, including folder discovery and document downloads. | `df85f4d6-205c-4ac5-a5ea-6bf408dba283` |
| `Sites.Read.All` | Delegated | Read SharePoint documents and list items on behalf of the connected user, subject to that user's access. | `205e70e5-aba6-4c52-a976-6d2d46c48043` |

The runtime also requests `openid profile offline_access` for sign-in context
and delegated background continuation. These are OAuth/OpenID Connect scopes,
not Microsoft Graph **application** permissions. `offline_access` does not
provide an unlimited or policy-exempt session.

Microsoft's [permissions reference](https://learn.microsoft.com/graph/permissions-reference)
currently lists both delegated read permissions as **not inherently requiring
admin consent**. Tenant user-consent policy, app trust/publisher restrictions,
and other controls may nevertheless require administrator approval. This guide
uses administrator pre-consent for a controlled rollout; it does not require
loosening the tenant's user-consent policy.

Do **not** grant this connector Graph application permissions, write permissions,
directory-read/write permissions, or a site-specific application grant.
Do not assign Graph app roles to the runtime managed identity. Permissions a
Terraform deployment identity may need to manage Entra objects are a different
matter and must not be added to the runtime connector.

## 5. Agree the Terraform and administrator handoff first

Before running Terraform, agree who owns each Entra object and its lifecycle.
**Reusing an app registration does not make the current Terraform apply
directory-read-only.** With the connector enabled, Terraform still manages a
federated credential for each selected runtime managed identity. The wider
shared deployment can also manage product bootstrap and AWReason app-role
assignments.

Choose an approved operating model:

| Model | Administrator actions | Terraform operator boundary |
| --- | --- | --- |
| Scoped delegation, where permitted | Pre-create/review the connector; grant consent yourself. Grant the actual Terraform identity only the reviewed application ownership/write and lookup permissions it needs. | No tenant-admin role is needed, but app-specific credential-management rights are still sensitive: they let the operator change which workload can authenticate as this connector. |
| Administrator-controlled Entra changes | Create the registration, consent, and federated credentials yourself; coordinate import of Terraform-managed credentials/assignments into the agreed state. Use an approved privileged operator for any later directory changes. | The normal operator needs sufficient read permissions to refresh/plan existing objects. Apply only a reviewed plan with no unauthorized Entra writes. Import alone does not remove Terraform's management responsibility. |

For a service-principal Terraform identity, application ownership alone is not
a replacement for consented Microsoft Graph API permissions. The deployment
team must arrange the appropriate provider permissions; for example,
`Application.ReadWrite.OwnedBy` for owned-app writes and required application
lookup permissions. Broader assignment writes have separate requirements in
[the deployment identity guidance](./MANUAL_ENTRA_ID_RBAC_IMPLEMENTATION_GUIDE.md#134-entra-permissions-for-the-deployment-identity).
Those permissions belong to the **deployment identity**, not this connector.

If policy prohibits even scoped directory writes, do not promise that the stock
Terraform script can create or repair these objects with Azure Contributor.
Complete the administrator-assisted changes and imports first. If a plan still
requests directory changes, stop and have the authorized administrator execute
them through the agreed workflow; do not give the operator a standing global
administrator role to get past the error.

### 5.1 Where the connector fits in the full deployment

For a **new environment**, use two passes: first deploy the base application with
`MICROSOFT365_SOURCE_MODE=off`, then configure and activate the connector after
the public TalentMatch URL and runtime managed identity are confirmed. Do not
enable `graph` in the first apply while registration, federation, or key
protection is incomplete.

The [full deployment list](./MANUAL_ENTRA_ID_RBAC_IMPLEMENTATION_GUIDE.md#2-required-implementation-order)
maps to this sequence as follows:

- Full-list steps **1-8** prepare product identity and deployment permissions;
  keep the connector off.
- Expand full-list steps **9-10** into shared-root provisioning **and Stack B
  host-root provisioning**. The shared root creates/reuses dependencies; it does
  **not** create the App Service. The actual host URL is available after the
  Stack B root is applied.
- After the host-URL handoff below, M365 administrators can complete connector
  setup in parallel with the remaining base deployment, full-list steps
  **11-15**. A running application is not required merely to register callback
  URLs, but it is required for the live connection test.
- Activate the connector only after base sign-in works and all connector
  prerequisites pass. Defer full-list step **16**, removal of temporary
  privileges, until connector setup and pilot/release approval are complete.

The table below is the executable ordering for this handoff. "Terraform
operator" means the actual CLI user or automation service principal, not the
runtime managed identity. Azure, Entra, SQL, and product permissions are
separate; one actor can perform several rows only if separately authorized.

| Order | Action and completion condition | Actor who performs the step | Required rights, scoped to this operation |
| --- | --- | --- | --- |
| D01 | Agree the environment, selected stack, data-processing approval, public-URL strategy, and ownership model in section 5. Resolve the product SPA's initial URL prerequisite using section 5.2. | Deployment lead, data owner, and hosting-tenant application administrator | Authority to approve the deployment/data use. Technical role grants are arranged in D04; planning itself requires no tenant-admin role. |
| D02 | Create/reuse the product API and selected SPA, configure product scopes/roles and redirects, and approve product consent. Create/map the required security groups. This is full-list steps 2-4, not connector setup. | Hosting-tenant Entra application/consent administrator; group administrator or group owner for group changes | Application Administrator or Cloud Application Administrator for application setup and delegated consent, or specific application ownership for non-consent changes. Group creation requires permitted group-creation rights; membership changes require ownership or Groups Administrator. API role assignments require ownership of the API service principal or Application Administrator/Cloud Application Administrator. |
| D03 | Select the enabled Member bootstrap user; arrange SQL Entra administrator configuration. Create/reuse the Stack B user-assigned managed identity, or authorize its creation in D06. Record its tenant, resource ID, client ID, and principal ID. | Azure SQL control-plane operator and Azure identity operator | `Microsoft.Sql/servers/administrators/read` and `/write` on the SQL server when it exists. Managed Identity Contributor or Contributor on the identity resource group for creation; Reader for lookup of an existing identity. No M365 tenant-admin rights are needed to create an Azure identity. |
| D04 | Grant the actual Terraform identity the Azure/provider/state access for the reviewed operations. Arrange manual product/AWReason assignment imports if it will not perform those directory writes. For GitHub Actions only, configure deployment OIDC. | Azure IAM administrator; hosting-tenant Entra administrator for directory grants | Role Based Access Control Administrator or User Access Administrator at the relevant Azure scopes to grant RBAC. Entra ownership/delegation as described in section 5; consenting Microsoft Graph **application permissions** for a deployment service principal requires Privileged Role Administrator or an appropriately authorized custom consent role, not merely Cloud Application Administrator. No tenant-admin role is assigned to the normal Terraform operator. |
| D05 | Prepare the base environment profile with connector mode explicitly `off` and product registrations reused where manually created. Use `AZ_SQL_BOOTSTRAP_ENABLED=FALSE` if the Terraform operator lacks database bootstrap rights or private SQL connectivity. | Terraform operator/configuration owner | Approved configuration access and Terraform state/backend access. This step grants no Entra, SQL, or source-content permission. |
| D06 | Review and apply the **shared root** for the base environment. Confirm SQL, the App Service plan, runtime identity, and required networking outputs. The connector remains off. | Terraform operator; authorized Entra operator for any directory changes outside the agreed delegation | Contributor on resource groups being managed; Reader on reused-resource groups; Network Contributor where changing networking. Any Azure role assignments, including optional monitoring assignments, need role-assignment write authority at their scopes. Entra lookups/writes follow D04; importing existing assignments does not eliminate read requirements. |
| D07 | Review and apply the **Stack B root** against those shared outputs, creating or adopting the actual App Service and attaching its runtime identity. A shared-only apply is not enough. | Terraform operator | Contributor on the host resource group, or equivalent App Service write permissions; Managed Identity Operator on a reused identity when not covered by broader access. Approved state/backend access. |
| D08 | **Host-URL handoff:** read the actual host URL, configure/verify the intended custom domain and HTTPS if used, and give the exact public origin plus both full callback URLs to the M365 administrator. Verify product SPA redirects against this origin too. | Azure App Service operator; DNS operator for a custom domain; hosting-tenant application owner for any product redirect correction | App Service read for the URL; App Service configuration/write rights for hostname/TLS binding. DNS write authority on the applicable zone, such as DNS Zone Contributor for Azure DNS, if records change. Product redirect correction needs ownership/write permission on that SPA. **Do not start D13 with a guessed or temporary hostname.** |
| D09 | Only for `AWR_AUTH_MODE=entra`, confirm the runtime identity's direct AWReason application-role assignment. Otherwise mark this step not applicable. | AWReason enterprise-application administrator or separately authorized Terraform identity | Ownership of the AWReason service principal or Application Administrator/Cloud Application Administrator. Automation needs approved Graph permissions such as `Application.Read.All` and `AppRoleAssignment.ReadWrite.All`. Azure Contributor and SharePoint Administrator do not grant this authority. |
| D10 | From a VNet-connected host, create the runtime SQL contained user and grant `db_datareader`, `db_datawriter`, and `db_ddladmin`. | Configured SQL Entra administrator or approved SQL bootstrap operator | Database user/schema/role-management rights on the application database, normally the configured SQL Entra administrator or temporary database-only `db_owner`, plus private SQL network access. Do not grant `db_owner` to the runtime identity. |
| D11 | Package/deploy Stack B with the connector still off; ensure database schema initialization/migration completes. | Application deployment operator; runtime managed identity performs startup database work | Website Contributor on the target App Service, or equivalent package-deployment rights. The runtime uses the database roles from D10; the packaging operator does not need M365 consent rights. |
| D12 | Complete bootstrap-admin check/apply/check and test base product sign-in, job authorization, database access, and scoring. | Authorized bootstrap operator; then TalentMatch administrator and test recruiter | Bootstrap requires Directory Readers or equivalent approved Graph read access, `Microsoft.Sql/servers/administrators/read`, API service-principal ownership/assignment permission if the direct admin assignment is absent, and configured SQL Entra administrator or temporary database-only `db_owner` access. See the [authorization runbook](./ENTRA_AUTHORIZATION.md#least-privilege-operator-permissions). Product checks use active TalentMatch roles/job access, not tenant-admin accounts. |
| D13 | **M365 setup begins:** create/review the separate connector using section 6.2, with the confirmed D08 callbacks and publisher evidence. This may run alongside D09-D12 after D08 is complete. | Hosting-tenant Entra application administrator | Application Administrator or Cloud Application Administrator, or approved app-creation rights plus ownership/write authority on the specific registration. Publisher/domain verification requires the organization's authorized publisher/domain owner. Source-folder ownership and Azure Contributor are not substitutes. |
| D14 | Create/review the connector's managed-identity federated credential using section 6.3 and the actual runtime principal from D03/D06. Return its ID for import if manually created. | Hosting-tenant application owner/administrator, or the specifically delegated Terraform identity | Ownership/credential-update permission on the connector (`microsoft.directory/applications/credentials/update`), Application Administrator, or Cloud Application Administrator. No source-tenant admin role is needed for this hosting-tenant trust. |
| D15 | Grant consent in **each source tenant** using section 7; configure connector enterprise-app user/group assignment if required. | Each source tenant's consent administrator; enterprise-application administrator for assignments | Cloud Application Administrator or Application Administrator for these **delegated** Graph consent grants, or an authorized custom consent role. Enterprise-app assignment-management authority for user/group assignments. SharePoint Administrator alone cannot grant Entra consent. |
| D16 | Prepare the test folder/account using section 8, and grant the recruiter's separate TalentMatch organization/department/job access. | Site/folder owner or SharePoint administrator; TalentMatch administrator for product access | SharePoint permission-management authority on the relevant content; use tenant SharePoint policy authority only when a policy change is approved. TalentMatch `admin` or appropriately scoped `organization_admin` for product assignments. A recruiter needs read/download access and job mutation permission, not either administrator role. |
| D17 | Consolidate the section 9 handoff, coordinate credential/assignment imports, and prepare the final `graph` profile. Review all resulting Entra and Azure changes before apply. | Terraform state owner and configuration owner; relevant administrators approve their changes | State/backend read/write and import access; required provider lookup permissions. Each proposed directory mutation must have its separately authorized actor under section 5. Import does not grant directory write rights. The running host is still connector-off. |
| D18 | Apply the reviewed **shared-only** connector changes: adopt the registration, reconcile/import federation, and create/verify the wrapping key, key-scoped runtime RBAC, private endpoint, and DNS. Capture the connector client ID and wrapping-key URI outputs. | Terraform operator with the agreed delegation; Azure IAM/network administrator and hosting-tenant app administrator for changes outside that delegation | D06 Azure rights, key resource-management permission, and role-assignment write at the key scope for `Key Vault Crypto User`. Network Contributor for private endpoint/DNS changes. Connector lookup/credential rights from D14, or administrator-performed changes and imports with no unauthorized directory diff. |
| D19 | After D09-D18 are satisfied, apply the **Stack B root** with the final connector settings. This changes runtime mode to `graph` and may restart the host; deploy a new package only if the required connector version is not already installed. Restrict access to the approved pilot. | Terraform/App Service deployment operator | App Service configuration/write rights, state access, and shared-output read access; Website Contributor/equivalent if deploying code. No tenant-admin role is required. Production publisher assertion must reflect actual verification. |
| D20 | Run section 10's browser-to-API-to-worker pilot and required T099/T100 release approval. Only then open the feature to the approved production population. | Test recruiter; M365/SharePoint administrators verify their controls; release owner approves rollout | Recruiter: authorized source read/download and TalentMatch job access. Evidence readers: Reports Reader for Entra logs, Audit Reader/equivalent Purview Audit permission for M365 audit evidence, and Monitoring Reader on Application Insights when used. Control changes require the administrator rights from the relevant earlier row. Release owner: organizational change/release authority. |
| D21 | Perform full-list step 16: remove temporary setup privileges, retain required runtime grants and any approved ongoing deployment delegation, and record ownership/support handoff. | Azure IAM administrator, Entra administrators, SQL administrator, and deployment lead | Authority to remove the respective Azure/Entra/database grants. Do not remove runtime SQL or key-use permissions needed by the running service. |

On a new database, D11 precedes the database-data seeding portion of D12 because
the application tables must exist. If the schema already exists, bootstrap may
be completed earlier as in the full list. This is not permission to sign in
before the product bootstrap is complete.

### 5.2 Resolve the URL dependency without temporary callbacks

An app registration needs the **exact callback URI string**, but the endpoint
does not have to be running when the administrator saves the registration.
Use the final public browser-facing HTTPS origin, not the App Service management
URL, an internal hostname, or a guessed name.

- If a stable customer-owned domain is approved in D01, the Entra administrator
  can configure product SPA redirects before the host exists. The DNS/App Service
  operators complete domain/TLS binding and verify that origin in D08.
- If only a newly generated App Service default hostname will be used, obtain it
  from the actual host. If the base product registration also cannot proceed
  without that hostname, have the Azure team provision the host through an
  approved bootstrap change and coordinate adoption/import into Terraform
  before D02/D07. Do not supply dummy product redirects or change authentication
  modes merely to bypass validation.
- After the Stack B root is applied, its `app_url` output is authoritative for
  the default App Service URL. In the already selected Terraform
  workspace/backend, the deployment operator can read it from the repository
  root with:

  ```powershell
  terraform -chdir="infra\terraform\live\stack-b" output -raw app_url
  ```

  App Service **Overview > Default domain** is an alternative. A custom domain
  selected for actual user access takes precedence over the default URL when
  composing the callback list.

D08 must return the approved origin, both complete callback URLs from section
6.2, and the runtime identity identifiers. D13-D14 cannot be signed off with
placeholders. If the public host later changes, the application owner must
update the connector Web callbacks and the deployment configuration before
users reconnect; update product SPA redirects separately.

### 5.3 Keep shared provisioning separate from runtime activation

The [deployment wrapper](../infra/scripts/deploy.sh) does not pause for an M365
administrator. Its `stack-b` target applies shared infrastructure and then the
Stack B host. For the second pass, use the wrapper's `shared-only` target for
D18 and `stack-b-only` for D19, with the same reviewed final environment profile,
Terraform environment, and established backend/workspace. The latter consumes
the shared outputs and does not reapply the shared root.

Setting `graph` in the profile for a **shared-only** apply provisions the
connector dependencies without yet updating the App Service runtime settings.
Only proceed to the host apply once registration, trust, key access, consent,
and the pilot population are approved. A successful shared apply alone does not
activate or validate the folder-source capability.

For an **existing environment**, verify the prerequisites and start at the
appropriate handoff point; do not rerun the new-environment connector-off pass
against an already enabled connector. Changing mode/selection in the shared
state can plan deletion of managed credentials or key resources. Always review
the plan and preserve the current ownership model.

## 6. Hosting-tenant administrator: create the connector

### 6.1 Obtain the deployment inputs

Ask the deployment team for the environment name, exact public HTTPS application
origin, and the user-assigned managed identity's tenant ID, client ID, and
**principal object ID**. These are different identifiers.

Obtain these at the **D08 host-URL handoff in section 5.1**; registration and
federation are D13-D14, before connector activation in D19. Do not wait for an
enabled connector to obtain the host URL, and do not enable the connector merely
to create the base host.

Have the Azure team pre-create the runtime identity if necessary so federation
can be completed before connector activation. For a new Stack B-only deployment,
the deployment team should select `STACK_TARGET=stack-b`; no Stack A registration
or trust is needed for this capability. Do not change an existing shared state's
stack target without reviewing the plan for deletions.

### 6.2 Create and configure the registration

In the hosting tenant's [Microsoft Entra admin center](https://entra.microsoft.com):

1. Open **Entra ID > App registrations > New registration**.
2. Use an environment-specific name, such as
   `talentmatch-<environment>-microsoft365-source`.
3. Select **Accounts in any organizational directory**. The manifest
   `signInAudience` must be `AzureADMultipleOrgs`, not a personal-account option.
4. Under **Authentication**, add the **Web** platform with the exact approved
   callbacks, normally:

   ```text
   https://<talentmatch-host>/api/source-connections/callback
   https://<talentmatch-host>/api/source-connections/admin-consent-callback
   ```

   Register each approved public host separately. Do not use wildcards, SPA
   callbacks, or an origin-only URL for the connector. The current Stack B
   endpoints use these paths; do not assume changing an environment path value
   creates a new server route. Reverse proxies must preserve the correct public
   scheme, host, and any approved base path.
5. Leave implicit grants and public-client flows disabled. Do not create a client
   secret or upload a client certificate.
6. Under **API permissions > Add a permission > Microsoft Graph > Delegated
   permissions**, add exactly `Files.Read.All` and `Sites.Read.All`. Remove an
   automatically added `User.Read` if present; it is not requested by this
   connector. Verify that neither permission is listed as **Application**.
7. Assign at least two durable customer-controlled application owners. Record
   the corresponding enterprise application and its owners.
8. Complete publisher-domain and publisher-verification review with the
   application's publisher. A display name or an environment assertion is not
   proof of verified publisher status.
9. Record the application client ID, application object ID, hosting tenant ID,
   and hosting-tenant enterprise application object ID for the secure handoff.

Publisher verification is a release prerequisite, not a substitute for consent.
The deployment team must set `MICROSOFT365_SOURCE_PUBLISHER_VERIFIED=true` only
after actual verification. Current runtime behavior uses `PRODUCT_ENVIRONMENT`
when populated, otherwise the ASP.NET/.NET runtime environment, defaulting to
Production. Product Production requires an explicit true assertion. Controlled
Development/QA may permit an absent/false assertion; do not label a production
deployment QA to bypass the gate or change the ASP.NET hosting environment.

### 6.3 Configure managed-identity federation

If Terraform is authorized to create the credential, review its planned values.
If the administrator owns credential creation, perform these manual steps:

1. Open the connector app registration, not the managed identity or GitHub
   deployment app.
2. Select **Certificates & secrets > Federated credentials > Add credential**.
3. Select **Managed Identity** where available and select the approved
   user-assigned identity. If using another issuer configuration, enter the
   same reviewed values below.

| Credential field | Required value for Stack B |
| --- | --- |
| Name/display name | `source-connector-stack_b`, matching the Terraform-managed name |
| Issuer | `https://login.microsoftonline.com/<hosting-tenant-id>/v2.0` |
| Subject | Stack B runtime managed identity **principal object ID**, not its client ID or Azure resource ID |
| Audience | `api://AzureADTokenExchange` |

The connector registration and the managed identity must belong to the **same
hosting tenant**, even when SharePoint content is in another tenant. Do not use
the source tenant's issuer or create a second managed identity there.
Do not confuse this trust with GitHub OIDC deployment federation.

Record the credential ID and pass it with the application object ID to the
Terraform state owner. The current credential resource address for Stack B is:

```text
module.entra.azuread_application_federated_identity_credential.source_connector["stack_b"]
```

If created manually, coordinate provider-appropriate import before apply; do not
let Terraform attempt to create a duplicate. Review the plan for replacements
or deletion of manually configured credentials. For any deliberately selected
additional runtime, review its trust separately rather than granting access to
all managed identities.

## 7. Source-tenant administrator: approve access

Perform this procedure in **each tenant whose content will be used**, including
the hosting tenant when it also holds source folders. Consent in the hosting
tenant is not consent in another tenant.

1. Obtain the connector's application client ID and verified publisher details
   from the hosting team through an approved channel. Check the ID, not just the
   application display name.
2. Activate a suitable consent-administrator role. A SharePoint Administrator
   role alone is not sufficient for Entra tenant-wide consent.
3. If the enterprise application already exists, open **Entra ID > Enterprise
   applications > All applications**, locate it by application client ID, and
   review **Security > Permissions**.
4. Review and grant administrator consent to the two delegated read permissions
   in section 4. For a locally owned registration, the equivalent action is
   **App registrations > API permissions > Grant admin consent**.
5. If an externally hosted multi-tenant app is not yet present, use Microsoft's
   tenant-specific consent URL with the independently verified client ID:

   ```text
   https://login.microsoftonline.com/<source-tenant-id>/adminconsent?client_id=<connector-client-id>
   ```

   Review the actual consent screen before accepting. Approval provisions the
   source tenant's enterprise application as needed; do not create a replacement
   app registration or give the hosting operator your administrator credentials.
6. Reopen the source tenant's enterprise application and verify the granted
   **delegated** Graph permissions. No Graph application app-role grant should
   have been added. Record the approval and source-tenant service principal
   object ID in the controlled handoff.
7. Decide who may sign in to the connector. Tenant-wide consent does not itself
   restrict access to a recruiter group. If required by policy, set
   **Properties > Assignment required? > Yes**, then assign the approved users
   or groups under **Users and groups**. Confirm applicable Entra licensing for
   group-based assignment; use direct assignments where necessary.
8. Check Conditional Access, MFA, device/session controls, user-consent policy,
   and external-sharing restrictions. Keep the policies in place. A session
   requiring fresh interaction may interrupt background imports; users must
   reconnect rather than the app bypassing the requirement.

Enterprise-app assignment is an additional sign-in restriction, not SharePoint
file permission or a TalentMatch role assignment.

The current application consent callback is not proof that a pending recruiter
connection has resumed. Portal or standalone URL consent can return without a
valid pending connection and may show a callback error after consent succeeds.
Verify the grant in Entra, then have the recruiter **start a fresh connection**
from TalentMatch. Do not manufacture OAuth state values or share callback URLs
containing codes/tokens.

## 8. SharePoint administrator or content owner: prepare users and folders

1. Grant the intended connected work/school account access to the folder and
   required descendants, through approved site/library membership or sharing.
   Access must permit document download, not only browser preview.
2. Verify that the same account can open the folder and download representative
   files. Folder ownership and site-administrator rights are not prerequisites.
3. Check library/folder/file-specific permissions. A readable root folder does
   not make every descendant readable. Restricted descendants may be excluded
   or fail rather than being read under another identity.
4. Supply a supported SharePoint or OneDrive for Business folder link, not an
   individual file, a consumer OneDrive link, or a sign-in page. Anonymous
   "Anyone" sharing is neither required nor a substitute for delegated sign-in.
5. Check sensitivity-label encryption, rights management, download restrictions,
   sharing expiry, and applicable data-loss-prevention policies. Consent does
   not bypass these controls. Do not remove protections merely to make scoring
   succeed; obtain a data-owner decision on an approved source.
6. For content in another organization, have that organization's administrator
   complete section 7 and approve the relevant account/sharing arrangement.
   The connected account must successfully resolve and read that folder in its
   source context; a product login or another tenant's consent is insufficient.
7. Ask the TalentMatch administrator to confirm the recruiter's product/job
   access. Connecting Microsoft 365 does not grant access to a TalentMatch job.

## 9. Return the handoff to the deployment team

Use an approved configuration register or ignored environment profile, not a
committed document with customer identifiers. Return:

| Handoff item | Deployment use |
| --- | --- |
| Hosting tenant and connector application client ID | Set `MICROSOFT365_SOURCE_CLIENT_ID`; distinguish it from product SPA/API IDs. |
| Connector application object ID and hosting enterprise application object ID | Ownership, audit, provider lookup/import, and troubleshooting. |
| Exact Web callback list | Set `MICROSOFT365_SOURCE_REDIRECT_URIS` as a comma-separated list. |
| Reviewed publisher/domain evidence | Supports the explicit publisher assertion and release decision. |
| Managed identity principal ID and federated credential ID/values | Review trust and import any manually created Terraform-managed credential. |
| Consent decision and enterprise application ID for each source tenant | Verify the grant in the correct directory; these are not source allow-list settings. |
| Connector assignment policy and approved users/groups | Verify users can sign in to the connector. |
| Terraform ownership/permissions agreement | Identify who may change credentials and who imports or applies directory changes. |

The deployment team configures the following; the administrator approves the
identity/consent inputs rather than running deployment commands:

This is the **final connector profile for D17-D19**, not the initial base-host
profile. The initial new-environment deployment uses
`MICROSOFT365_SOURCE_MODE=off`; apply the final profile to shared infrastructure
first and to the Stack B runtime only after the administrator handoff is complete.

```ini
MICROSOFT365_SOURCE_MODE=graph
MICROSOFT365_SOURCE_REUSE=TRUE
MICROSOFT365_SOURCE_CLIENT_ID=<precreated-connector-client-id>
MICROSOFT365_SOURCE_REDIRECT_URIS=https://<talentmatch-host>/api/source-connections/callback,https://<talentmatch-host>/api/source-connections/admin-consent-callback
MICROSOFT365_SOURCE_CALLBACK_PATH=/api/source-connections/callback
MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH=/api/source-connections/admin-consent-callback
MICROSOFT365_SOURCE_AUTHORITY=https://login.microsoftonline.com/organizations
MICROSOFT365_SOURCE_SCOPES=openid profile offline_access Files.Read.All Sites.Read.All
PRODUCT_ENVIRONMENT=Production
MICROSOFT365_SOURCE_PUBLISHER_VERIFIED=true
AZ_KEY_VAULT_ENABLED=TRUE
AZ_VNET_REUSE=TRUE
```

The Production example assumes publisher verification has actually completed.
Keep the canonical scope order shown. `ENTRA_REUSE=TRUE` concerns the separate
product registrations; it does not replace `MICROSOFT365_SOURCE_REUSE=TRUE`.

The Azure team must also complete Key Vault creation/reuse coordinates, the
wrapping key, VNet integration, private endpoint/DNS connectivity, and runtime
key-scoped `Key Vault Crypto User` assignments. The standard Terraform workflow
passes `BULK_AUTH_KEY_URI` from shared outputs; do not substitute a client
password, certificate, or source access token. The Terraform identity needs
separate Azure role-assignment/network permissions for this work.

## 10. Joint pilot and acceptance checklist

Use non-sensitive test documents and an ordinary recruiter account, not a tenant
administrator's account. Record sanitized outcomes, times, and correlation IDs;
keep tenant IDs, source URLs/names, and operational identifiers in the controlled
handoff rather than public or committed evidence. Never record tokens or caches.

- [ ] Connector client ID is distinct from product sign-in/API registrations.
- [ ] `AzureADMultipleOrgs`, exact Web callbacks, no client secret/certificate,
  and only the required delegated Graph read permissions are configured.
- [ ] Federation matches the hosting tenant and the exact runtime principal.
- [ ] Every applicable source tenant has the approved consent grant.
- [ ] Any required enterprise-app assignments include the test recruiter.
- [ ] The source account has folder/download access and the recruiter has
  separate TalentMatch job mutation permission.
- [ ] A fresh connection returns to TalentMatch, verifies the intended folder,
  and permits preview and confirmation.
- [ ] A test import completes in the background after the browser is closed;
  scoring and any approved conversions complete as expected.
- [ ] An inaccessible folder/file is not exposed or read through a fallback.
- [ ] A separately consented source tenant/shared-but-not-owned folder is tested
  if the intended rollout includes those scenarios.
- [ ] Controlled access removal or session interruption stops further source
  reads once detected; reconnect/retry is tested. Already imported content is
  handled separately and is not assumed deleted.
- [ ] Data storage/scoring/retention approval and the release team's T099/T100
  decision are recorded before production activation.

Administrative approval and a green Terraform apply do not replace the live
browser-to-API-to-worker acceptance test.

## 11. Common failures and administrator actions

| Symptom | Check and action |
| --- | --- |
| "Need admin approval" or consent blocked | Verify the client ID, source directory, consent policy, and delegated grant. Have the authorized source-tenant administrator approve it; do not grant the recruiter an admin role. |
| Redirect URI mismatch | Compare the actual public callback with the connector's Web registrations, including path/base path. Ask the hosting team to correct proxy/host configuration where necessary. |
| User is not assigned to the app | If connector assignment is required, assign the user/group in that tenant's enterprise application. Check product access separately. |
| Folder inaccessible despite consent | Confirm the connected account, folder/download rights, sharing expiry, protected content, and the correct source context. Consent is not a file ACL grant. |
| Managed-identity token exchange fails | Hosting team checks the credential's issuer, audience, and subject principal ID, plus runtime identity attachment/client ID. Do not add Graph application permissions as a workaround. |
| Terraform reports insufficient directory privileges | Follow the ownership/import model in section 5. Azure Contributor cannot create Entra credentials or consent grants. Reuse is not a bypass for directory permissions. |
| Connector unavailable or app fails activation | Deployment team checks `MICROSOFT365_SOURCE_MODE`, product-environment/publisher assertion, required connector settings, and private Key Vault connectivity. |
| Background work requests sign-in again | Check session/Conditional Access policy, consent revocation, and source access. Request-bound authorization has a default maximum age of 72 hours (`BULK_AUTH_MAX_AGE_HOURS`), not a guaranteed session duration. Have the recruiter reconnect and retry failed work. |
| Callback error after administrator approval | Inspect enterprise-app permissions to determine whether consent succeeded, then restart the recruiter's connection. Do not treat the return page alone as the consent result. |

## 12. Revocation and ongoing administration

- Remove a user's folder access to restrict future reads of that content.
- Revoke the connector's delegated consent or disable its enterprise application
  in a source tenant to stop future authorized use there. Review both tenant-wide
  and any individual grants; deleting one grant is not proof that all are gone.
- Disabling a user or revoking sessions can require fresh authentication.
  Microsoft token lifetimes, policy evaluation, and propagation mean revocation
  must not be described as instantaneous cancellation of every in-flight call.
- Coordinate urgent shutdown with the TalentMatch/Azure team to stop active
  source work as well. Removing a federated credential affects its trusted
  workload and can be recreated by Terraform unless state/desired configuration
  ownership is updated.
- Once a lapse/access failure is detected, source-dependent work cannot switch
  to application-only access or another user. Already imported content and
  completed scoring results need a separate TalentMatch retention/deletion
  decision.
- Review consent, owners, enterprise-app assignments, publisher evidence, and
  federated identities regularly and when public callback hosts change. Monitor
  Entra sign-in/audit logs and applicable Microsoft 365 audit records under your
  licensing and policy; use application correlation IDs for escalation.

## 13. References

### TalentMatch

- [Customer identity, Azure RBAC, and SQL guide](./MANUAL_ENTRA_ID_RBAC_IMPLEMENTATION_GUIDE.md)
- [Connector registration and technical configuration](./SHAREPOINT_BULK_SCORING_APP_REGISTRATION.md)
- [Configuration reference](../CONFIGURATION.md)
- [Product authentication and authorization](../AUTHENTICATION.md)
- [Bulk-scoring feature and release gates](../specs/001-sharepoint-bulk-scoring/tasks.md)

### Microsoft

- [Microsoft Graph permissions reference](https://learn.microsoft.com/graph/permissions-reference)
- [Grant tenant-wide admin consent](https://learn.microsoft.com/entra/identity/enterprise-apps/grant-admin-consent)
- [Configure an application to trust a managed identity](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity)
- [Assign users and groups to an enterprise application](https://learn.microsoft.com/entra/identity/enterprise-apps/assign-user-or-group-access-portal)
- [Configure user consent](https://learn.microsoft.com/entra/identity/enterprise-apps/configure-user-consent)
