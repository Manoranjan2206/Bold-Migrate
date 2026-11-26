# ADR-002: Local Staged Preview & Headless Playwright Validation Before Upload

## Status
Accepted

## Date
2026-11-26

## Context
Uploading untested reports directly to a live Bold Reports Server pollutes the destination catalog with broken definitions, forces repeated delete/re-upload cycles, and makes iterative RDL fixes slow. Operators need to catch parameter prompts, RDL syntax/binding errors, and layout issues before anything is pushed to Bold Reports Server.

## Decision
Implement Step 4 (**Validation & Preview**) ahead of Step 5 (**Migration Pipeline / Upload**) using two complementary mechanisms:
1. **`StagedReportServer` (`ReportingServer` subclass):** Serves staged `.rdl`, `.rsds`/`.rds`, `.rsd`, and resource files directly from `FileSystemStagingStore` + `StagedCatalog` to `LocalReportViewerController`, `LocalReportDesignerController`, and `StagedReportRenderer`. Designer edits save back into staging (`LocalEditPendingUpload = true`) without touching SSRS or Bold Reports Server.
2. **`RenderValidationEngine` (Headless Playwright Chromium):** Renders staged reports in parallel (default concurrency 5) via `preview-runner.html`, classifies prompt-parameter reports (`NeedsManualCheck`) and offline-DB reports (`DataSourceOffline`) separately from true RDL render failures (`Failed`), captures PNG screenshots, and pairs them with a client-side Visual Comparison Swipe Slider & Pixel-Diff modal.

## Consequences
- Operators verify and edit reports locally before mutating the destination server.
- Step 5 (`UploadLayer`) enforces upload gating (`IsEligibleForUpload`), skipping unvalidated/blocked reports and pushing `LocalEditPendingUpload` updates via `PUT /v1.0/reports`.
- Docker runtime requires Chromium system libraries and `PLAYWRIGHT_BROWSERS_PATH=/ms-playwright` (provisioned in `Dockerfile`).
