# Bold Migrate — REST API Reference

Base URL: `http://localhost:5000` (dev) or `http://localhost:8080` (Docker container; mapped to `8085` in `docker-compose.yml`).

## Authentication & CSRF Rules

1. **Optional Operator Token (`BoldMigrate:ApiAccessToken`):** When configured, all `/api/*` requests must supply one of:
   - `Authorization: Bearer <token>`
   - `X-Api-Key: <token>`
   - `?access_token=<token>`
2. **Browser CSRF Protection (`SecurityMiddleware`):** Mutating `/api/*` requests (`POST`, `PUT`, `PATCH`, `DELETE`) from a browser (`Origin` or `Sec-Fetch-Site` header present) must include the `bm_csrf` cookie and matching `X-BoldMigrate-Csrf` header (automatically handled by `Api.request()` in `wwwroot/js/api.js`). Viewer/designer internal endpoints (`/api/preview/*`, `/api/localreportviewer/*`, `/api/localreportdesigner/*`) are CSRF-exempt and rely on same-origin checks.

---

## Migration Lifecycle (`/api/migration`)

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/migration/runs` | Create or return the active run. Body: `{ name, sourceUrl, forceNew }`. Setting `forceNew: true` abandons any active run first. |
| `GET` | `/api/migration/active-run` | Return the current active (non-`Abandoned`, non-`CutOver`) `MigrationRun`, or `null`/empty if none. |
| `GET` | `/api/migration/runs/{id}` | Get a specific `MigrationRun` by GUID. |
| `PUT` | `/api/migration/runs/{id}/credential-mode` | Switch credential vault mode (`InMemory` or `DpapiLocal`). `ExternalVault` returns `400`. |
| `POST` | `/api/migration/runs/{id}/connect` | Probe and connect source SSRS/PBIRS server (`Integrated`, `WindowsExplicit`, `Basic` over HTTPS). Body: `{ sourceUrl, username, password, authMode, credentialMode }`. |
| `POST` | `/api/migration/runs/{id}/connect-destination` | Authenticate to Bold Reports Server via `{ destinationUrl, email, password }` or `{ destinationUrl, bearerToken }`. |
| `POST` | `/api/migration/runs/{id}/fetch` | Start background discovery and staging of SSRS items (`202 Accepted`). |
| `POST` | `/api/migration/runs/{id}/check` | Start background `TransformEngine` rewrite and classification (`202 Accepted`). |
| `GET` | `/api/migration/runs/{id}/poll` | Poll run progress, item counts, validation counts, and compatibility bucket totals. |
| `GET` | `/api/migration/runs/{id}/log` | Return in-memory live log entries (`LiveLogEntry[]`) for the run. |
| `POST` | `/api/migration/runs/{id}/abandon` | Abandon the run, delete staging + `_designer-cache`, and clear credentials. |

### Catalog, Data Sources & Dependencies

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/migration/runs/{id}/estate?skip=0&take=100` | Paginated catalog items (`take` clamped `1..1000`) and type summary counts. |
| `GET` | `/api/migration/runs/{id}/datasources` | List staged shared data sources with redacted connection strings and credential status. |
| `PUT` | `/api/migration/runs/{id}/datasources/{dataSourceId}/credentials` | Save credential retrieval mode, optional credentials, and connection string to vault and `.rsds` XML. |
| `POST` | `/api/migration/runs/{id}/datasources/{dataSourceId}/validate-and-save` | Test SQL Server connectivity live (with optional `trustServerCertificate`) and persist on success. Returns `certUntrusted: true` on untrusted TLS errors. |
| `POST` | `/api/migration/runs/{id}/datasources/bulk-remap` | Preview (`dryRun: true`) or apply host, database, raw string, credential mode, username/password, and TLS trust updates across staged `.rsds` files. |
| `GET` | `/api/migration/runs/{id}/reports` | List all reports with state, bucket, `blockReason`, `featureTags`, `parameterCount`, and `uploadAttempts`. |
| `POST` | `/api/migration/runs/{id}/select-reports` | Persist selected report IDs (`{ selectedReportIds: Guid[] }`); unselected non-blocked reports become `Excluded`. |
| `GET` | `/api/migration/runs/{id}/dependencies/verify` | Verify all `DataSourceReference`, `SharedDataSetReference` (including chained DS), and `Subreport` links plus cycle detection (`DependencyVerificationSummary`). |
| `POST` | `/api/migration/runs/{id}/reports/{reportId}/rebind-reference` | Rewrite a broken `DataSource`, `DataSet`, or `Subreport` reference in staged RDL XML. Body: `{ referenceType, oldReference, newReference }`. |
| `GET` | `/api/migration/runs/{id}/permissions` | List discovered SSRS `PermissionMapping` records. |
| `GET` | `/api/migration/runs/{id}/subscriptions` | List discovered SSRS `SubscriptionRecord` inventory. |

