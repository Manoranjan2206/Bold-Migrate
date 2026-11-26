# Bold Migrate — Deployment, Configuration & Operations Guide

## 1. Running Locally (.NET 10 SDK)

```powershell
dotnet run --project src/BoldMigrate.Api/BoldMigrate.Api.csproj --urls http://localhost:5000
```

Open `http://localhost:5000` in your browser.
- Compatibility Scanner: `http://localhost:5000/scanner.html`
- Workflow & Features Guide: `http://localhost:5000/workflow-guide.html`
- Health endpoint: `http://localhost:5000/health`

### Playwright Browser Dependency (Step 4 Automated Validation)
Step 4 uses `Microsoft.Playwright` (`1.65.1`) with headless Chromium. If Chromium is not already installed on the host, `RenderValidationEngine` attempts `Microsoft.Playwright.Program.Main(["install", "chromium"])` automatically on first run, or you can pre-install it via the generated Playwright script in `src/BoldMigrate.Api/bin/Debug/net10.0/playwright.ps1`:

```powershell
pwsh src/BoldMigrate.Api/bin/Debug/net10.0/playwright.ps1 install chromium
```

---

## 2. Running in Docker

The repository ships a multi-stage `Dockerfile` (`.NET 10 SDK` build -> `aspnet:10.0` runtime with `gss-ntlmssp`, `libgdiplus`, `fonts-liberation`, Chromium system dependencies, non-root `boldmigrate` user, and pre-installed Playwright Chromium) and `docker-compose.yml`:

```powershell
docker compose up --build -d
```

- Exposed host port: `8085` -> container `8080` (`http://localhost:8085`).
- Persistent volumes:
  - `boldmigrate_data` -> `/app/data` (`boldmigrate.db`, `settings.json`, optional encrypted `credentials.vault` / `credentials.key`)
  - `boldmigrate_staging` -> `/app/staging` (run-scoped staged definitions, `__originals/`, screenshots, `_designer-cache/`)
  - `boldmigrate_logs` -> `/app/logs` (`boldmigrate-yyyyMMdd.log`)
- **Container-to-Host Networking:** `HostResolver.Resolve()` automatically rewrites `localhost` and `127.0.0.1` URLs to `host.docker.internal` when `DOTNET_RUNNING_IN_CONTAINER=true` or `/.dockerenv` exists, allowing containerized Bold Migrate to reach host-hosted SSRS and Bold Reports instances.

---

## 3. Configuration (`BoldMigrate` Section)

Settings are bound from `appsettings.json`, environment variables (`BoldMigrate__...`), and overridden at runtime by `settings.json` in the resolved data directory (`SettingsController`).

