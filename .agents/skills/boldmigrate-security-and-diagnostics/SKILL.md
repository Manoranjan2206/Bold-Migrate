---
name: boldmigrate-security-and-diagnostics
description: >-
  Use this skill when auditing or modifying Bold Migrate security controls,
  credential storage, secret redaction, path/redirect validation, URL shim
  behavior, structured logging, health checks, or diagnostic support bundles.
---

# Bold Migrate Security & Diagnostics

## Security Controls Checklist

1. **Middleware (`SecurityMiddleware.cs`):**
   - Emits `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`, `Referrer-Policy: no-referrer`, `Permissions-Policy`.
   - Enforces `BoldMigrate:ApiAccessToken` (`Bearer`, `X-Api-Key`, `access_token`) with `CryptographicOperations.FixedTimeEquals`.
   - Enforces double-submit CSRF (`bm_csrf` cookie + `X-BoldMigrate-Csrf` header) on browser mutating `/api/*` requests; exempts only `/api/preview/`, `/api/localreportviewer/`, `/api/localreportdesigner/` (which still validate same-origin).
2. **Credential Storage (`InMemoryCredentialStore.cs`):**
   - Default mode is `InMemory` (cleared on cutover/abandon via `ClearAll()`).
   - `DpapiLocal` uses Windows DPAPI (`DataProtectionScope.CurrentUser`) or Linux `AesGcm` (`0600` key file).
   - Never persist secrets to SQLite (`MigrationDbContext`) or expose raw Bearer JWTs over `GET` endpoints (e.g. `GetDesignerConfig` returns `hasAuthorizationToken`, never the token).
3. **Secret Redaction (`Redactor.cs`):**
   - Always sanitize connection strings with `Redactor.RedactConnectionString(...)` and exceptions/messages with `Redactor.RedactSecrets(...)` before logging or UI responses.
   - `FileLoggerProvider` automatically runs `Redactor.RedactSecrets` on every log line and exception.
4. **Path & Redirect Guards:**
   - `FileSystemStagingStore.ResolveSafeRunFilePath` and `LocalReportDesignerController.CacheFilePath` block `..` traversal and enforce canonical directory prefixes.
   - `LocalFolderMigrationService.ValidateFolderPath` rejects `..` and enforces `AllowedLocalFolderRoots` when configured.
   - `UrlShimMiddleware.IsSafeRedirectDestination` allows only safe relative paths (not `//` or `/\`) or exact origin (`Scheme` + `Host` + `Port`) matching `DestinationServerUrl`.
5. **SQL TLS & Host Checks (`SqlConnectionHelper.cs`):**
   - Auto-trusts SQL TLS certs only for loopback/local hosts unless `Preview.AllowUntrustedSqlCert` or explicit per-request `trustServerCertificate` is enabled.

## Diagnostics, Event Codes & Health

- Add new operational events to `EventCodes.cs` and user-facing three-part errors to `ErrorCatalogue.cs` without altering existing code meanings.
- Keep `GET /health` (`MigrationHealthChecks`) returning `Healthy` or `Degraded` (never `Unhealthy`).
- Keep `GET /api/diagnostics/runs/{id}/support-bundle` free of staged RDL files, rendered output, credentials, and connection strings.
