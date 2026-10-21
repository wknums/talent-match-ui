# Spike S5 — long-running on-behalf-of (OBO)

**Task**: T008A — **status: IN PROGRESS** (feasibility and lapse behavior proven in .NET and Node; the +1 h / +24 h duration readings are being collected). Not ticked in `tasks.md`.
**Started**: 2026-10-05 (session start ≈ 14:10 UTC)
**Tenant**: the home (product) tenant. No SharePoint involved: the unattended call is Graph `GET /me`.

## Setup

Throwaway objects, all in the home tenant, created by `infra/scripts/lib/spike-s5.sh` and removed by `s5_cleanup`:

| Object | Purpose |
| --- | --- |
| `talent-match-s5-api` app | Stands in for the product API: confidential client, **certificate** credential (the real stacks use a managed-identity federated credential), exposes `access_as_user`, tenant-wide consent for delegated `User.Read`, `Files.Read.All`, `Sites.Read.All` (the scopes the feature will use) |
| `talent-match-s5-client` app | Public client, pre-authorized for the API (stands in for the SPA / Blazor client) |
| Test users `s5-control`, `s5-revoke`, `s5-reset`, `s5-disable`, `s5-ca` | One per scenario, because revoking sessions affects every session of a user |

The real API registration was never touched. The tenant enforces MFA, so each test user signs in **once, interactively, with a Temporary Access Pass**; everything after that is unattended.

**.NET harness**: `dotnet/spikes/S5.LongRunningObo` (MSAL.NET, `Microsoft.Identity.Client` 4.x; not part of `TalentMatch.slnx`).

- `login`: signs the user in to the client app, takes the API token, calls `InitiateLongRunningProcessInWebApi` (scopes: the three Graph scopes), stores the session key and the token cache (a file here; production: encrypted rows in `BulkRequestAuthorizations`), calls `/me`.
- `probe`: a **new process** with no user and no request calls `AcquireTokenInLongRunningProcess` with `WithForceRefresh(true)` (so Entra is really asked, not the local cache) and calls `/me`.

## Results — .NET (MSAL.NET `InitiateLongRunningProcessInWebApi` / `AcquireTokenInLongRunningProcess`)

### Unattended access

| Time after the sign-in | Result | Notes |
| --- | --- | --- |
| +0 min (login) | 200 | initial OBO token |
| +0.5 min, from cache | ok | token source `Cache` |
| +1.4 min, forced refresh in a new process | ok, 200 | token source `IdentityProvider`: Entra honoured the stored session without the user or the original request |
| +2 … +22 min, repeated | ok, 200 | control user probed every 30 min by a scheduled task |
| +1 h, +24 h | **pending** | the control user is probed every 30 min for 28 h by a scheduled task; see "Open items" |

### Lapse behavior (forced refresh in a fresh process; Graph tokens last ≈ 1 h, so every probe really asks Entra)

| Trigger | When it showed | MSAL exception | Error | Distinguishable from a transient failure? |
| --- | --- | --- | --- | --- |
| **Sign-in sessions revoked** (`revokeSignInSessions`) | Immediately; unchanged at +2 and +6 min | `MsalUiRequiredException` | `invalid_grant`, **AADSTS50173** | **Yes** — typed exception, `Classification` set; permanent: the session never recovered |
| **Account disabled** | Immediately; unchanged at +2 and +6 min | `MsalUiRequiredException` | `invalid_grant`, **AADSTS130507** | **Yes** — same exception type; **recovered** when the account was re-enabled (the old session worked again at once) |
| **Admin password reset** (Graph `PATCH` of the password) | **Did not lapse** for the 8+ minutes observed (probes at +0, +2, +6) | none | none | n/a — an admin reset alone does **not** invalidate the session; revoke is the lever. A further probe runs every 30 min to see whether it lapses later |
| **Conditional Access: block**, scoped to one test user, created after the session started | **Not at the first probe** (propagation); at ≈ 90 s | `MsalUiRequiredException` | `invalid_grant`, **AADSTS53003** | **Yes** — and the **same old session worked again** once the policy was removed |
| **Conditional Access requiring MFA / sign-in frequency** | **pending** (see open items) | | | |

Transient failures surface as a different exception type (`MsalServiceException`, `IsRetryable`) and are classified `transient_service_error` by the harness; none occurred.

### What this means for the design (R2b, R13)