| Key | JSON Property | Default | Bounds / Notes |
|---|---|---|---|
| `Enabled` | `BoldMigrate.Enabled` | `true` | Enables/disables module behavior |
| `StagingRootPath` | `Staging.RootPath` | `/migrations` (or `/app/staging` in Docker) | Base folder for run staging and `_designer-cache` |
| `StagingDeleteOnClose` | `Staging.DeleteOnClose` | `true` | Deletes run staging & `_designer-cache` on cutover/abandon |
| `DatabasePath` | `DatabasePath` | `/app/data/boldmigrate.db` (if `/app/data` exists) | SQLite database file path |
| `LogsPath` | `LogsPath` | `<base>/logs` | Daily `boldmigrate-yyyyMMdd.log` output directory |
| `ExtractionPreferredApi` | `Extraction.PreferredApi` | `Auto` | `Auto`, `RestV2`, or `Soap2010` |
| `ExtractionTimeoutSeconds` | `Extraction.TimeoutSeconds` | `100` | Clamped `30..600` |
| `ExtractionMaxParallel` | `Extraction.MaxParallel` | `4` | Clamped `1..16` |
| `TransformMaxAutoFixAttempts` | `Transform.MaxAutoFixAttempts` | `3` | Clamped `1..5` |
| `TransformRulesUrl` | `Transform.RulesUrl` | `rules.json` | Path/URL fallback; probes `data/compatibility-rules.json` upward |
| `TransformRulesRefreshHours` | `Transform.RulesRefreshHours` | `24` | Clamped `1..720` |
| `UploadMaxParallel` | `Upload.MaxParallel` | `4` | Clamped `1..16` |
| `UploadMaxAttempts` | `Upload.MaxAttempts` | `3` | Clamped `1..10` |
| `UploadConflictPolicy` | `Upload.ConflictPolicy` | `Skip` | `Skip`, `Overwrite`, or `Rename` |
| `UploadRetryBackoffSeconds` | `Upload.RetryBackoffSeconds` | `[2, 8, 30]` | Fixed retry intervals |
| `ValidationDataAccuracyThreshold` | `Validation.DataAccuracyThreshold` | `99.5` | Clamped `90.0..100.0` |
| `ValidationVisualSimilarityThreshold` | `Validation.VisualSimilarityThreshold` | `95.0` | Clamped `80.0..100.0` |
| `ValidationPixelTolerance` | `Validation.PixelTolerance` | `2` | Clamped `0..10` |
| `ValidationSampleRowLimit` | `Validation.SampleRowLimit` | `10000` | Clamped `100..1000000` |
| `ValidationMaxParallel` | `Validation.MaxParallel` | `2` | Clamped `1..8` |
| `ValidationRenderDpi` | `Validation.RenderDpi` | `96` | `96` or `150` |
| `PreviewAllowUntrustedSqlCert` | `Preview.AllowUntrustedSqlCert` | `false` | Appends `TrustServerCertificate=True` for non-loopback SQL preview/validation when enabled |
| `UrlShimEnabled` | `UrlShim.Enabled` | `false` | Enables `/ReportServer` & `/Reports/Pages/Report.aspx` redirection |
| `UrlShimListenPath` | `UrlShim.ListenPath` | `/ReportServer` | Cannot be `/`, `/api`, `/api/*`, or `/health` |
| `AllowedOrigins` | `AllowedOrigins` | `[]` | Empty = same-origin only (no wildcard CORS) |
| `AllowedLocalFolderRoots` | `AllowedLocalFolderRoots` | `[]` | Optional parent directory allowlist for Local Folder Import |
| `ApiAccessToken` | *(not serialized)* | `""` | Optional static Bearer / `X-Api-Key` token protecting `/api/*` |

---

## 4. Security Hardening Checklist

1. **Always terminate TLS (HTTPS)** in front of Bold Migrate when used outside `localhost`, especially when sending SSRS Basic credentials, SQL passwords, or Bold Reports credentials/tokens.
2. **Set `BoldMigrate__ApiAccessToken`** in shared or non-local environments so `/api/*` endpoints reject unauthenticated callers (`401`).
3. **Restrict `BoldMigrate__AllowedLocalFolderRoots`** in server deployments so Local Folder Import cannot read arbitrary host directories.
4. **Keep `Staging.DeleteOnClose` = `true`** and `InMemory` credential mode for single-sitting migrations so customer RDL definitions and secrets are cleared at cutover/abandon.
5. **Do not expose Bold Migrate publicly:** It has no built-in multi-user login UI; run it on a trusted administrative workstation or behind an authenticated reverse proxy.

---

## 5. Observability & Troubleshooting

### Health Check (`GET /health`)
`MigrationHealthChecks` checks:
- Orchestrator heartbeat age (`<= 300s`)
- SQLite store readability (`ListRunsAsync`)
- Staging volume free space (`>= 500 MB`)
- Compatibility rules load (`CompatibilityRulesLoader.LoadRules()`)

Degraded checks return `Degraded` (HTTP 200 in default ASP.NET Core configuration) with component metadata in `data`, never bringing down the host as `Unhealthy`.

### Error Catalogue Quick Reference (`ErrorCatalogue.cs`)
- `SRC-001`..`SRC-009`: Source SSRS unreachable, 401/403, Kerberos double-hop (`SRC-004`), SSRS 2016 `/api/v1.0` fallback (`SRC-005`), unsupported SSRS 2008 (`SRC-006`), Basic auth over plain HTTP blocked (`SRC-007`), custom security extension (`SRC-008`), throttling (`SRC-009`).
- `DST-001`..`DST-004`: Bold Reports 401/403/400/quota failures.
- `CRED-001`..`CRED-003`: Database connection test failure, unparseable connection string, vault failure.
- `XFRM-001`..`XFRM-004`: Invalid RDL XML, auto-fix exhausted, blocked rule fired, compatibility rules load failure.
- `VALD-001`..`VALD-005`: Source/destination render failure, page count mismatch, font gap, substituted parameter.
- `SYS-001`..`SYS-004`: Staging permission/disk errors, orchestrator crash recovery, concurrent run conflict.

### Support Bundle
Download `GET /api/diagnostics/runs/{id}/support-bundle` to obtain `run.json`, `reports.json`, and `audit-events.json` without any staged RDL files, rendered output, or credentials.
