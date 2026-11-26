---
name: boldmigrate-frontend-ui
description: >-
  Use this skill when building or modifying the Bold Migrate browser UI in
  src/BoldMigrate.Api/wwwroot/: the 6-step migration console, standalone tools,
  Api client, CSS design tokens, modals, visual comparison slider/pixel-diff, or
  embedded Bold Reports Web Viewer and Designer.
---

# Bold Migrate Frontend UI Development

The frontend is a zero-build vanilla HTML + CSS + JavaScript single-page application served from `src/BoldMigrate.Api/wwwroot/`.

## File Map

- `index.html` — Main 6-step console + Standalone Tools (`Local Folder Import`, `Bulk Delete Reports`, `Settings & Rules`) + modals.
- `scanner.html` + `js/scanner.js` — Standalone client-side `.rdl` compatibility scanner.
- `preview-runner.html` — Minimal host page driven by Playwright in Step 4 (`data-render-status`).
- `workflow-guide.html` — In-app architecture and workflow reference page.
- `css/tokens.css` & `css/app.css` — Design tokens (`Geist` / `Geist Mono`, `#FF4800` accent, `#020202` primary, `#0D1420` dark sidebar) and component styles.
- `js/api.js` — `Api` fetch wrapper handling CSRF headers.
- `js/app.js` — Shell initialization, `JOURNEY_STEPS`, `switchTab`, `startPoller`, `beginActivity` / `withButtonProgress`, `escapeHtml`.
- `js/connections.js` — Step 1 connections, data source vault, SQL TLS trust toggle, bulk host/DB remap.
- `js/library.js` — Step 2 report library + Step 3 assessment dashboard & dependency link verification.
- `js/validation.js` — Step 4 Playwright render validation grid/table, filters, bulk approve, RDL diff.
- `js/migration.js` — Step 5 upload pipeline, conflict policy & dry-run, live log + Step 6 cutover gates, delta sync, URL shim.
- `js/modals.js` — Accessible confirm/alert dialogs, delete modal, viewer/designer modals, visual swipe slider & canvas pixel-diff.
- `js/tools.js` — Local Folder Import, Settings & Rules, landing dropzone scanner.
- `js/bolddelete.js` — Standalone multi-type Bulk Delete UI.

## Required Patterns

1. **CSRF & API Calls:** Add new endpoint wrappers to `Api` in `js/api.js` and always call `this.request(...)` so `X-BoldMigrate-Csrf` is sent on `POST`/`PUT`/`PATCH`/`DELETE` requests.
2. **XSS Prevention:** Always wrap dynamic values in `escapeHtml(...)` (or `escapeScannerHtml` in `scanner.js`) before rendering HTML templates. Prefer `data-*-action` event delegation over inline `onclick` string interpolation.
3. **Progress & Loading UX:** Wrap long user actions with `withButtonProgress(btnOrId, loadingLabel, activityTitle, fn, detail)` so both the clicked button and `#global-activity-bar` update consistently.
4. **Bold Reports CDN Parity:** Keep `https://cdn.boldreports.com/15.1.17/...` in `index.html` and `preview-runner.html` matched to the `BoldReports.*` package version in `BoldMigrate.Api.csproj`.
5. **Cache Busting & Sync:** Bump `?v=...` query strings on edited JS/CSS `<script>`/`<link>` tags in `index.html`, and update `workflow-guide.html` if step behavior or routes change.
6. **Status Accessibility:** Never convey state with color alone; always pair status pills/dots with text labels.