1. The authorization store works as designed in .NET: a session started at confirm keeps yielding delegated Graph tokens in a separate process with no user present.
2. Lapse is detectable: **every trigger except an admin password reset surfaces as `MsalUiRequiredException` / `invalid_grant`** with a specific AADSTS code. Map `MsalUiRequiredException` to `authorization_lapsed` (retryable after the recruiter signs in again); anything else (`MsalServiceException` with `IsRetryable`, HTTP 5xx/429) is transient and must not lapse the authorization.
3. The lapse is **per session, not forever** for a disabled account or a Conditional Access block (it resumes when the condition is lifted), but **permanent for a revoked session**. The product must not assume a lapsed authorization is dead: the safe behavior is the documented one — mark `lapsed`, fail remaining items as `authorization_lapsed`, and require the recruiter to start a **new** authorization on retry (R13).
4. An administrator resetting a password does not stop a running request; revocation (the normal offboarding action) does.
5. The harness never printed or logged a token or the session key (only outcomes and timings).


## Results — Node (MSAL Node 5.6.0, `spikes/s5-long-running-obo-node`)

MSAL Node has no long-running OBO helper, so the test was whether `acquireTokenOnBehalfOf` (with `offline_access` added to the scopes) followed, **in a new process with no user and no request**, by `acquireTokenSilent` against a persisted cache can renew. The cache is a file here; production would encrypt it and store it per request.

| Check | Result |
| --- | --- |
| OBO exchange of the incoming API token | ok, Graph `/me` 200; the cache then held **1 account, 1 refresh token, 1 id token, 1 access token** (MSAL Node does keep an account for an OBO result, so no custom refresh flow is needed) |
| New process, from cache | ok, 200 |
| New process, **forced refresh at Entra** (`acquireTokenSilent`, `forceRefresh: true`) | ok, 200 — twice in a row, so the rotated refresh token keeps working |
| **Conditional Access block** (scoped to the one test user, created after the session started) | `InteractionRequiredAuthError`, `invalid_grant`, **AADSTS53003**, at ≈ 2.6 and 3.6 min; the **same session worked again** once the policy was removed |
| +1 h, +24 h | **pending**: scheduled task `talent-match-s5-probe-node-ca` probes every 30 min |
| Revoked session / disabled account (Node classification) | **pending**: to be run at the end of the 24 h window (revoking is irreversible and would end the session being timed). Expected `InteractionRequiredAuthError` with AADSTS50173 / AADSTS130507, as in .NET |

So far the Node half behaves like the .NET half; the open risk is duration (+24 h) and the revoke/disable classification, not feasibility.

## First long run: the sessions ended with their Temporary Access Pass (a test artifact, not a product limit)

The scheduled probes ran from the sign-in until 2026-10-05 21:45 local (about 5.5 h). All three long-running sessions, two .NET users and one Node user, worked until about **+2 h** and then failed on every later probe with `invalid_grant`, **AADSTS130504**:

| Session | Signed in | Last good probe | First failed probe | Temporary Access Pass lifetime |
| --- | --- | --- | --- | --- |
| .NET `s5-control` | 14:10 UTC | +94.5 min | +124.2 min | 120 min |
| .NET `s5-reset` | 14:25 UTC | +103.2 min | +133.2 min | 120 min |
| Node `s5-ca` | 14:49 UTC | +98.3 min | +128.6 min | 120 min |

AADSTS130504 means the Temporary Access Pass expired. The tenant enforces multi-factor authentication, so each test user signed in once with a pass that lasted 2 hours, and Entra tied the session to the pass. The numbers line up for all three users, in both runtimes.

What this does and does not tell us:

- It does **not** show that a recruiter's long-running session ends after 2 hours. Recruiters sign in with their normal credentials and multi-factor method, not a pass.
- It is **one more distinguishable lapse code** (`invalid_grant` with `AADSTS130504`, surfaced as `MsalUiRequiredException` in .NET and `InteractionRequiredAuthError` in Node), so lapse handling needs no special case for it.
- It does show that **the session cannot outlive the credential it was created from**, which is the property the `BULK_AUTH_MAX_AGE_HOURS` setting has to respect.
- It means **the +1 h reading is proven (all three sessions worked through +94 min) but +24 h is not measurable with these sessions.** The 24 h reading needs sessions created with a credential that lasts at least 26 hours.

To get a valid +24 h reading: allow a longer maximum lifetime in the Temporary Access Pass authentication-method policy (Entra admin center → Protection → Authentication methods → Temporary Access Pass; the default maximum is 8 hours, the portal allows up to 30 days), create a pass of 28 hours for `s5-control` and `s5-ca`, sign each in once, and probe again. The scheduled probes were stopped on 2026-10-05 because they could only repeat the failure.

