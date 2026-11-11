# Stack B contract map (T011)

Layer ownership for `specs/001-sharepoint-bulk-scoring`. Every operation and entity has exactly one owning layer; nothing in this feature touches Stack A runtime before B-GATE (T073) except the types-only task T012.

## Layers

| Layer | Project | Owns |
| --- | --- | --- |
| Domain | `dotnet/src/Domain` | Entities, enums with wire values, state machines, invariants, repository interfaces |
| Application | `dotnet/src/Application` | Ports, options, DTOs, validators, MediatR commands/queries, services (eligibility, discovery step, link classification, authorization lifecycle) |
| Infrastructure | `dotnet/src/Infrastructure` | EF mapping and migration, repositories, Graph/mock sources, authorization store and key wrapper, rasterizer, content store, hosted worker |
| Web.Server | `dotnet/src/Web.Server` | Minimal-API endpoints, auth, error mapping |
| Web.Client | `dotnet/src/Web.Client` | `ApiClient` methods, Blazor components |

## Entities

| Entity | Owning layer | Types |
| --- | --- | --- |
| BulkScoringRequest | Domain | `Domain/Entities/BulkScoringRequests/BulkScoringRequest.cs` |
| BulkScoringItem | Domain | `BulkScoringItem.cs` |
| BulkScoringItemContent | Domain | `BulkScoringItemContent.cs` |
| BulkImportSettings | Domain | `BulkImportSettings.cs` |
| BulkRequestAuthorization | Domain | `BulkRequestAuthorization.cs` (persisted by Infrastructure; protected material only ever handled by `IRequestAuthorizationStore`) |

Persistence of each entity is Infrastructure (`BulkScoringModelConfiguration`, migration `AddBulkScoring`, repositories) behind the Domain interfaces.

## OpenAPI operations

| Operation | Endpoint | Owning layer (handler) | Web.Server adapter |
| --- | --- | --- | --- |
| getBulkScoringCapabilities | `GET /api/bulk-scoring-requests/capabilities` | Application | `BulkScoringRequestsEndpoints` |
| getBulkScoringSettings / updateBulkScoringSettings | `GET`/`PUT /api/bulk-scoring-settings` | Application | `BulkScoringSettingsEndpoints` |
| previewBulkScoringRequest | `POST /api/jobs/{jobId}/bulk-scoring-requests/preview` | Application | `BulkScoringRequestsEndpoints` |
| discoverMoreBulkScoringRequest | `POST /api/bulk-scoring-requests/{id}/discover-more` | Application | `BulkScoringRequestsEndpoints` |
| getBulkScoringRequest / abandonBulkScoringRequestDraft | `GET`/`DELETE /api/bulk-scoring-requests/{id}` | Application | `BulkScoringRequestsEndpoints` |
| confirmBulkScoringRequest | `POST /api/bulk-scoring-requests/{id}/confirm` | Application | `BulkScoringRequestsEndpoints` |
| listJobBulkScoringRequests | `GET /api/jobs/{jobId}/bulk-scoring-requests` | Application | `BulkScoringRequestsEndpoints` |
| listBulkScoringRequestNotifications | `GET /api/bulk-scoring-requests/notifications` | Application | `BulkScoringRequestsEndpoints` |
| acknowledgeBulkScoringRequest | `POST /api/bulk-scoring-requests/{id}/acknowledge` | Application | `BulkScoringRequestsEndpoints` |
| listBulkScoringRequestItems | `GET /api/bulk-scoring-requests/{id}/items` | Application | `BulkScoringRequestsEndpoints` |
| downloadBulkScoringRequestOriginal | `GET /api/bulk-scoring-requests/{id}/items/{itemId}/original` | Application | `BulkScoringRequestsEndpoints` |
| cancelBulkScoringRequest | `POST /api/bulk-scoring-requests/{id}/cancel` | Application | `BulkScoringRequestsEndpoints` |
| retryFailedBulkScoringRequestItems | `POST /api/bulk-scoring-requests/{id}/retry-failed` | Application | `BulkScoringRequestsEndpoints` |

The background worker (`BulkScoringWorker`) is Infrastructure and has no HTTP surface.

## Legacy isolation

- The existing multipart upload endpoint (`ApplicationsEndpoints`), `UploadApplications.razor`, and the optional parallel-upload session flow stay **outside** the new contract and are not routed through it.
- Prohibited before B-GATE: any Stack A runtime change (`server/`, `src/` other than `src/types/index.ts`, schema files). T012 touches only `src/types/index.ts`.

## Documented legacy exceptions

| # | Exception | Owning tasks |
| --- | --- | --- |
| 1 | Standardized raw-byte SHA-256 fingerprint; duplicate lookup accepts a candidate set (raw-byte and legacy base64-string hash) at the four lookup sites | T050 (Stack B), T093 (Stack A) |
| 2 | Settings screens show limits in MB | T049 (Stack B), T092 (Stack A) |
| 3 | Per-file default raised from 4 MiB to 8 MiB for installations with no saved value (also changes the optional parallel upload's default) | T023 (Stack B, done), T094 (Stack A) |

### T023 notes (8 MiB default)

`UploadSettings.DefaultMaxIndividualFileBytes` changed from `4_194_304` to `8_388_608`. Saved rows are untouched. Existing tests that mirror the default were updated (and only those): `UploadSettingsTests`, `OptionalUploadSessionTests` (default snapshot, one-byte-over case), `OptionalUploadPersistenceTests` (session snapshot). Tests with explicit limits are unchanged.
