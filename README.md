# Bold Migrate — SSRS to Bold Reports Enterprise Migration

Enterprise migration suite for moving Microsoft SQL Server Reporting Services (SSRS 2012–2022) and Power BI Report Server (PBIRS) estates—or offline local report directories—to Bold Reports Server with automated extraction, RDL transformation, local Playwright & visual validation, dependency-ordered upload, and gated production cutover.

---

## Key Capabilities

### 1. Guided 4-Step Migration Journey

The console presents four milestones. Discovery and compatibility assessment both complete in
Step 1; Step 2 is where the operator chooses the migration scope, not where work happens.

| Step | Name | What it does |
|---|---|---|
| 1 | **Source & Target** | Connect SSRS/PBIRS and Bold Reports, discover, stage and assess the catalog |
| 2 | **Assess & Resolve** | Review readiness, verify links, and choose which reports to migrate |
| 3 | **Visual Verify** | Playwright render validation, local viewer/designer, XML, visual and data diff |
| 4 | **Publish & Cutover** | 4-stage dependency upload, readiness gates, delta sync and URL shim |

Detail by capability:
1. **Connect & Discover:** Auto-probes SSRS/PBIRS via REST API v2.0 with SOAP `ReportingService2010` fallback (`Windows Integrated`, `WindowsExplicit`, or HTTPS `Basic`), authenticates to Bold Reports Server (admin credentials or direct Bearer JWT token), discovers and stages `.rdl`/`.rsds`/`.rds`/`.rsd` items locally, and provides **Bulk Host / Database Remapping** plus live SQL Server credential validation (with `TrustServerCertificate` support).
2. **Report Library:** Searchable, filterable catalog (`All`, `Compatible`, `Needs Review`) with multi-select scoping, compatibility findings, and run-scoped destination cleanup.
3. **Assessment Dashboard:** Readiness score, compatibility breakdown against `data/compatibility-rules.json`, and **Subreport / Shared DataSet / Shared Data Source Link Verification** with cycle detection and one-click XML reference rebinding.
4. **Validation & Preview:** Automated headless **Playwright Chromium** render validation (5 concurrent tabs), local **Bold Reports Viewer & Web Designer (`15.1.17`)** backed by `StagedReportServer` (edits save back to staging before upload), side-by-side **RDL XML Diff**, and **Visual Comparison** (Swipe Slider, Side-by-Side, and Canvas Pixel-Diff).
5. **Migration Pipeline:** Strict 4-stage dependency upload (`Folders -> DataSources -> DataSets -> Reports`) with configurable conflict policies (`Skip`, `Overwrite`, `Rename`), **Pre-Flight Dry-Run**, idempotency keys, live execution logs, pause/resume, retry failed, and server-integrated report launch.
6. **Cutover & Audit:** 5 blocking readiness gates + advisory URL shim gate, incremental **Delta Sync**, transparent **URL Compatibility Shim** (`/ReportServer` & `/Reports/Pages/Report.aspx`), **Audit Run Report**, and **Diagnostic Support Bundle (ZIP)**.

### 2. Standalone Tools
- **Local Folder Import:** Direct ingestion of local `.rdl`, `.rsds`/`.rds`, and `.rsd` directories with recursive nested Bold Reports category creation, BOM stripping, `UnsafeRelaxedJsonEscaping`, RDL backups, phase logs, `MigrationResult.csv`, `inventory.csv`, `item-results.json`, and `migration-summary.md`.
- **Bulk Delete Items:** Credential-less-storage standalone cleanup tool for **Reports, Datasets, Data Sources, and Folders** on any Bold Reports Server, deleting in safe dependency order (`Report -> Dataset -> Datasource -> Folder`).
- **Offline Compatibility Scanner (`/scanner.html`):** 100% client-side in-browser RDL compatibility scanner (plus `/api/scanner/analyse` endpoint for server-side `TransformEngine` scanning).
- **Settings & Rules:** Runtime configuration persisted to `settings.json` and active compatibility rule inspection.
- **PowerShell Script Parity (`SSRSToBoldReportsMigration.ps1` / `.bat`):** Standalone PowerShell migration runner configured via `Configuration.json`.

---

## Getting Started

### 1. Run with .NET 10 SDK
```powershell
dotnet run --project src/BoldMigrate.Api/BoldMigrate.Api.csproj --urls http://localhost:5000
```
Open **`http://localhost:5000`** in your browser (Workflow Guide: `/workflow-guide.html`, Offline Scanner: `/scanner.html`, Health: `/health`).

### 2. Run with Docker Compose
```powershell
docker compose up --build -d
```
Open **`http://localhost:8085`** in your browser.

### 3. Run Automated Tests
```powershell
dotnet test
```

### 4. Run PowerShell Migration Script
See [SSRSToBoldReportsMigration.md](SSRSToBoldReportsMigration.md) for prerequisites and [Configuration.json](Configuration.json) setup:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\SSRSToBoldReportsMigration.ps1
```

---

## Documentation & Engineering Guides

- **[Application & System Specification](docs/application-spec.md)** — Authoritative specification of the implemented system, architecture, state machines, and features.
- **[REST API Reference](docs/api-reference.md)** — Complete endpoint, payload, CSRF, and authentication reference.
- **[Deployment, Configuration & Operations](docs/deployment-and-operations.md)** — Configuration bounds, Docker, Playwright setup, security hardening, health checks, and error catalogue.
- **[Developer Onboarding Guide](docs/developer-guide.md)** — Solution DAG, coding invariants, schema upgrades, and extension workflows.
- **[Architecture Decision Records](docs/decisions/)** — Key technical decisions (`ADR-001` through `ADR-003`).
- **[Agent & Engineer Workspace Instructions](AGENTS.md)** — Repository rules and `.agents/skills/` for engineers and AI agents.
