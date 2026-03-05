# Symphony Implementation Plan (.NET 10 + SQLite)

## Scope

Build the Symphony service from `SPEC.md` as a long-running orchestrator that:

- Reads work from GitHub issues (with PR and milestone context).
- Creates and manages per-issue workspaces.
- Runs Codex app-server sessions per issue.
- Persists operational state in SQLite for restart recovery and auditability.

## Locked Decisions (2026-03-05)

1. Host model: Worker + HTTP API from day 1.
2. Persistence: EF Core with SQLite.
3. Topology: Multi-instance safety required.
4. GitHub auth: PAT for v1.
5. Candidate filtering: states + labels + milestones.
6. Dispatch target: issues only (no PR-only dispatch).
7. Completion state: `Closed`.
8. Workspace strategy: shared clone + Git worktrees per issue.
9. Codex policy: permissive auto-approve.
10. Optional tool extension: include `github_graphql` in v1.
11. v1 defaults: `max_concurrent_agents=5`, `polling.interval_ms=600000` (10 minutes).
12. Deployment target: Windows Service.

## Target Stack

- Runtime: `.NET 10` (`net10.0`)
- Host model: ASP.NET Core minimal host with background workers + HTTP API
- Persistence: SQLite (`Microsoft.Data.Sqlite` + EF Core 10 migrations)
- Tracker integration: GitHub GraphQL API
- Observability: structured logging + optional HTTP status API
- Testing: xUnit + integration tests using in-memory SQLite
- Hosting mode: Windows Service

## Architecture (Spec Mapping)

1. Workflow Loader (`SPEC.md` sections 5-6)
- Load `WORKFLOW.md` from explicit path or default CWD.
- Parse YAML front matter + markdown prompt body.
- Validate strict config and typed options.
- Support live reload with "last known good" fallback.

2. Config Layer (`SPEC.md` section 6)
- Bind `tracker`, `polling`, `workspace`, `hooks`, `agent`, `codex`.
- Resolve `$ENV_VAR` values.
- Validate required GitHub fields: `tracker.kind`, `tracker.api_key`, `tracker.owner`, `tracker.repo`.

3. GitHub Tracker Client (`SPEC.md` section 11)
- Implement:
  - `fetch_candidate_issues()`
  - `fetch_issues_by_states(state_names)`
  - `fetch_issue_states_by_ids(issue_ids)`
- Normalize issue + milestone + linked PR metadata into domain model.
- Enforce candidate filters using configured states + labels + milestones.
- Exclude PR-only records from dispatch.
- Paginate and enforce timeout/retry policies.

4. Orchestrator (`SPEC.md` sections 7-8)
- Poll loop with bounded concurrency.
- Eligibility checks (state, blockers, retry due time, slot availability).
- Dispatch ordering: priority then creation time.
- Reconciliation loop for active runs.
- Retry queue with exponential backoff and cap.
- Terminal completion handling aligned to issue `Closed` state.

5. Workspace Manager (`SPEC.md` section 9)
- Deterministic workspace path per issue identifier.
- Path sanitization and root containment checks.
- Shared repository clone root plus per-issue worktree management.
- Lifecycle hooks: `after_create`, `before_run`, `after_run`, `before_remove`.
- Startup terminal cleanup.

6. Agent Runner (`SPEC.md` section 10)
- Launch `codex.command` as subprocess.
- Perform app-server handshake (`initialize`, `thread/start`, `turn/start`).
- Parse stdout protocol stream; treat stderr as logs only.
- Handle approvals/user-input/tool-call policy (permissive auto-approve for v1).
- Implement `github_graphql` tool extension contract in v1.

7. Prompt Builder (`SPEC.md` section 12)
- Strict template rendering with `issue` + `attempt`.
- Fail run on unknown variables/filters.
- Support continuation prompt behavior.

8. Observability and API (`SPEC.md` section 13)
- Structured logs with issue/session correlation.
- In-memory snapshot + SQLite-backed history.
- `/api/v1/*` endpoints included in v1 for runtime status.

9. Failure + Security (`SPEC.md` sections 14-15)
- Typed failure categories.
- Retry/stop/cleanup behavior.
- Secrets via environment only.
- Guard rails for workspace path, hook execution, and tool scoping.
- PAT auth handling for v1 with explicit secret redaction in logs.

