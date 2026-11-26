---
name: boldmigrate-testing-and-release
description: >-
  Use this skill when writing unit or integration tests, validating compatibility
  rules, building the Bold Migrate Docker image with Playwright Chromium, or
  running release readiness checks.
---

# Bold Migrate Testing & Release

## Test Projects

1. **`tests/BoldMigrate.Core.Tests/`**
   - Tests `ReportStateMachine` legal/illegal transitions, `ErrorCatalogue` & `EventCodes`, `HostResolver`, `SqlConnectionHelper`, and `MigrationSettings.ValidateAndNormalize`.
2. **`tests/BoldMigrate.Engine.Tests/`**
   - Tests `TransformEngine`, `CompatibilityRulesLoader`, `Redactor`, `DependencyVerifier`, `StagedCatalog`, `SharedDataSourceDefinition`, `ReportDependencies`, `UploadLayer` (idempotency keys, gating, conflict policies, dry-run), `DataComparer`, `PdfStructureComparer`, `LocalFolderMigrationService`, and `ExpressionTesterEngine`.
3. **`tests/BoldMigrate.Api.Tests/`**
   - Uses `BoldMigrateApiFactory : WebApplicationFactory<Program>` with isolated temp SQLite DB, staging, and logs directories.
   - Verifies `SecurityMiddleware` (headers, CSRF, `ApiAccessToken`), `SettingsController`, `ScannerController`, `UrlShimMiddleware` redirect safety, `MigrationController` endpoints, and `BoldDeleteToolController`.

## Commands

```powershell
# Run full test suite
dotnet test

# Run with verbose output for a specific filter
dotnet test --filter "FullyQualifiedName~ApiIntegrationTests"

# Build Release & publish check
dotnet publish src/BoldMigrate.Api/BoldMigrate.Api.csproj -c Release -o ./publish_check /p:UseAppHost=false
```

## Docker & Playwright Verification

- `Dockerfile` uses a two-stage `.NET 10` build (`sdk:10.0` -> `aspnet:10.0`), installs `gss-ntlmssp`, `libgdiplus`, `fonts-liberation`, and Chromium runtime libs, installs Playwright Chromium into `/ms-playwright`, and runs as non-root user `boldmigrate` on port `8080`.
- `docker-compose.yml` maps `8085:8080`, mounts `boldmigrate_data`, `boldmigrate_staging`, and `boldmigrate_logs`, and runs a `/health` check.
- When updating `Microsoft.Playwright` or `BoldReports.*` NuGet packages, verify `Dockerfile` Playwright installation paths and `wwwroot/` CDN URLs match.

## Release Checklist

- [ ] `dotnet test` passes with zero failures across all 3 test projects.
- [ ] `data/compatibility-rules.json` parses cleanly and every rule has a valid `article` URL.
- [ ] `docs/application-spec.md`, `docs/api-reference.md`, and `wwwroot/workflow-guide.html` reflect any new endpoints, settings, or UI steps.
