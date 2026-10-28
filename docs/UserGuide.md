# Symphony User Guide

## What Symphony Does

Symphony is a `.NET 10` Windows-service-friendly orchestrator that watches GitHub issues, prepares a per-issue worktree workspace, runs a Codex app-server session, and keeps durable runtime state in SQLite for recovery and debugging.

In v1, Symphony ships with:

- GitHub PAT authentication
- EF Core + SQLite persistence
- Worker + HTTP API hosting
- Responsive dashboard UI served by the host root path
- Shared clone + per-issue git worktrees
- Permissive auto-approval for supported Codex approval requests
- Optional `github_graphql` client-side tool support during Codex sessions

## Quick Start

1. Set `GITHUB_TOKEN` in the environment.
   In PowerShell, set it in the same terminal session that will start Symphony:

```powershell
$env:GITHUB_TOKEN = "<token>"
```

   Verify the host process will see it before running:

```powershell
if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { throw "GITHUB_TOKEN is not set." }
```

2. Edit [`WORKFLOW.md`](../WORKFLOW.md) with your GitHub owner/repo and any label or milestone filters.
   If your hooks, Git credential helper, or agent commands use GitHub CLI (`gh`), complete the [GitHub CLI authentication prerequisite](#github-cli-authentication-prerequisite) before dispatching work.
3. Restore tools and build:

```powershell
dotnet tool restore
dotnet build Symphony.slnx --warnaserror
```

4. Run locally:

```powershell
dotnet run --project src/Symphony.Host
```

The repository launch profile already supplies `../../WORKFLOW.md`. If you want to pass the workflow path explicitly from the repository root, use:

```powershell
dotnet run --project src/Symphony.Host -- ../../WORKFLOW.md
```

5. Open the dashboard:

```text
http://127.0.0.1:<port>/
```

If you need the raw probe, the health endpoint remains available at:

```text
http://127.0.0.1:<port>/api/v1/health
```

If you are starting from a GitHub Release bundle instead of source, use [PackageGuide.md](PackageGuide.md) for the interactive installer flow.

### GitHub CLI authentication prerequisite

Install GitHub CLI (`gh`) when your workflow uses it, and check authentication under the same OS account and in the same environment that runs Symphony's hooks and Codex commands:

```sh
gh auth status
```

If an invalid `GH_TOKEN` or `GITHUB_TOKEN` supplies authentication, replace it through the execution environment's existing secret configuration, then run `gh auth status` again. For GitHub.com, `GH_TOKEN` takes precedence over `GITHUB_TOKEN`, and both override stored credentials; interactive login cannot repair an invalid overriding token. Do not blindly unset `GITHUB_TOKEN`, because Symphony's tracker may depend on it. See [GitHub CLI environment variables](https://cli.github.com/manual/gh_help_environment).

When authentication is missing or invalid and no environment token overrides stored credentials, complete the interactive login locally, then verify it:

```sh
gh auth login
gh auth status
```

For WSL deployments, run these commands inside the WSL distribution and Linux account running Symphony; a Windows login does not establish authentication there. Service and container deployments likewise need authentication available to their execution account and environment. Complete any interactive login before unattended jobs run.

Symphony's `tracker.api_key` authenticates its own GitHub API requests and the `github_graphql` tool. A successful API response (including HTTP 200) does not verify authentication for a separate `gh` invocation. GitHub CLI can use its own stored login or a supported token environment variable, such as `GH_TOKEN` or `GITHUB_TOKEN`, inherited by that process; an existing valid environment-based setup does not also require interactive login. Verify with `gh auth status` without printing token values.

The checked-in workflow has `after_create` and `before_run` set to `null`; Symphony does not universally require `gh` for dispatch. This prerequisite applies when your configured hooks, Git credential helper, or agent commands depend on it. Git clone/fetch access and Codex login must also work in that environment.

## Workflow Settings

Symphony reads its runtime behavior from [`WORKFLOW.md`](../WORKFLOW.md).

See the [complete WORKFLOW.md reference](WorkflowReference.md) for every supported
option, defaults, validation, prompt variables and reload behavior. See the
[WSL upgrade guide](WslUpgradeGuide.md) for preserving configuration and state
when installing a new release.

When you use `dotnet run --project src/Symphony.Host`, the default SQLite file path from `appsettings.json` is created under `src/Symphony.Host/data/` if it is missing.

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

The dashboard includes a workflow editor for `WORKFLOW.md`, so operators can edit the YAML front matter and prompt template in the browser and save them back to disk without restarting the host. The editor starts minimized by default and can be expanded or minimized from the panel header. Inline secret values are masked with placeholders and preserved unless replaced explicitly. If another operator saves first, your save returns a conflict; reload the editor before retrying.

`tracker.api_key` supports `$ENV_VAR` indirection. Symphony validates that required secrets exist without logging their values.

If you see `missing_tracker_api_key`, the workflow value usually looks correct and the problem is that the current host process does not have the referenced environment variable. This often happens when:

- `GITHUB_TOKEN` was set in one shell but `dotnet run` was started from another shell, the IDE, or a Windows Service
- the variable name in `WORKFLOW.md` does not exactly match the environment variable name
- the variable is present but empty or whitespace

## Runtime API

Symphony exposes these HTTP endpoints:

- `GET /`: dashboard for host health, workload, tokens, activity, leases, and issue drill-down
- `GET /api/v1/health`: liveness/health checks
- `GET /api/v1/runtime`: current workflow/config snapshot
- `GET /api/v1/workflow`: masked editable source with an opaque `contentRevision` and `effectiveLoadedRevision`; these differ if a broken file was rejected and the last valid workflow remains active
- `POST /api/v1/workflow/validate`: validate a draft's YAML and prompt syntax without changing the file
- `PUT /api/v1/workflow`: validate and atomically save a draft with `expectedRevision` (or the `contentRevision` returned by GET); stale revisions return HTTP 409 with `workflow_revision_conflict`, and a missing revision returns HTTP 428
- `GET /api/v1/state`: running sessions, retry queue, tracked issue distribution, recent activity, lease state, token totals, runtime totals, and latest rate limits
- `GET /api/v1/<issue_identifier>`: issue-specific runtime/debug view
- `POST /api/v1/refresh`: queue an immediate best-effort poll/reconcile cycle
- `GET /api/v1/management/status`: current persisted pause state, active run count, pending retry count, and quiescence
- `POST /api/v1/management/pause`: persist a dispatch pause immediately; active runs continue
- `POST /api/v1/management/drain?timeout_ms=30000`: pause and wait for active runs to finish; returns `200` only when quiescent, or `202` with state `draining` on timeout. The timeout may be 0–300000 ms; invalid values return `invalid_drain_timeout`.
- `POST /api/v1/management/resume`: clear the pause and queue an immediate refresh

Pause, drain, resume, and status are idempotent. A paused host still reconciles existing work and retains pending retries without dispatching them. The pause survives process restart. The management API is intended for a device-local supervisor over loopback; do not expose the unauthenticated host port directly to a network.

### Managed dispatch lease

For backplane-managed instances, set `ManagedLease:Enabled=true` in host configuration and
provide `ManagedLease:InstanceId`, `ManagedLease:GenerationId`, and
`ManagedLease:SigningKeyReference=$ENV_VAR`. The referenced environment variable must contain
at least 32 characters and be available under the Symphony service account. Managed mode
starts without dispatch permission, even if SQLite contains a lease from a previous process.
Startup terminal cleanup waits for a current lease and runs when the worker next wakes.

The device-local supervisor forwards a backplane-issued lease to
`PUT /api/v1/management/lease` as JSON with `instanceId`, `generationId`, `epoch`,
`issuedAtUtc`, `expiresAtUtc`, and base64 `signature`. The signature is HMAC-SHA256 over the
UTF-8 string `instanceId\ngenerationId\nepoch\nissuedUnixMilliseconds\nexpiresUnixMilliseconds`,
using the key referenced in configuration. The default maximum lease is 60 seconds with a
5-second conservative clock-skew allowance. The backplane must keep leases for an execution
scope exclusive across generations. It must let the old generation's lease expire or prove it
fenced before issuing a replacement lease.

Status includes `currentEpoch`, `leaseExpiresAtUtc`, and `dispatchDeniedReason`. A valid
renewal is idempotent; stale epochs and wrong generations are rejected. Lease expiry blocks
new dispatch and cancels active Codex process trees without waiting for a poll or network
response. Resume also requires a valid lease in managed mode.

The state and issue endpoints are derived from persisted orchestrator state in SQLite rather than ad hoc in-memory caches.
Dashboard and `/api/v1/state` token totals are derived from absolute Codex usage snapshots such as `thread/tokenUsage/updated` and `total_token_usage`. Delta-only payloads like `last_token_usage`, and generic `usage` maps on ordinary notifications or turn events, are ignored to avoid double-counting, so totals only advance when Codex emits a new absolute snapshot.
For human-readable dashboard surfaces, fallback-only `other_message` protocol entries are suppressed when Codex did not emit a useful message; the durable SQLite event log remains available for deeper debugging.
Tracked issue summaries refresh cached GitHub states for previously seen issues, so the dashboard and `/api/v1/state` continue showing `Closed` counts after issues leave the active set. When a tracked retrying issue becomes terminal, Symphony releases its retry reservation and runs the same terminal workspace cleanup used for closed running issues and startup sweeps.
Per-issue worktree branch creation does not configure upstream tracking. Shared-clone Git mutations are serialized inside the host process and across processes with a `<shared_clone_path>.lock` file, and Symphony fetches the configured base branch into a private `refs/symphony/origin/<base_branch>` snapshot instead of broadly pruning `refs/remotes/origin/*`.
On large desktop layouts, the right-hand issue detail rail stays pinned and scrolls independently so long issue detail content does not hide instance health or rate-limit cards.
The dashboard shell uses the full viewport width on large screens instead of staying capped inside a centered content column.
Retry queue surfaces show time until the next attempt, and any overdue retry is shown as ready now instead of appearing to be scheduled in the past.
The workflow editor writes back to `WORKFLOW.md` after validating the updated YAML and prompt body, so broken edits are rejected before they replace the on-disk workflow file. Validation and management errors contain codes and generic messages so inline secret values are not returned.

## Codex Session Behavior

For Codex app-server runs, Symphony:

- starts one app-server subprocess per worker attempt
- checks the installed CLI's model catalog and selects the account default when the configured model is unavailable
- reuses the same `threadId` across continuation turns
- treats failed or interrupted `turn/completed` statuses as run failures, with the Codex error in the attempt diagnostics
- refreshes the GitHub issue state after each successful turn
- continues on the live thread until the issue leaves the configured active states, stops matching the configured labels or milestone, or `agent.max_turns` is reached
- keeps parsing strictly on stdout only
- treats stderr as diagnostics only

For label-gated workflows, the agent can remove the execution label after implementing, verifying, pushing, and opening a PR. Symphony checks the same label and milestone filters between turns, during running-issue reconciliation, and before scheduling a successful continuation retry. All configured labels must still be present (case-insensitive); milestones match by title or number. Losing eligibility stops continuation and releases the worker claim. Reconciliation then applies the configured workspace cleanup policy after finalization; dirty or otherwise unsafe workspaces are preserved. The issue and PR may remain open for human review; Symphony does not remove labels, close issues, or merge PRs on its own. Workflows without label or milestone filters retain their existing state-based behavior. Persisted retries are checked against current candidates before dispatch after a restart.

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
- a single row of aggregate accounting for pruned runs

Issue/session-related diagnostics include `issue_id`, `issue_identifier`, and `session_id` where applicable. Token-bearing auth strings and common PAT formats are redacted from persisted diagnostics.

When GitHub confirms a configured terminal state (`Closed` by default), Symphony removes the issue's worktree and all of its issue-specific SQLite records: cached details, runs, attempts, sessions, retries, dispatch claims, workspace records, and events correlated by issue, run, attempt, or session. This runs at startup and on each polling cycle, including for older history that no longer has a cache row. A running worker must finish before records are deleted, and another live instance's work is preserved. Failed or incomplete workspace removal keeps recovery records and retries cleanup on a later tick; tracker errors or missing results do not authorize deletion. Pausing dispatch still allows cleanup.

After cleanup the issue no longer appears in tracked counts, recent activity, or per-issue drill-down; `GET /api/v1/{issueIdentifier}` returns `404`. Lifetime tokens, runtime seconds, and the latest rate-limit snapshot remain available through the dashboard and state API, using one accounting row with no issue identifiers. Reopening an eligible GitHub issue lets Symphony discover it afresh. GitHub remains the history source; cleanup does not change GitHub issues, PRs, or repository refs, and does not rewrite external operational logs or shared workflow configuration.

Each worker has its own attempt lease in `instance_leases`, renewed at least every 10 seconds independently of the poll lease. The lifetime lease remains active through shutdown hooks and final persistence, so another instance cannot mistake the worker for an orphan between ticks or remove its workspace early. The heartbeat also observes stop requests from other instances. If an instance dies, its worker lease expires and permits recovery. Legacy events containing only an issue ID are refreshed during polling; GitHub supplies the issue number used for safe workspace cleanup. Missing identifiers defer cleanup until a later successful refresh.

The schema migration adds the accounting table automatically at startup. SQLite reuses pages freed by deletion, so row counts and future polling work shrink without running a blocking `VACUUM` during dispatch. The database file may retain its previous allocated size. Back up the database before an upgrade if you need to retain closed-issue diagnostics: successfully pruned history is not recoverable from a migration rollback.

Cleanup uses a durable `cleanup` dispatch claim to reserve the affected issue while hooks and filesystem operations run. Other issues can continue writing state during removal. A failed/interrupted cleanup retains that reservation until a later cleanup succeeds or the tracker confirms the issue is eligible again. Upgrade all instances sharing a database together: older versions do not understand cleanup reservations.

### Dispatched job shows "Session not started" and zero turns/tokens

These symptoms mean no session ID or turn/token activity has been recorded yet; they do not identify a single cause. Symphony prepares the Git workspace and runs configured `after_create` and `before_run` hooks before starting the Codex session. Check the issue's recent events, attempt diagnostics, and host logs to locate the last completed step.

If that path uses `gh` (directly or as a Git credential helper), check `gh auth status` in the execution environment. Replace an invalid environment token through the existing secret configuration; use `gh auth login` locally only when no environment token overrides stored credentials, then verify again as described above. Missing GitHub CLI authentication was resolved by interactive login in a reported WSL setup using Symphony 0.1.11 and Codex 0.163.0, even though Symphony's configured GitHub API requests returned HTTP 200. It is one possible cause, not a diagnosis for every stalled job. If authentication is valid, investigate Git clone/fetch access, workspace locks, hook failures/timeouts, and Codex startup diagnostics.

A background Codex daemon alone does not establish that a Symphony job is active: Symphony starts an app-server subprocess per worker attempt. Confirm the specific issue's session ID and subsequent activity in the dashboard or `GET /api/v1/<issue_identifier>`, correlated with host logs. Keep credentials out of logs, issue reports, and chat.

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

## Dashboard Assets

The dashboard is a static frontend served by `src/Symphony.Host`:

- Tailwind source: `src/Symphony.Host/Styles/dashboard.css`
- Generated CSS: `src/Symphony.Host/wwwroot/assets/dashboard.css`
- Client script: `src/Symphony.Host/wwwroot/assets/dashboard.js`

To rebuild the CSS after UI changes:

```powershell
Set-Location src/Symphony.Host
npm install
npm run build:css
```

## Containers

For container deployment, mounted workflow/config, and compose artifacts, see [ContainerGuide.md](ContainerGuide.md).