10. Multi-Instance Coordination
- Add DB-backed lease/lock for poll-dispatch ownership.
- Prevent duplicate dispatch across instances.
- Add heartbeat and lease expiry for failover.
- Ensure retry queue claiming is atomic.

## Solution Layout

Suggested mono-repo layout:

```text
/src
  /Symphony.Host                 (ASP.NET Core host + background services + optional API)
  /Symphony.Core                 (domain models, interfaces, orchestrator rules)
  /Symphony.Infrastructure
    /Persistence.Sqlite          (DbContext, migrations, repositories)
    /Tracker.GitHub              (GraphQL client + normalization)
    /Agent.Codex                 (protocol client + process runner)
    /Workflows                   (WORKFLOW.md parsing + validation)
/tests
  /Symphony.Core.Tests
  /Symphony.Integration.Tests
```

## SQLite Data Model (Initial)

Persist only what improves recovery and operations:

- `workflow_snapshots` - loaded config hash, source path, loaded_at.
- `issues_cache` - latest normalized issue payload, state, updated_at.
- `runs` - run lifecycle (queued/running/succeeded/failed/cancelled).
- `run_attempts` - attempt number, started_at, ended_at, outcome, error.
- `sessions` - codex thread/session IDs and state.
- `retry_queue` - due_at, attempt, reason, max_backoff policy values.
- `workspace_records` - issue to workspace mapping and cleanup metadata.
- `event_log` - typed operational events for diagnostics.
- `instance_leases` - distributed lease ownership and heartbeat metadata.
- `dispatch_claims` - atomic issue claims to prevent duplicate processing across instances.

DB operational defaults:

- SQLite WAL mode.
- Busy timeout configured.
- Migration-on-startup (fail fast on migration error).
- Indexed columns for `state`, `due_at`, `issue_id`, `issue_identifier`.
- Indexed lease/claim columns for multi-instance coordination.

## Delivery Phases

Phase 0 - Bootstrap (1-2 days)
- Create solution/projects, CI skeleton, base logging, options model.
- Add Windows Service host mode and API host wiring.
- Exit criteria: host boots as service, config loads, health endpoint responds.

Phase 1 - Workflow + Config (2-3 days)
- Implement `WORKFLOW.md` loader, strict parsing, validation, reload.
- Exit criteria: all section 5-6 conformance checks passing.

Phase 2 - GitHub Tracker Adapter (3-5 days)
- GraphQL queries, pagination, normalization, error mapping.
- Add state + label + milestone filters and issue-only dispatch source rules.
- Exit criteria: section 11 conformance tests passing.

Phase 3 - Orchestrator Engine (4-6 days)
- Polling, dispatch, reconciliation, retries, state transitions.
- Add default v1 concurrency and poll settings (`5` agents, `10` minute poll).
- Exit criteria: section 7-8 behavior tests passing.

Phase 4 - Workspace + Hooks (2-4 days)
- Workspace safety, hook execution, cleanup paths, and Git worktree flows.
- Exit criteria: section 9 tests passing including safety invariants.

Phase 5 - Agent Runner Protocol (4-7 days)
- Subprocess protocol client, continuation, timeout/stall handling.
- Exit criteria: section 10 integration tests passing.

Phase 6 - SQLite Persistence (3-5 days)
- Durable run/retry/session state and startup recovery.
- Add multi-instance lease/claim safety.
- Exit criteria: restart recovery + cross-instance safety scenarios validated.

Phase 7 - Observability + Hardening (3-5 days)
- Snapshot API, structured metrics/events, security hardening.
- Exit criteria: sections 13-15 checks and operational runbook draft.

Phase 8 - End-to-End Validation (3-4 days)
- Execute section 17 test matrix and section 18 checklist.
- Exit criteria: release candidate tag.

## Testing Plan

- Unit tests: orchestrator eligibility, retry math, config validation, prompt rendering.
- Integration tests: GitHub adapter, SQLite repositories, workspace safety, protocol parser.
- End-to-end tests: local fake Codex app-server + fake GitHub responses + full poll/dispatch loop.
- Real integration profile: gated tests with real `GITHUB_TOKEN` and test repo.
- Multi-instance tests: two+ hosts against one SQLite DB with duplicate-dispatch prevention checks.

## Immediate Next Steps

1. Scaffold solution and projects.
2. Implement Phase 1 first (workflow/config) before external integrations.
3. Implement multi-instance lease/claim persistence before enabling multi-node deployment.
