# WORKFLOW.md configuration reference

This is the reference for the parser and runtime in this branch. `workspace.cleanup`
is **new in this change, not a claim of support in installed 0.1.11/0.1.12 packages**.
All other entries below describe the existing parser. Check the release containing
this change before adding cleanup settings to an installed instance: older versions
ignore unknown keys, so adding them alone does not change cleanup behavior.

## File format and validation

Start the file with `---` on its own first line, put a YAML mapping between the
delimiters, then put the agent prompt after the second `---`. CRLF is accepted.
Leading text or whitespace before the opening delimiter makes the entire file a
prompt, which then fails required tracker validation. The body is trimmed.

Map keys are case-insensitive. Unknown keys are retained in the parsed source but
ignored by runtime settings; there is no unknown-key warning. Do not use a typo as
a way to disable an option. Optional missing/null values use defaults. Strings are
trimmed except hook scripts. Integer strings are accepted; numeric settings are
signed 32-bit integers. Booleans accept YAML booleans or `true`/`false` strings.
List settings accept YAML sequences or comma-separated strings; blank entries are
discarded. Malformed section maps/types fail loading.

Required settings are `tracker.kind`, `tracker.api_key`, `tracker.owner`, and
`tracker.repo`. Parse validation and dispatch preflight are separate: a `$TOKEN`
reference can parse successfully but preflight fails if its environment value is
missing. Prompt syntax is checked when loading; variable/member access is checked
when rendering an issue. The dashboard's validate action does not execute hooks.

## Tracker

| Key | Type / default | Meaning and constraints |
| --- | --- | --- |
| `tracker.kind` | required string | `github` only, case-insensitive. |
| `tracker.endpoint` | string, `https://api.github.com/graphql` | GraphQL endpoint; use a trusted endpoint because requests carry the tracker credential. |
| `tracker.api_key` | required string | Prefer `$GITHUB_TOKEN`. The entire leading-`$` string names an environment variable. No `${NAME}` or embedded interpolation. Literal tokens work but should not be committed. Resolved at preflight. |
| `tracker.owner` | required nonblank string | GitHub repository owner. |
| `tracker.repo` | required nonblank string | Repository name, without owner prefix. |
| `tracker.milestone` | string or integer, null | Filter by milestone title (case-insensitive) or number; blank/null disables this filter. |
| `tracker.include_pull_requests` | boolean, `true` | Include linked PR context in normalized issues/prompts. Does not dispatch PRs as independent work items. |
| `tracker.labels` | list/CSV, `[]` | All specified labels must match, case-insensitive. Empty means no label filter. Losing a required label ends continuation and makes the issue ineligible. |
| `tracker.active_states` | list/CSV, `[Open, In Progress]` | States eligible for execution; compared case-insensitively. GitHub issues normally supply Open/Closed, so custom state names do not create GitHub workflow states. |
| `tracker.terminal_states` | list/CSV, `[Closed]` | Terminal states take precedence over active states, stop execution, and qualify for cleanup according to policy. A merged PR alone is not a terminal issue unless the issue also closes. |

Filters are evaluated during polling, reconciliation, between turns, and before
successful continuation retry. Failed/missing tracker refreshes do not prove an
issue obsolete. Symphony does not close issues, remove labels or merge PRs as a
hidden orchestration action; any such action must come from the configured agent
workflow or an operator.

## Scheduling and HTTP

| Key | Type / default | Meaning and constraints |
| --- | --- | --- |
| `polling.interval_ms` | integer, `600000` | Poll delay in milliseconds; minimum 1000. Explicit refresh can wake the worker earlier. |
| `agent.max_concurrent_agents` | integer, `5` | Positive limit on simultaneously dispatched issue workers. Not a cap on saved Codex threads or historical worktrees. |
| `agent.max_turns` | integer, `20` | Positive maximum turns in one app-server attempt; the same Codex thread continues across those turns. |
| `agent.max_retry_backoff_ms` | integer, `300000` | Positive cap on failure retry backoff, in milliseconds. Successful continuation has its own scheduling behavior. |
| `agent.max_concurrent_agents_by_state` | map, `{}` | Per-state positive integer caps in addition to the global cap. Keys are trimmed and lowercased. Invalid/nonpositive entries are silently ignored. Example: `{Open: 2}`. |
| `server.port` | nullable integer, null | Optional loopback HTTP port override. CLI `--port` wins. Null leaves host configuration in control; 0 asks the OS for an ephemeral port. Parser rejects negatives; actual listener validation still applies (valid port range 0–65535). Only applied during startup, so changing it requires restart. |

## Workspace and cleanup