### Validation, Preview & Review

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/migration/runs/{id}/validate` | Start background `ValidationEngine` CSV/PDF comparison against live SSRS (`202 Accepted`). |
| `POST` | `/api/migration/runs/{id}/validate-render?concurrency=5` | Start background headless Playwright render validation (`RenderValidationEngine`) for all or `{ reportIds }` (`202 Accepted`). |
| `POST` | `/api/migration/runs/{id}/skip-validation` | Mark non-blocked reports `AutoPassed` (`Skipped`) to allow immediate upload. |
| `GET` | `/api/migration/runs/{id}/validation-summary` | Return card/table summary counts (`passed`, `dataSourceOffline`, `failed`, `needsManualCheck`, `skipped`) and per-report statuses. |
| `DELETE` | `/api/migration/runs/{id}/validation-results` | Clear stored `ValidationResults` and cached PNG screenshots, resetting validated states to `Analysed`. |
| `GET` | `/api/migration/runs/{id}/reports/{reportId}/validation` | Return latest measured `ValidationResult` or `{ validated: false }`. |
| `GET` | `/api/migration/runs/{id}/reports/{reportId}/rdl` | Return `originalRdl` (`__originals/`) and `transformedRdl` (staged) XML for diff inspection. |
| `GET` | `/api/migration/runs/{id}/screenshots/{reportId}.png` | Serve captured Playwright local preview PNG. |
| `GET` | `/api/migration/runs/{id}/screenshots/{reportId}/source.png?refresh=false` | Fetch/cache and serve SSRS `IMAGE` (PNG) render for visual comparison. |
| `GET` | `/api/migration/runs/{id}/preview/reports` | List staged reports and resolved dependencies with local viewer/designer service URLs. |
| `GET` | `/api/migration/runs/{id}/designer-config` | Return Bold Reports server designer URLs and `hasAuthorizationToken` (never leaks raw JWT). |
| `GET` | `/api/migration/runs/{id}/review-queue` | List reports in `AwaitingReview` state. |
| `POST` | `/api/migration/runs/{id}/reports/{reportId}/approve` | Approve report and record `Approval` audit entry. |
| `POST` | `/api/migration/runs/{id}/reports/{reportId}/flag` | Flag report (`ReportState.Flagged`) for designer editing. |
| `POST` | `/api/migration/runs/{id}/reports/bulk-approve?minAccuracy=99.0` | Sequentially approve `AwaitingReview` reports until first report below `minAccuracy`. |

### Upload, Destination Management & Cutover

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/migration/runs/{id}/upload/dry-run?conflictPolicy=Skip` | Scan destination items and return an `UploadDryRunPlan` (`Create`, `Skip`, `Overwrite`, `Rename`, `Blocked`). |
| `POST` | `/api/migration/runs/{id}/upload?action=&conflictPolicy=` | Start 4-stage upload (`202 Accepted`), or pass `action=pause` / `action=resume` (`200 OK`). |
| `POST` | `/api/migration/runs/{id}/retry` | Re-queue `UploadFailed` reports and re-run upload (`202 Accepted`). |
| `GET` | `/api/migration/runs/{id}/destination/sections` | Group run reports by folder and compare with live Bold Reports items. |
| `POST` | `/api/migration/runs/{id}/destination/delete-reports` | Delete run reports (`reportIds`, `sections`, or `deleteAll`, plus optional `deleteCategories`) from Bold Reports and reset local state to `Analysed`. |
| `POST` | `/api/migration/runs/{id}/delta-sync` | Run incremental `DeltaSyncEngine` check against SSRS. |
| `GET` | `/api/migration/runs/{id}/cutover` | Evaluate 5 blocking gates + 1 advisory shim gate (`{ ready, gates, gateCount, advisoryGates }`). |
| `POST` | `/api/migration/runs/{id}/cutover` | Finalize cutover (`RunStatus.CutOver`), clean up staging, and clear credentials. Requires at least 1 uploaded report. |

