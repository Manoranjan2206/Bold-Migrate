---
name: boldmigrate-agent-orchestration
description: >-
  Use this skill when acting as the Master Orchestrator (or coordinating multiple
  specialist agents) on Bold Migrate tasks spanning backend, frontend, security/QA,
  and documentation. Defines delegation matrices, contract freezing, parallel
  execution rules, handoff prompts, and verification gates.
---

# Bold Migrate Multi-Agent Orchestration Playbook

This skill governs how the **Master Orchestrator** (`boldmigrate-master-orchestrator`) manages the four Bold Migrate specialist agents:
- `backend-pipeline-engineer`
- `frontend-ui-engineer`
- `security-and-qa-reviewer`
- `docs-and-spec-maintainer`

---

## 1. Quick Routing Matrix

| Task Type | Agents Invoked | Execution Order |
|---|---|---|
| Full-stack feature (new API + UI + tests + docs) | `backend-pipeline-engineer`, `frontend-ui-engineer`, `security-and-qa-reviewer`, `docs-and-spec-maintainer` | `(Backend ∥ Frontend) -> Security/QA -> Docs` |
| Backend-only feature / bugfix | `backend-pipeline-engineer`, `security-and-qa-reviewer` (+ `docs-and-spec-maintainer` if public API/settings changed) | `Backend -> Security/QA (-> Docs)` |
| Frontend-only UI / modal / CSS change | `frontend-ui-engineer`, `security-and-qa-reviewer` (or `browser` for live UI check) | `Frontend -> Security/QA / Browser` |
| Security audit & hardening | `security-and-qa-reviewer` -> targeted fixes (`backend`/`frontend`) -> `security-and-qa-reviewer` | `Audit -> Fix -> Re-Audit` |
| Compatibility rules update (`data/compatibility-rules.json`) | `backend-pipeline-engineer`, `security-and-qa-reviewer` | `Backend -> Security/QA` |
| Docs / ADR / spec update | `docs-and-spec-maintainer` | `Docs` |

---

## 2. Step-by-Step Orchestration Procedure

### Step 1: Freeze the Cross-Agent Contract
Before spawning parallel workers for a full-stack task, write down and pass the exact contract in both briefs:
- **HTTP Route & Method** (e.g. `POST /api/migration/runs/{id}/...`)
- **Request & Response JSON keys** (pay special attention to `MigrationSettings` dotted `JsonPropertyName` keys vs PascalCase C# properties)
- **Status / Error Codes** (`EventCodes`, `ErrorCatalogue`, HTTP `200`/`202`/`400`/`404`)
- **UI Hooks** (`wwwroot/js/api.js` method name, tab/modal IDs, `data-*-action` attributes)

### Step 2: Spawn Implementation Specialists
Invoke specialists via `invoke_subagent` (using their exact `TypeName`):
- **File Partitioning:**
  - `backend-pipeline-engineer` owns `src/BoldMigrate.Core/`, `src/BoldMigrate.Infrastructure/`, `src/BoldMigrate.Engine/`, `src/BoldMigrate.Api/Controllers/`, `Middleware/`, `Preview/`, `Program.cs`, and `data/compatibility-rules.json`.
  - `frontend-ui-engineer` owns `src/BoldMigrate.Api/wwwroot/` (`index.html`, `scanner.html`, `css/`, `js/`), **except** `workflow-guide.html` (owned by `docs-and-spec-maintainer`).
- **Workspace Mode:**
  - If `backend-pipeline-engineer` and `frontend-ui-engineer` touch strictly disjoint files above and only `backend-pipeline-engineer` runs `dotnet build`, `Workspace: "inherit"` is fast and avoids merge overhead.
  - If two workers might build/test concurrently or touch overlapping files, set `Workspace: "share"` (or serialize them).
- **Do not poll:** After calling `invoke_subagent`, proceed with non-conflicting work or end your turn and wait for reactive notifications.

### Step 3: Run the Security & QA Gate
Once implementation workers report back:
1. Invoke `security-and-qa-reviewer` with the list of changed files and the frozen contract.
2. Require `security-and-qa-reviewer` to:
   - Audit against all 8 invariants in [AGENTS.md](../../../AGENTS.md).
   - Add/update xUnit tests in `BoldMigrate.Core.Tests`, `BoldMigrate.Engine.Tests`, or `BoldMigrate.Api.Tests`.
   - Run `dotnet test` and return exact pass/fail counts per test assembly.
3. If `security-and-qa-reviewer` flags issues, send follow-up instructions to the existing worker via `send_message` (do not launch duplicate workers for the same role), then have `security-and-qa-reviewer` re-verify.

### Step 4: Synchronize Documentation
If any public API route, `MigrationSettings` key, `ReportStateMachine` transition, `EventCodes`/`ErrorCatalogue` entry, UI workflow step, or architectural decision changed:
1. Invoke `docs-and-spec-maintainer`.
2. Provide the verified diff summary from Steps 2–3.
3. Ensure `docs/application-spec.md`, `docs/api-reference.md`, `wwwroot/workflow-guide.html`, and (if architectural) `docs/decisions/*.md` are updated.

### Step 5: Final Solution Sign-Off
Before replying to the user:
- Run `dotnet test --verbosity minimal` if any files changed after the last test run (including `.html` or `data/` changes).
- Summarize changes per specialist, list modified files, and report exact observed test results.

---

## 3. Ready-to-Use Delegation Prompt Templates

### Backend Brief Template
```text
Read .agents/skills/boldmigrate-backend-pipeline/SKILL.md first.
Goal: <concise feature/fix goal>
Owned files: <exact C# / data paths>
Frozen contract: <routes, DTOs, state transitions, settings keys>
Constraints: Keep Core dependency-free; source adapters read-only; DTD-prohibited XML parsing; Redactor on logs; startup SQLite guard if schema changes.
Return: Changed files, new/updated symbols, and build status.
```

### Frontend Brief Template
```text
Read .agents/skills/boldmigrate-frontend-ui/SKILL.md first.
Goal: <concise UI goal>
Owned files: <exact wwwroot/index.html, css/, js/ paths — do NOT edit workflow-guide.html>
Frozen contract: <Api.js method signatures, exact JSON payload/response keys, DOM IDs>
Constraints: Use Api.request() for CSRF; escapeHtml() on all dynamic strings; withButtonProgress() for async actions; bump ?v= on edited assets.
Return: Changed files, UI behaviors added, and manual check notes.
```

### Security & QA Brief Template
```text
Read .agents/skills/boldmigrate-security-and-diagnostics/SKILL.md and .agents/skills/boldmigrate-testing-and-release/SKILL.md first.
Scope: Audit and test changes in <list of files>.
Check: All 8 AGENTS.md invariants (CSRF, escapeHtml, Redactor, XXE, read-only SSRS, path/redirect guards, BoldReports 15.1.17 parity, SQLite guards).
Actions: Add/update unit or BoldMigrateApiFactory integration tests as needed and run `dotnet test`.
Return: Security findings (pass/fail per invariant) and exact `dotnet test` counts per assembly.
```

### Docs & Spec Brief Template
```text
Read .agents/skills/documentation-and-adrs/SKILL.md first.
Scope: Synchronize docs with verified changes in <list of files / endpoints / UI steps>.
Targets: docs/application-spec.md, docs/api-reference.md, src/BoldMigrate.Api/wwwroot/workflow-guide.html, README.md, and docs/decisions/ (if architectural).
Constraints: Verify every cited route, symbol, and setting directly in source files.
Return: Updated doc files and summary of changes.
```
