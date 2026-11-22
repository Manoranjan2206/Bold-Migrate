# Bold Migrate — SSRS to Bold Reports Enterprise Migration

Enterprise migration suite for moving Microsoft SQL Server Reporting Services (SSRS) and Power BI Report Server (PBIRS) estates to Bold Reports Server with automated extraction, transformation, upload, and side-by-side verification.

---

## What's New & Adopted Features

### 1. Adopted from `SSRSToBoldReportsMigration`
- **Local Folder Migration Service**: Direct ingestion and upload of `.rdl` reports, `.rsds` shared data sources, and `.rsd` shared datasets from local directories.
- **Recursive Directory & Category Tree Creation**: Automatically analyzes directory hierarchies and provisions corresponding nested categories in Bold Reports Server via `/api/site/{tenant}/v5.0/categories`.
- **DataSource Definition Mapping**: Parses XML `<DataSourceDefinition>` from `.rsds` files, mapping connection strings, credential retrieval modes, and server types (SQL, Oracle, MySQL, PostgreSQL, OLEDB, ODBC, etc.).
- **Shared DataSet Remapping**: Parses `<SharedDataSetDefinition>`, rebinds `DataSourceReference`, and registers published dataset lookups.
- **BOM Stripping & UnsafeRelaxedJsonEscaping**: Preserves base64 `+` characters in `ItemContent` during serialization to prevent 400 Bad Request errors on Bold Reports endpoints.
- **Automatic Backups & Summaries**: Creates backups of original `.rdl` files and outputs `MigrationLog.txt` and `MigrationResult.csv`.

### 2. Additional Details from `Migration Scripts`
- **PowerShell & Batch Launcher Parity**: Standalone batch launcher (`SSRSToBoldReportsMigration.bat`) and PowerShell migration script (`SSRSToBoldReportsMigration.ps1`) powered by `Configuration.json`.
- **Direct JWT Bearer Token Support**: Full authorization support for pre-issued Bold Reports JWT bearer tokens alongside administrator credentials.
- **Structured Export Layout**:
  - `Export/Reports`
  - `Export/Datasets`
  - `Export/Datasources`
  - `logs/` containing 6 dedicated phase logs: `preflight.log`, `export.log`, `datasources.log`, `datasets.log`, `reports.log`, and `reconciliation.log`.
- **Reconciliation & Inventory Artifacts**:
  - `inventory.csv`: Complete estate disposition ledger and summary counts.
  - `item-results.json`: Machine-readable results with timestamps, published item IDs, and stage traces.
  - `migration-summary.md`: Markdown summary report with execution statistics.
- **409 Conflict Handling**: Seamlessly skips existing data sources, datasets, and reports without failing incremental runs.

### 3. Layout Inspired by `Bold Migrate – UI Screens.html`
- **Enterprise Dark Slate Sidebar Navigation (`#0F172A`)**:
  - **D** **Dashboard**: Estate health at a glance, metric tiles, compatibility bars, live connection status, recent activity.
  - **L** **Report Library**: Searchable catalog table with filter chips (`All`, `Validated`, `Uploaded`, `Needs review`, `Blocked`), status pills, similarity scores, and inline `Compare` actions.
  - **C** **Connections**: 3-part credentials setup (SSRS login, Bold Reports login with Bearer Token or credentials, Data source credential vault).
  - **M** **Migration**: 4-stage upload tracker (Folders, DataSources, DataSets, Reports), overall progress bar, live log console, Pause/Resume controls.
  - **V** **Validation**: Interactive side-by-side comparison (SSRS vs Bold Reports Web Report Viewer), segmented view (`Side by side`, `Pixel diff`, `Data diff`), layout shift highlights, approve and flag actions.
  - **F** **Local Folder Import**: Direct GUI tool for local directory migrations.
  - **E** **AI Expression Tester**: Built-in expression builder & tester inspired by official Bold Reports tools.
  - **S** **Settings & Cutover**: Threshold controls, URL shim settings, production cutover gates, diagnostic support bundle export.
- **Landing Overview View**: Full-width architectural flow diagram (`SSRS -> Bold Migrate -> Bold Reports`), browser RDL dropzone scanner, and developer feature highlights.

### 4. AI SSRS Expression Tester (Inspired by Official Tool)
- **Inspired by:** [Bold Reports AI SSRS Expression Builder & Tester](https://www.boldreports.com/free-tools/ai-ssrs-expression-builder-tester/)
- **Syntax Compatibility Analysis**: Classifies expressions as `Compatible` (Green), `Needs Review` (Amber), or `Blocked` (Red).
- **Custom Assembly & Framework Detection**: Detects `CodeModule` or custom DLL calls that cannot execute in web environments.
- **Scoped Aggregate Insights**: Detects `Lookup`, `LookupSet`, `RunningValue`, and `Previous` with Bold Reports scoping advice.
- **Live Simulated Evaluation**: Evaluates expressions (`IIf`, `Sum`, `Format`, string manipulation, comparisons) against custom or mock Fields and Parameters JSON data.

---

## Getting Started

### 1. Run with .NET 10 SDK
```powershell
dotnet run --project src/BoldMigrate.Api/BoldMigrate.Api.csproj
```
Open **`http://localhost:5000`** in your browser.

### 2. Run PowerShell Migration Script
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\SSRSToBoldReportsMigration.ps1
```
Or launch via the batch wrapper:
```cmd
SSRSToBoldReportsMigration.bat
```

### 3. Run Automated Tests
```powershell
dotnet test
```

---

## REST API Endpoints

- `POST /api/migration/runs`: Create or get active migration session.
- `POST /api/migration/runs/{id}/connect`: Test connection to SSRS server.
- `POST /api/migration/runs/{id}/connect-destination`: Test Bold Reports login (via credentials or Bearer token).
- `POST /api/migration/runs/{id}/fetch`: Discover source estate and stage definitions.
- `POST /api/migration/runs/{id}/check`: Run transformation and compatibility classification.
- `POST /api/migration/runs/{id}/upload`: Execute 4-stage dependency upload.
- `POST /api/migration/runs/{id}/validate`: Run font calibration and visual/data comparison.
- `POST /api/migration/local-folder`: Execute local folder migration (RDL, RSDS, RSD).
- `GET /api/migration/local-folder/logs`: Fetch live streaming logs for local folder migration.
- `POST /api/migration/expression/evaluate`: Analyze and test an SSRS expression.
- `GET /api/migration/expression/samples`: Retrieve sample SSRS expressions.
- `GET /api/diagnostics/runs/{id}/support-bundle`: Download diagnostic support bundle ZIP.