| Key | Type / default | Meaning and constraints |
| --- | --- | --- |
| `workspace.root` | string, `./workspaces` | Containment root for workspace operations. |
| `workspace.shared_clone_path` | string, `./workspaces/repo` | Shared Git clone holding refs/objects and worktree registrations. Must be inside root. |
| `workspace.worktrees_root` | string, `./workspaces/worktrees` | Parent for per-issue directories. Must be inside root and not overlap the shared clone. |
| `workspace.base_branch` | nonblank string, `main` | Remote base branch used to create new issue branches. Existing issue branches/worktrees are reused. |
| `workspace.remote_url` | nullable string, null | Optional clone remote; absent/blank uses `https://github.com/<owner>/<repo>.git`. Git authentication is separate from tracker API authentication. Keep credentials out of URLs. |
| `workspace.cleanup.mode` **new** | string, `ineligible` | `retain`, `terminal`, or `ineligible`, case-insensitive. See policy table below. |
| `workspace.cleanup.dry_run` **new** | boolean, `false` | Log tracker-qualified cleanup candidates, but do not run removal hooks, remove workspaces, or purge issue history. This is a policy preview, not a complete filesystem safety audit. |

The three path values accept an entire `$ENV_VAR` reference and `~` expansion.
Missing/blank environment references fall back to that setting's default. Relative
paths resolve from the process working directory, **not the location of WORKFLOW.md**.
Changing root does not recalculate either child-path default: configure all three
together. No arbitrary config-wide environment substitution is performed.

| Mode | Removal eligibility after worker finalization |
| --- | --- |
| `retain` | Leave workspaces and correlated local history for administrator review. Normal stopping/retry eligibility rules still apply. Retention can grow without bound. |
| `terminal` | Remove only after fresh tracker data confirms a configured terminal state. Keep open issues whose label/milestone no longer matches. |
| `ineligible` | Most aggressive safe mode; existing behavior. Remove terminal issues and issues no longer eligible by state/label/milestone. Does not remove still-eligible work merely because a turn succeeded. |

Cleanup runs at startup and during reconciliation, including while dispatch is
paused. Successful turns/exits, failed attempts, and cancellation alone are not
deletion triggers. There is no age/count/size-based retention setting and no
force-delete option. `dry_run` does not freeze polling, worker cancellation,
retry bookkeeping or ordinary runtime events. Retained issues remain tracked.

All real cleanup still requires no active worker/live foreign lease, a durable
cleanup claim, fresh tracker eligibility, path containment, verified Git ownership,
and clean tracked/non-ignored untracked files. Local branches and shared-clone
objects remain, preserving unpublished branch commits. Detached HEAD worktrees
are preserved because their commits might lack a surviving branch. Ignored build output is
disposable. Unknown directories and symbolic links are refused. Existing failed
cleanup remains retryable. Policy changes are reread before removal; already-running
filesystem operations cannot be retroactively undone. See [workspace cleanup](workspace-cleanup.md).

These settings do not archive/delete Codex threads, close native subagents, or
change `/agents`. The latter is a Codex descendant-thread picker, not a Symphony
worker/process counter. No undocumented native-agent cleanup RPC is introduced.

## Hooks

| Key | Type / default | Execution |
| --- | --- | --- |
| `hooks.after_create` | script string, null | Only after a new issue workspace is created; failure fails preparation. |
| `hooks.before_run` | script string, null | Before each attempt; failure aborts the attempt. |
| `hooks.after_run` | script string, null | During finalization when a workspace exists; failure is logged, not allowed to replace run finalization. |
| `hooks.before_remove` | script string, null | Before actual workspace removal, after ownership/cleanliness checks. Failure is logged and removal continues subject to rechecking safety. Not executed in retain/dry-run. |
| `hooks.timeout_ms` | integer, `60000` | Hook timeout in milliseconds. Values <=0 fall back to 60000 rather than rejecting. Cleanup also has its own bounded total budget (30 seconds plus at most 60 seconds for hook allowance). |

Scripts run in the workspace with the service account's access, using the platform
shell. YAML block scalars preserve multiline scripts. Treat workflow edits as code
execution privileges. Empty/whitespace scripts mean disabled. Do not print secrets
or use hooks to bypass the safe cleanup policy.

## Codex

| Key | Type / default | Meaning and validation |
| --- | --- | --- |
| `codex.command` | nonblank string, `codex app-server` | Launch command in the issue workspace. Shell syntax is intentional; use trusted text. App-server mode implements protocol initialization and turns; other commands follow the shell-command path. |
| `codex.turn_timeout_ms` | positive integer, `3600000` | Per-turn timeout (milliseconds); shell-command path uses its command timeout. |
| `codex.timeout_ms` | positive integer alias | Backward-compatible fallback for `turn_timeout_ms`; explicit `turn_timeout_ms` wins. Prefer the new name. An invalid noninteger alias still fails parsing even if the new field is present. |
| `codex.approval_policy` | nonblank string, `never` | Passed to Codex. Symphony does not enum-validate it; use policies supported by the installed Codex protocol. Defaults allow unattended operation. |
| `codex.thread_sandbox` | nonblank string, `danger-full-access` | Passed on thread start. Common aliases normalize to `read-only`, `workspace-write`, `danger-full-access`; other values pass through for Codex validation. |
| `codex.turn_sandbox_policy` | nonblank string, `danger-full-access` | Turn policy type; aliases normalize to `readOnly`, `workspaceWrite`, `dangerFullAccess`, `externalSandbox`. The runner supplies only a type, so types requiring extra fields may be rejected by Codex. |
| `codex.read_timeout_ms` | positive integer, `5000` | Protocol response/read timeout and bounded shutdown waits, milliseconds. |
| `codex.stall_timeout_ms` | integer, `300000` | Reconciliation stall threshold; <=0 disables stall checking. Does not disable the turn timeout. |

