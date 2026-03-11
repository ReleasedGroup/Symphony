# Symphony User Guide

## What Symphony Does

Symphony is a `.NET 10` Windows-service-friendly orchestrator that watches GitHub issues, prepares a per-issue worktree workspace, runs a Codex app-server session, and keeps durable runtime state in SQLite for recovery and debugging.

In v1, Symphony ships with:

- GitHub PAT authentication
- EF Core + SQLite persistence
- Worker + HTTP API hosting
- Shared clone + per-issue git worktrees
- Permissive auto-approval for supported Codex approval requests
- Optional `github_graphql` client-side tool support during Codex sessions

## Quick Start

1. Set `GITHUB_TOKEN` in the environment.
2. Edit [`WORKFLOW.md`](../WORKFLOW.md) with your GitHub owner/repo and any label or milestone filters.
3. Restore tools and build:

```powershell
dotnet tool restore
dotnet build Symphony.slnx --warnaserror
```

4. Run locally:

```powershell
dotnet run --project src/Symphony.Host -- ./WORKFLOW.md
```

5. Open the health endpoint:

```text
http://127.0.0.1:<port>/api/v1/health
```

## Workflow Settings

Symphony reads its runtime behavior from [`WORKFLOW.md`](../WORKFLOW.md).

Important settings:

- `tracker.*`: GitHub endpoint, PAT, owner, repo, labels, milestones, and active/terminal states
- `agent.max_concurrent_agents`: default `5`
- `agent.max_turns`: max in-process continuation turns per worker session
- `codex.turn_timeout_ms`: per-turn timeout
- `codex.read_timeout_ms`: startup and sync request timeout
- `codex.stall_timeout_ms`: inactivity timeout enforced by the orchestrator
- `workspace.*`: shared clone, worktree root, base branch, and optional remote URL
- `hooks.*`: lifecycle hooks and timeout
- `server.port`: optional HTTP bind override

`tracker.api_key` supports `$ENV_VAR` indirection. Symphony validates that required secrets exist without logging their values.

## Runtime API

Symphony exposes these HTTP endpoints:

- `GET /api/v1/health`: liveness/health checks
- `GET /api/v1/runtime`: current workflow/config snapshot
- `GET /api/v1/state`: running sessions, retry queue, token totals, runtime totals, and latest rate limits
- `GET /api/v1/<issue_identifier>`: issue-specific runtime/debug view
- `POST /api/v1/refresh`: queue an immediate best-effort poll/reconcile cycle

The state and issue endpoints are derived from persisted orchestrator state in SQLite rather than ad hoc in-memory caches.

## Codex Session Behavior

For Codex app-server runs, Symphony:

- starts one app-server subprocess per worker attempt
- reuses the same `threadId` across continuation turns
- refreshes the GitHub issue state after each successful turn
- continues on the live thread until the issue leaves the configured active states or `agent.max_turns` is reached
- keeps parsing strictly on stdout only
- treats stderr as diagnostics only

### Approval and Tool Policy

The current v1 trust posture is permissive:

- command/file approvals are auto-approved
- user-input requests fail the run immediately
- unsupported dynamic tool calls return a structured failure result and the session continues
- supported tool execution failures return a structured failure result and are recorded as `tool_call_failed`
- `github_graphql` is advertised and uses Symphony's configured GitHub endpoint and PAT

`github_graphql` accepts either:

- a raw GraphQL query string
- an object with `query` and optional `variables`

Only one GraphQL operation is accepted per tool call.

## Workspace Safety

Symphony enforces these safety rules:

- workspaces must stay under the configured workspace root
- issue identifiers are sanitized before being used in directory names
- non-directory collisions at workspace paths fail fast
- hook scripts run inside the issue workspace only
- temp hook files are cleaned up after execution

Lifecycle hooks:

- `after_create`: fatal on failure
- `before_run`: fatal on failure
- `after_run`: best effort
- `before_remove`: best effort

## Logs and Diagnostics

Symphony stores durable operational data in SQLite:

- workflow snapshots
- issue cache
- runs and run attempts
- sessions
- retry queue
- workspace records
- event log
- leases and dispatch claims

Issue/session-related diagnostics include `issue_id`, `issue_identifier`, and `session_id` where applicable. Token-bearing auth strings and common PAT formats are redacted from persisted diagnostics.

## Testing

Recommended local validation:

```powershell
dotnet build Symphony.slnx --warnaserror
dotnet test Symphony.slnx --no-build
```

Real GitHub integration tests are opt-in:

```powershell
$env:SYMPHONY_RUN_REAL_INTEGRATION_TESTS = "1"
$env:GITHUB_TOKEN = "<token>"
dotnet test tests/Symphony.Integration.Tests/Symphony.Integration.Tests.csproj --filter RealIntegrationTests
```

If the opt-in flag is not set, the real integration test is reported as skipped.
