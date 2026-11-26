# ADR-003: In-Memory Default Credential Vault with Optional DPAPI / AES-GCM Persistence

## Status
Accepted

## Date
2026-11-26

## Context
Migrating SSRS estates requires handling sensitive secrets: SSRS source credentials, Bold Reports administrator passwords/Bearer tokens, and database passwords for shared data sources (which SSRS never exports via API). Storing these secrets in SQLite or plaintext config files would expose production database credentials if a database file or support bundle is shared.

## Decision
Never store secrets in SQLite (`MigrationDbContext`) or logs (`Redactor`). Store all runtime credentials in `InMemoryCredentialStore` (`ICredentialStore`), defaulting to volatile `CredentialMode.InMemory`. When an operator explicitly selects `CredentialMode.DpapiLocal` for multi-day runs, encrypt the vault payload on disk (`credentials.vault`) using:
- Windows DPAPI (`ProtectedData.Protect` with `DataProtectionScope.CurrentUser`) on Windows.
- `AesGcm` with a `0600`-permissioned 256-bit key file (`credentials.key`) on Linux/Docker.

Clear all credentials and delete any encrypted vault file on cutover or run abandonment (`ClearAll()`).

## Consequences
- Support bundles (`run.json`, `reports.json`, `audit-events.json`) and SQLite databases contain zero passwords or tokens.
- In default `InMemory` mode, restarting the Bold Migrate process requires re-entering credentials before resuming upload or delta sync.
- `CredentialMode.ExternalVault` remains reserved in the enum and explicitly returns `400 Bad Request` until a KMS/KeyVault provider is integrated.
