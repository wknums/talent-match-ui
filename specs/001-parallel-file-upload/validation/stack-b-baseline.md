# Stack B upload baseline

Recorded before optional-upload source changes on 2026-09-22.

## Focused results

All commands were run from `C:\code\awr-cv-match-client`.

| Existing behavior suite | Command | Result |
|---|---|---|
| Application command | `dotnet test .\dotnet\tests\Application.Tests\TalentMatch.Application.Tests.csproj --filter "FullyQualifiedName~UploadApplicationsCommandTests"` | PASS — 4 passed, 0 failed |
| Infrastructure publication | `dotnet test .\dotnet\tests\Infrastructure.Tests\TalentMatch.Infrastructure.Tests.csproj --filter "FullyQualifiedName~UploadedApplicationPublicationTests"` | PASS — 2 passed, 0 failed |
| Blazor upload usability | `dotnet test .\dotnet\tests\Web.Tests\TalentMatch.Web.Tests.csproj --filter "FullyQualifiedName~JobUsabilityComponentsTests"` | PASS — 9 passed, 0 failed |

The first attempt to execute the three projects concurrently caused two compiler-output
file-lock errors (`CS2012`) against the shared Domain build output. This was a command
orchestration issue rather than a product/test failure. The affected commands were rerun
sequentially and passed as shown above.

## Frozen legacy expectations

- Stack B continues to send one multipart batch through
  `ApiClient.UploadApplicationsAsync`.
- The existing endpoint dispatches `UploadApplicationsCommand`.
- Publication remains all-or-none, returns `Queued`, and pulses the existing scoring
  queue.
- Existing duplicate, validation, progress, warning, close, and result behavior is the
  baseline for optional-mode-off regressions.
- The legacy path has no optional upload settings, session, or item dependency.