Sandbox defaults grant broad access. Setting policy names is not a substitute for
the service account's filesystem and secret controls. Codex version compatibility
still matters. See [UserGuide](UserGuide.md#codex-session-behavior) for approval and
tool handling, failure/retry behavior and startup troubleshooting.

## Prompt template

The Markdown body is a Scriban template, not another YAML configuration section.
Use `{{ issue.title }}`, conditions and loops. Empty body produces
`You are working on a GitHub issue.`. Strict variable/member/index/function access
turns unknown template references into `template_render_error` rather than silently
substituting empty strings. Invalid syntax produces `template_parse_error`.

Available globals: `issue` and nullable `attempt` (initial run null, retry attempt
number thereafter). Issue fields are `id`, `identifier`, `title`, `description`,
`priority`, `state`, `branch_name`, `url`, `milestone`, `labels`, `created_at`,
`updated_at`, `pull_requests`, `blocked_by`. Nullable tracker fields can be null.
PR entries expose `id`, `number`, `state`, `url`, `head_ref`, `base_ref`.
Blockers expose `id`, `identifier`, `state`. Environment variables, raw YAML
settings, secrets and arbitrary issue comments are not automatically template
globals. The agent must retrieve any additional context permitted by its tools.

The rendered issue prompt starts the first turn. Later turns use the runner's
continuation prompt in the existing thread; changing WORKFLOW.md does not rewrite
already-submitted prompts or an attempt's captured settings.

## Reload, examples and operational boundaries

File watching plus timestamp/length checks reload the workflow on next access.
Invalid reloads retain the last valid definition and log the validation error;
invalid initial load blocks startup/preflight. New attempts use new settings.
Reconciliation uses current filters and cleanup policy; active attempts retain their
launch snapshot. `server.port` is startup-only. Host/service options such as database
connection, instance ID, managed lease and workflow file path belong to host
configuration/CLI, not new WORKFLOW.md keys. Changing inherited environment variables
requires updating the service process environment, usually by restarting it.

Minimal file (set the referenced secret in the running account's environment):

```yaml
---
tracker:
  kind: github
  api_key: $GITHUB_TOKEN
  owner: example-org
  repo: example-repo
---
Resolve {{ issue.identifier }}: {{ issue.title }}.
Run relevant tests and report the result.
```

Full example, including the new optional cleanup policy; safe to review, not an
instruction to apply to a running instance:

```yaml
---
tracker:
  kind: github
  endpoint: https://api.github.com/graphql
  api_key: $GITHUB_TOKEN
  owner: example-org
  repo: example-repo
  milestone: null
  include_pull_requests: true
  labels: [symphony]
  active_states: [Open, In Progress]
  terminal_states: [Closed]
polling:
  interval_ms: 600000
agent:
  max_concurrent_agents: 5
  max_turns: 20
  max_retry_backoff_ms: 300000
  max_concurrent_agents_by_state: {}
server:
  port: null
workspace:
  root: ./workspaces
  shared_clone_path: ./workspaces/repo
  worktrees_root: ./workspaces/worktrees
  base_branch: main
  remote_url: null
  cleanup:
    mode: terminal
    dry_run: true
hooks:
  after_create: null
  before_run: null
  after_run: null
  before_remove: null
  timeout_ms: 60000
codex:
  command: codex app-server
  turn_timeout_ms: 3600000
  approval_policy: never
  thread_sandbox: danger-full-access
  turn_sandbox_policy: danger-full-access
  read_timeout_ms: 5000
  stall_timeout_ms: 300000
---
Work on {{ issue.identifier }}: {{ issue.title }}.
{{ if attempt }}This is retry {{ attempt }}.{{ end }}
{{ issue.description }}
Implement, verify, and report your changes.
```

Source authorities: `WorkflowLoader`, `WorkflowDispatchPreflightValidator`,
`WorkflowValueResolver`, `WorkflowDefinitionProvider`, `WorkflowPromptRenderer`,
`CodexAgentRunner`, `GitWorktreeWorkspaceManager`, and orchestration reconciliation.
