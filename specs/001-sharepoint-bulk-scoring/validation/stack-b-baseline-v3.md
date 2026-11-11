# Stack B v3.1 baseline (T002)

Recorded 2026-10-13 with .NET SDK 10.0.401.

```text
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~UploadApplicationsCommandTests|FullyQualifiedName~OptionalUploadSessionTests|FullyQualifiedName~UploadSettingsTests|FullyQualifiedName~OptionalUploadEndpointsTests|FullyQualifiedName~UploadSettingsEndpointsTests|FullyQualifiedName~UploadSettingsPageTests"
```

Result: **PASS** (exit code 0).

| Test assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `TalentMatch.Application.Tests` | 24 | 0 | 0 |
| `TalentMatch.Web.Tests` | 16 | 0 | 0 |

The Domain and Infrastructure test projects contained no tests matching the exact filter. Restore,
build, and both matching test assemblies completed successfully.
