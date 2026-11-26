# Bold Migrate — Developer Onboarding & Engineering Guide

## 1. Prerequisites
- **.NET 10 SDK** (`net10.0`)
- **PowerShell 5.1+** or `pwsh` (for optional Playwright script / `SSRSToBoldReportsMigration.ps1`)
- **Docker Desktop** (optional, for container builds)

---

## 2. Build, Run, and Test

```powershell
# Restore & build the full solution
dotnet build BoldMigrate.slnx

# Run all unit and API integration tests
dotnet test

# Run a single test project
dotnet test tests/BoldMigrate.Core.Tests/BoldMigrate.Core.Tests.csproj
dotnet test tests/BoldMigrate.Engine.Tests/BoldMigrate.Engine.Tests.csproj
dotnet test tests/BoldMigrate.Api.Tests/BoldMigrate.Api.Tests.csproj

# Launch the web application locally
dotnet run --project src/BoldMigrate.Api/BoldMigrate.Api.csproj --urls http://localhost:5000
```

---

## 3. Architectural Boundaries & Conventions

### Project Dependency DAG
- `BoldMigrate.Core` has **zero** project or external package dependencies. Keep it pure domain types, enums, interfaces, state machine, and static catalogues (`EventCodes`, `ErrorCatalogue`, `SqlConnectionHelper`, `HostResolver`).
- `BoldMigrate.Infrastructure` depends only on `BoldMigrate.Core`.
- `BoldMigrate.Engine` depends on `BoldMigrate.Core` and `BoldMigrate.Infrastructure`.
- `BoldMigrate.Api` wires DI, controllers, middleware, local Bold Reports preview, and static assets in `wwwroot/`.

### Key Implementation Rules
1. **Strict Read-Only Source Access:** Never add write/mutate calls to `ISourceCatalog`, `RestV2SourceAdapter`, or `SoapSourceAdapter`.
2. **XML & XXE Safety:** Always parse `.rdl`, `.rsd`, `.rsds`, `.rds`, and SOAP XML using `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }` and `XmlResolver = null`. Never rewrite RDL with regular expressions.
3. **State Transitions:** Always mutate `ReportRecord.State` via `ReportStateMachine.Transition(...)` when enforcing lifecycle transitions.
4. **Bold Reports JSON Escaping:** When serializing base64 `ItemContent` payloads for Bold Reports REST APIs, use `UnsafeRelaxedJsonEscaping` so `+` characters are not escaped to `\u002B`.
5. **Bold Reports SDK / CDN Parity:** `BoldMigrate.Api.csproj` references Bold Reports `15.1.17` packages (`BoldReports.AspNet.Core`, `BoldReports.Net.Core`, `BoldReports.Data.*`). Keep CDN URLs in `wwwroot/index.html` and `wwwroot/preview-runner.html` aligned with the exact same version.
6. **Secret Redaction:** Pass any exception message, connection string, or external output through `Redactor.RedactSecrets(...)` before logging or returning diagnostic details.
7. **Frontend Security & UX:**
   - Route all API calls through `Api.request()` in `wwwroot/js/api.js` so `X-BoldMigrate-Csrf` is attached automatically on mutating requests.
   - Escape all untrusted/server-returned strings with `escapeHtml(...)` before inserting into `innerHTML`.
   - Use `withButtonProgress(...)` / `beginActivity(...)` for async actions so the global activity bar and button states stay consistent.

---

## 4. Extending Common Areas

### Adding a Compatibility Rule
1. Add the rule entry to `data/compatibility-rules.json` (and mirror in `CompatibilityRulesLoader.GetFallbackRules()`).
2. Ensure `"article"` is a valid non-empty KB URL (`CompatibilityRulesLoader` rejects rules without an article).
3. Update `TransformEngine.TransformAndClassify` if a new `FeatureTags` mapping is desired, and add unit coverage in `tests/BoldMigrate.Engine.Tests/Transform/TransformEngineTests.cs`.

### Adding an API Endpoint
1. Add the action to the appropriate controller in `src/BoldMigrate.Api/Controllers/`.
2. Add the corresponding client wrapper to `src/BoldMigrate.Api/wwwroot/js/api.js` using `this.request(...)`.
3. Add an integration test in `tests/BoldMigrate.Api.Tests/ApiIntegrationTests.cs` using `BoldMigrateApiFactory`.

### Updating SQLite Schema
`Program.cs` uses `db.Database.EnsureCreated()` rather than EF Migrations, paired with idempotent startup `pragma_table_info` / `ALTER TABLE` checks. Any new nullable/defaulted column added to an existing entity must include an idempotent startup `ALTER TABLE` guard in `Program.cs` so existing `boldmigrate.db` files upgrade cleanly.
