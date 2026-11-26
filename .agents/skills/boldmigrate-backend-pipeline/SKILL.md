---
name: boldmigrate-backend-pipeline
description: >-
  Use this skill when modifying or extending the Bold Migrate C# backend pipeline:
  SSRS REST/SOAP source adapters, RDL XML transforms and compatibility rules,
  dependency verification, 4-stage upload and conflict policies, Playwright render
  validation, staged report viewer/designer, or SQLite schema.
---

# Bold Migrate Backend Pipeline Development

Follow these project-specific procedures when building or changing backend features in `BoldMigrate.Core`, `BoldMigrate.Infrastructure`, `BoldMigrate.Engine`, or `BoldMigrate.Api`.

## Architecture Boundaries

1. Keep `BoldMigrate.Core` dependency-free (`net10.0` BCL only). Place domain models, enums, DTOs, interfaces, `ReportStateMachine`, `MigrationSettings`, `EventCodes`, `ErrorCatalogue`, `HostResolver`, and `SqlConnectionHelper` here.
2. Keep `ISourceCatalog` (`RestV2SourceAdapter`, `SoapSourceAdapter`) strictly **read-only**. Never issue writes to SSRS/PBIRS.
3. Wrap host URLs passed to external HTTP clients with `HostResolver.Resolve(...)` so Docker containers reach host-bound `localhost` services via `host.docker.internal`.

## Extending Transform & Compatibility Rules

1. Parse XML exclusively with `System.Xml.Linq` (`XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }`) or `XmlDocument { XmlResolver = null }`. Never rewrite RDL using regex.
2. Add classification rules in [data/compatibility-rules.json](../../../data/compatibility-rules.json) and mirror defaults in `CompatibilityRulesLoader.GetFallbackRules()`.
3. Every rule **must** include a non-empty `"article"` KB URL; `CompatibilityRulesLoader.LoadRules()` throws `InvalidOperationException` if omitted.
4. Keep `TransformEngine` idempotent by reading unmodified originals from `IStagingStore.ResolveOriginalPath(...)` (`__originals/`) when re-checking an estate.

## Extending Upload & DestinationClient

1. `UploadLayer.UploadEstateAsync` runs 4 non-overlapping stages: `Category` (parent-before-child) -> `Datasource` -> `Dataset` -> `Report`.
2. Report uploads are gated via `UploadLayer.IsEligibleForUpload(record.State)` (`AutoPassed`, `Approved`, `Queued`, or `Uploaded` with `LocalEditPendingUpload`).
3. Always serialize Bold Reports JSON payloads containing base64 `ItemContent` using `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so `+` is not escaped to `\u002B`.
4. Support `ConflictResolutionPolicy` (`Skip`, `Overwrite`, `Rename`) and keep `PreviewUploadPlanAsync` (`POST .../upload/dry-run`) in sync with actual upload behavior.

## Extending Validation & Staged Preview

1. `StagedReportServer` resolves items via `StagedCatalog` (never join browser paths directly onto disk).
2. `StagedReportSession.SaveReportAsync` validates RDL XML (`ReportDependencies.IsReportDefinition`), overwrites staging, recomputes SHA-256, and sets `LocalEditUtc` + `LocalEditPendingUpload = true`.
3. `RenderValidationEngine` uses Playwright Chromium against `wwwroot/preview-runner.html`, classifying prompt-parameter reports (`NeedsManualCheck`) and SQL offline errors (`DataSourceOffline`) separately from RDL errors (`Failed`).

## Evolving the SQLite Schema

`Program.cs` calls `db.Database.EnsureCreated()` on startup rather than EF migrations. Whenever you add columns to an entity in `BoldMigrate.Core.Models`:
1. Add an idempotent `pragma_table_info` check + `ALTER TABLE` statement in `Program.cs` so existing `boldmigrate.db` files upgrade safely.
2. Remember SQLite drops `DateTimeKind`; specify `DateTimeKind.Utc` when returning timestamps to API clients.

## Verification

Run `dotnet test` after every backend change and ensure `Core.Tests`, `Engine.Tests`, and `Api.Tests` all pass.