## Second run: 6-hour test with a normal (non-pass) sign-in

Decision (2026-10-06): a 24 h reading is not needed. The longest request is expected to take hours, not days, so the test runs **6 hours** and `BULK_AUTH_MAX_AGE_HOURS` **stays at 72 as a provisional value**. If a session or request is longer than what was tested, the worker lapses it cleanly and the recruiter uses Retry failed. T008A's "+24 h" acceptance is replaced by "+6 h".

- Both runtimes use the same sign-in: the tester's own account (`admin` in the spike user file), signed in interactively with the normal password and MFA, so the session is not tied to a Temporary Access Pass.
- Session start ≈ 07:50 UTC on 2026-10-06 for .NET (login) and 07:54 UTC for Node.
- Probes every 30 minutes by scheduled tasks `talent-match-s5-probe-admin` (.NET) and `talent-match-s5-probe-node-admin` (Node), for 7 hours from 08:24 UTC. A probe that runs late (laptop asleep) is still recorded with its true elapsed time.
- Read the results with `s5_run show` and `node spikes/s5-long-running-obo-node/s5.mjs show`; look at the `admin` rows. Any probe at or after +6 h that succeeds closes the duration reading.
- Do not use "sign out everywhere" or revoke sessions for that account while the test runs.

### Result of the second run (2026-10-06 to 2026-10-12)

Both sessions were created with a normal sign-in (own account, password and MFA), not a Temporary Access Pass.

| Runtime | Probes | Result |
| --- | --- | --- |
| .NET (`AcquireTokenInLongRunningProcess`, forced refresh at Entra) | 15, every 30 min from +0 to +422 min | all `ok`, Graph `/me` 200 each time |
| Node (MSAL Node, encrypted-cache design, forced refresh) | 14, every 30 min from +0 to +421 min (the +6.5 h probe was skipped, the laptop was asleep) | all `ok`, token source `IdentityProvider`, Graph 200 |
| Both, one extra probe about **6.3 days** (9,078 min) after sign-in | 1 each | `ok`, Graph 200 |

- **+1 h and +6 h are proven in both runtimes** and the session was still valid after 6.3 days. This is a normal-credential session in this tenant; the tenant has no Conditional Access sign-in-frequency rule that applies to it.
- **Decision (product owner, 2026-10-12): `BULK_AUTH_MAX_AGE_HOURS` stays at 72.** The session in this tenant outlives any request by days, so the age limit is the control that bounds the exposure. Do not shorten it without a new decision.
- Still open for T008A: the **Node** revoke and disable classification (the .NET results are recorded above). Both need a live Node session for a spike user that is not the tester's own account, because revoking sessions signs the account out everywhere.

## Open items

1. **+1 h and +24 h**, both runtimes: the control user (.NET) and `s5-ca` (Node) are probed every 30 min by scheduled tasks `talent-match-s5-probe-control` and `talent-match-s5-probe-node-ca`. Read the results with `s5_run show` and `node spikes/s5-long-running-obo-node/s5.mjs show` after 2026-10-06 ≈ 14:15 UTC (.NET, session start ≈ 14:10 UTC) and ≈ 14:50 UTC (Node, session start ≈ 14:49 UTC). The machine must stay awake; a missed run is recorded late (`StartWhenAvailable`).
2. **Password reset** (.NET): `talent-match-s5-probe-reset` probes every 30 min to see whether the session lapses later.
3. **Node revoke and disable classification**: after the 24 h reading, run `s5_lapse ca revoke`, then one Node probe; then `s5_lapse ca disable`.
4. **Conditional Access requiring MFA / sign-in frequency**: not yet exercised in either runtime. The tenant's own policies already require MFA for all users, and the test users satisfied it with a Temporary Access Pass; the interesting case is a sign-in frequency below the request duration (it affects `BULK_AUTH_MAX_AGE_HOURS`).
5. Set `BULK_AUTH_MAX_AGE_HOURS` from the measured lifetime once +24 h is in.
6. Cleanup when the spike is closed: `s5_cleanup`; `Unregister-ScheduledTask talent-match-s5-probe-control, talent-match-s5-probe-reset, talent-match-s5-probe-node-ca`; delete `dotnet/spikes/S5.LongRunningObo` and `spikes/s5-long-running-obo-node`.
