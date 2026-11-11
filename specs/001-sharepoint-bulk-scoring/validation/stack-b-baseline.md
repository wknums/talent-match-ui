# Stack B baseline (T001)

Recorded 2026-10-02 on branch `001-sharepoint-bulk-scoring`, before any source change.

Command: `dotnet test dotnet\TalentMatch.slnx --nologo -v q` (full solution; includes the legacy upload and optional-upload suites named in T001).

| Project | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| Domain.Tests | 34 | 0 | 0 |
| Application.Tests | 190 | 0 | 0 |
| Infrastructure.Tests | 125 | 0 | 0 |
| Web.Tests | 237 | 0 | 0 |
| **Total** | **586** | **0** | **0** |

No pre-existing failures. Legacy suites covered: `UploadApplicationsCommandTests`, `UploadedApplicationPublicationTests`, `JobUsabilityComponentsTests`, `UploadSettingsPageTests`, `UploadSettingsEndpointsTests`, `OptionalUploadSessionTests`, `OptionalUploadPersistenceTests`.