---

## Local Preview Viewer & Designer (`/api/preview/runs/{runId}`)

- **Viewer (`LocalReportViewerController`):**
  - `POST /api/preview/runs/{runId}/viewer/PostReportAction`
  - `POST /api/preview/runs/{runId}/viewer/PostFormReportAction`
  - `GET /api/preview/runs/{runId}/viewer/GetResource`
  - `GET /api/preview/runs/{runId}/viewer/render-logs?reportPath=...`
- **Designer (`LocalReportDesignerController`):**
  - `POST /api/preview/runs/{runId}/designer/PostDesignerAction`
  - `POST /api/preview/runs/{runId}/designer/PostFormDesignerAction`
  - `POST /api/preview/runs/{runId}/designer/UploadReportAction`
  - `POST /api/preview/runs/{runId}/designer/PostReportAction`
  - `POST /api/preview/runs/{runId}/designer/PostFormReportAction`
  - `GET /api/preview/runs/{runId}/designer/GetResource`
  - `GET /api/preview/runs/{runId}/designer/GetImage`

---

## Standalone Tools, Diagnostics & Operations

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/migration/local-folder` | Execute local folder migration (`LocalFolderMigrationRequest`). |
| `GET` | `/api/migration/local-folder/logs` | Stream current logs from `LocalFolderMigrationService`. |
| `POST` | `/api/migration/expression/evaluate` | Analyze and mock-evaluate an SSRS expression (`ExpressionTestRequest`). |
| `GET` | `/api/migration/expression/samples` | Return sample SSRS expressions. |
| `POST` | `/api/tools/bold-delete/list` | Connect per-request (`{ url, email, password }`) and list `Report`, `Dataset`, `Datasource`, and `Folder` items. |
| `POST` | `/api/tools/bold-delete/delete` | Delete selected `items: [{ id, itemType }]` (or legacy `reportIds`) in dependency order (`Report -> Dataset -> Datasource -> Folder`). |
| `POST` | `/api/scanner/analyse` | Multipart upload (`.rdl`, `.rdlc`, `.xml`, max 25 MB) for server-side `TransformEngine` compatibility analysis. |
| `GET` | `/api/settings` | Return current `MigrationSettings` (serialized with dotted `JsonPropertyName` keys). |
| `PUT` | `/api/settings` | Validate, apply, and persist mutable settings to `settings.json`. |
| `GET` | `/api/settings/rules` | Return active compatibility rules from `CompatibilityRulesLoader`. |
| `GET` | `/api/diagnostics/runs/{id}/support-bundle` | Download ZIP archive (`run.json`, `reports.json`, `audit-events.json`). |
| `GET` | `/api/diagnostics/runs/{id}/report-pdf` | Download plain-text audit sign-off summary (`migration-report-{id}.txt`). |
| `GET` | `/health` | ASP.NET Core health check (`Healthy` or `Degraded`). |
| `GET` | `/ReportServer?...` / `/Reports/Pages/Report.aspx?ItemPath=...` | `UrlShimMiddleware` redirector (when `UrlShim.Enabled` is `true`). |
