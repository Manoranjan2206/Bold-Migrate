# AGENTS.md — Bold Migrate Engineering Guidelines

This file defines the workspace architecture, build/test commands, invariants, and specialized agent skills for engineers and AI agents working on **Bold Migrate**.

## 1. Repository & Solution Structure

- `BoldMigrate.slnx` — .NET 10 (`net10.0`) solution:
  - `src/BoldMigrate.Core/` — Pure domain layer (models, enums, DTOs, interfaces, `ReportStateMachine`, `MigrationSettings`, `EventCodes`, `ErrorCatalogue`, `HostResolver`, `SqlConnectionHelper`). **Zero external dependencies.**
  - `src/BoldMigrate.Infrastructure/` — SQLite EF Core persistence (`MigrationDbContext`, `MigrationStoreRepository`), SSRS read-only adapters (`RestV2SourceAdapter`, `SoapSourceAdapter`, `SourceAdapterFactory`), staging (`FileSystemStagingStore`), credential vault (`InMemoryCredentialStore`). Depends on `Core`.
  - `src/BoldMigrate.Engine/` — Business logic (`MigrationOrchestrator`, `TransformEngine`, `CompatibilityRulesLoader`, `DependencyVerifier`, `StagedCatalog`, `UploadLayer`, `DestinationClient`, `RenderValidationEngine`, `ValidationEngine`, `DeltaSyncEngine`, `LocalFolderMigrationService`, `ExpressionTesterEngine`, `MigrationLogger`, `Redactor`, `MigrationMetrics`). Depends on `Core` + `Infrastructure`.
  - `src/BoldMigrate.Api/` — ASP.NET Core host (`Program.cs`), REST controllers, `SecurityMiddleware`, `UrlShimMiddleware`, local Bold Reports preview (`StagedReportServer`, `StagedReportSession`, `StagedReportRenderer`), `MigrationHealthChecks`, `FileLoggerProvider`, and vanilla HTML/CSS/JS SPA in `wwwroot/`.
  - `tests/` — xUnit test suites: `BoldMigrate.Core.Tests`, `BoldMigrate.Engine.Tests`, `BoldMigrate.Api.Tests`.
- `data/compatibility-rules.json` — Runtime compatibility rules loaded by `CompatibilityRulesLoader`.
- `docs/` — Authoritative documentation (`application-spec.md`, `api-reference.md`, `deployment-and-operations.md`, `developer-guide.md`, `decisions/`).

## 2. Build, Run & Test Commands

```powershell
dotnet build BoldMigrate.slnx
dotnet test
dotnet run --project src/BoldMigrate.Api/BoldMigrate.Api.csproj --urls http://localhost:5000
```

Before concluding any code change, run `dotnet test` and confirm zero failures across all three test projects.

## 3. Non-Negotiable Engineering Invariants

1. **Read-Only Source Server:** `ISourceCatalog` (`RestV2SourceAdapter`, `SoapSourceAdapter`) MUST NEVER issue write operations to SSRS/PBIRS.
2. **No Plaintext Secrets in DB or Logs:** Credentials go only through `ICredentialStore` (`InMemory` or `DpapiLocal`). Always sanitize errors, connection strings, tokens, and headers with `Redactor.RedactSecrets(...)` before logging.
3. **XXE Prevention & No Regex on XML:** Parse `.rdl`, `.rsd`, `.rsds`, `.rds`, and SOAP payloads with `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }` and `XmlResolver = null` (`System.Xml.Linq` / `XmlDocument`). Never rewrite RDL with regex.
4. **State Machine Integrity:** Transition `ReportRecord.State` via `ReportStateMachine.Transition(...)`. Never bypass illegal transition checks.
5. **Bold Reports JSON Escaping & Version Parity:**
   - Serialize base64 `ItemContent` payloads with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so `+` is preserved.
   - Keep `BoldReports.*` NuGet version (`15.1.17` in `BoldMigrate.Api.csproj`) aligned with the Bold Reports CDN script/CSS version in `wwwroot/index.html` and `wwwroot/preview-runner.html`.
6. **Security Boundaries:**
   - Frontend mutating calls MUST use `Api.request()` in `wwwroot/js/api.js` so `X-BoldMigrate-Csrf` is attached (`SecurityMiddleware`).
   - Escape all server/user strings with `escapeHtml(...)` before DOM `innerHTML` insertion.
   - Preserve path-traversal guards in `FileSystemStagingStore`, `LocalFolderMigrationService`, `LocalReportDesignerController`, and `UrlShimMiddleware.IsSafeRedirectDestination`.
7. **SQLite Schema Evolution:** `Program.cs` uses `EnsureCreated()` plus idempotent `pragma_table_info` / `ALTER TABLE` guards. Any new column on an existing entity requires a matching startup guard in `Program.cs`.
8. **Compatibility Rules & Public Contracts:** Every rule in `data/compatibility-rules.json` MUST include a non-empty `"article"` URL. Never repurpose existing `EventCodes` or `ErrorCatalogue` codes.

## 4. Workspace Skills & Specialist Agents

- **Skills (`.agents/skills/`):**
  - `boldmigrate-agent-orchestration` — Master orchestration playbook for coordinating specialist agents across backend, frontend, security/QA, and docs.
  - `boldmigrate-backend-pipeline` — Extending adapters, transform rules, dependency verifier, upload policies, Playwright validation, and SQLite schema.
  - `boldmigrate-frontend-ui` — Extending the 4-step SPA, `Api.request()` CSRF client, modals, visual diff, and Bold Reports Viewer/Designer widgets.
  - `boldmigrate-security-and-diagnostics` — Security middleware, redaction, path/redirect hardening, logging, health checks, and support bundles.
  - `boldmigrate-testing-and-release` — xUnit + `WebApplicationFactory` testing, compatibility rules validation, and multi-stage Playwright Docker builds.
- **Master & Specialist Agent Profiles (`.agents/agents/`):**
  - `boldmigrate-master-orchestrator.md` — **Master Engineering Orchestrator** (plans, freezes contracts, coordinates specialists, enforces QA/security & doc gates).
  - `backend-pipeline-engineer.md` — `Core`, `Infrastructure`, `Engine`, `Api` controllers, and `compatibility-rules.json`.
  - `frontend-ui-engineer.md` — `wwwroot/` SPA, CSS design tokens, modals, and Bold Reports Viewer/Designer integration.
  - `security-and-qa-reviewer.md` — Security invariants audit and xUnit / `WebApplicationFactory` test verification.
  - `docs-and-spec-maintainer.md` — `docs/`, ADRs, `README.md`, `AGENTS.md`, and `wwwroot/workflow-guide.html`.
