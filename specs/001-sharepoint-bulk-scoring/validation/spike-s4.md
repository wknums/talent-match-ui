# Spike S4 — cross-tenant app-only access limited to the granted site

**Task**: T006 — **status: .NET host PROVEN on the deployed QA app, Node host outstanding**. The per-site 403 was proven with a certificate (2026-10-05); the managed-identity credential was proven on the deployed Stack B host on 2026-10-12 (see below). Not ticked in `tasks.md` until the Node host (Stack A) has run too, or the owner accepts the .NET proof for Stack B's gate.
**Date**: 2026-10-05 (certificate), 2026-10-12 (managed identity)
**Mode**: cross-tenant (product tenant ≠ SharePoint tenant), app-only, SharePoint only

## What was proven

| Check | Result | How |
| --- | --- | --- |
| App-only token for the SharePoint tenant from a local .NET/shell client | **PASS** | Certificate client assertion (RS256 JWT, `x5t#S256`); no secret on the app |
| `GET /sites/{granted}` and `/sites/{granted}/drives` | **PASS** (200) | `sp_a_verify` |
| Live folder resolves as a folder by share URL | **PASS** | `sp_a_verify`; live test `The_configured_folder_resolves_as_a_sharepoint_folder_in_the_expected_tenant` |
| Document library of a **second, ungranted** site | **PASS — HTTP 403** | `sp_a_verify`; shares API probe |
| Folder that does not exist in the ungranted site | **403** (not 404) | Probe: the app cannot learn whether an item exists |
| Bare site URL (not a drive item) on the ungranted site | HTTP 400 → product raises "unsupported" | Probe; it is not a folder link, so this is not an access check |
| Product refusal | **PASS** | Live test `A_site_the_app_was_not_granted_is_refused_without_revealing_anything` → `SourceAccessDeniedException` |
| App holds only `Sites.Selected` | **PASS** | Application's required access lists one Graph application role; 1 certificate, 0 client secrets |

Scoping of the grant: the granted site is the tenant **root site**, so the `/sites/root is 403` check is skipped by the helper; the ungranted second site is the scoping proof.

## Managed identity on the deployed Stack B host (2026-10-12)

**Finding — the original design cannot work.** A federated credential placed on the reader app **registered in the SharePoint tenant**, trusting the Stack B managed identity (issuer = the product tenant's v2.0 endpoint), is refused by Entra: `AADSTS700236: Entra ID tokens issued by issuer '…/v2.0' may not be used for federated identity credential flows for applications or managed identities registered in this tenant`. The SharePoint tenant's sign-in log recorded four such failures (error 700236) for the original reader app.

**Working design.** A **multi-tenant app registered in the product tenant** (Terraform: `module.entra.azuread_application.sharepoint_reader`, sign-in audience `AzureADMultipleOrgs`) trusts the managed identity (credential issuer = the product tenant, subject = the identity's principal ID, audience `api://AzureADTokenExchange`). In the SharePoint tenant, an admin consented to it (an enterprise application with the `Sites.Selected` role) and granted it **read** on the one test site (`grant-sharepoint-site.sh --consent`). The host requests its token from the SharePoint tenant's endpoint with the managed identity's token as the client assertion (`ClientAssertionCredential`). No certificate, secret or password exists on the host.

| Check | Result |
| --- | --- |
| Preview of the live test folder from the deployed host | **PASS** (the first attempt after the change; the log showed the OLD client id until the web app was restarted) |
| A bulk request from that folder (68 synthetic files) | running/finished on the deployed host; recruiter confirmed it works |
| Sign-in log in the SharePoint tenant | the 700236 failures belong to the old reader app only |

## What remains (why T006 is not ticked)

1. The Node host (Stack A; not built yet) must obtain the same token. The mechanism is identical (the same multi-tenant reader app; add `stack_a` to `sharepoint_reader_trusted_stacks`).
2. If it fails, choose the certificate fallback (research.md S4) and record it here. The certificate path is **already proven** by this record.

## Decision

The **managed-identity credential on a multi-tenant reader registered in the product tenant** is the deployed credential for cross-tenant mode, proven from the .NET host. The certificate stays the credential for local runs.
