# ADR-001: Standalone ASP.NET Core Service with SQLite (WAL) Persistence

## Status
Accepted (Supersedes pre-implementation "module hosted inside Bold Reports Server" concept)

## Date
2026-11-26

## Context
The initial pre-implementation design envisioned Bold Migrate as an in-process module embedded directly inside Bold Reports Server. During implementation, Bold Migrate needed to:
- Ship and iterate independently of Bold Reports Server release cycles.
- Host headless Playwright Chromium (`Microsoft.Playwright`) and an isolated local `StagedReportServer` for pre-upload validation and editing.
- Connect to arbitrary customer Bold Reports targets (Docker, IIS, Cloud, multi-tenant sites) as well as offline local export folders.
- Run with zero external database setup in both local workstation and single-container Docker deployments.

## Decision
Build Bold Migrate as a standalone `.NET 10` ASP.NET Core web service (`BoldMigrate.Api`) serving a zero-build vanilla HTML/CSS/JS frontend (`wwwroot/`) and persisting run state, catalog items, validation results, URL mappings, and audit events in an embedded SQLite database (`MigrationDbContext`) configured with `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;`.

## Consequences
- **Independent deployment:** Engineers can deploy via `dotnet run` or a single Docker container (`Dockerfile` / `docker-compose.yml`) against any Bold Reports `v1.0`/`v5.0` endpoint.
- **External API integration:** `DestinationClient` authenticates over `/token` and handles endpoint variations across Bold Reports versions instead of calling internal server services.
- **Schema upgrades:** Because `EnsureCreated()` does not evolve existing SQLite files, `Program.cs` includes explicit startup `pragma_table_info` schema checks.
- **Container loopback support:** `HostResolver` translates `localhost`/`127.0.0.1` to `host.docker.internal` inside containers.
