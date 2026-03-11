# Symphony Container Guide

## Scope

This guide covers the deployment model Symphony supports today:

- one long-running orchestrator container
- SQLite persisted on a mounted volume
- git shared clone and per-issue worktrees stored on a mounted volume
- `WORKFLOW.md` mounted from outside the image
- `GITHUB_TOKEN` and other host settings injected externally
- `codex app-server` executed inside the same container as the orchestrator

This keeps behavior aligned with the current implementation and spec:

- `WORKFLOW.md` remains the repository-owned workflow contract
- the orchestrator remains the single scheduling authority
- agent processes still run with direct access to the per-issue workspace filesystem

## What Is Externalized

Use external configuration for deployment-specific values:

- `WORKFLOW.md` path and contents
- GitHub PAT via `GITHUB_TOKEN`
- SQLite connection string via `Persistence__ConnectionString`
- orchestrator identity via `Orchestration__InstanceId`
- ASP.NET Core bind settings via `ASPNETCORE_URLS`
- Codex CLI login/config state via a mounted Codex home directory

Use `WORKFLOW.md` for repository/workflow behavior:

- issue filters
- prompt body
- agent concurrency/turn limits
- Codex command and timeouts
- workspace paths inside the container
- hook scripts
- `server.port`

Note:

- `tracker.api_key` supports `$ENV_VAR` indirection, so `api_key: $GITHUB_TOKEN` works well in containers.
- `workspace.remote_url` is currently a plain string value, not an `$ENV_VAR`-resolved field. For private repositories, prefer container Git credentials or a static SSH URL in the mounted workflow.

## Artifacts In This Repo

Use these files as the baseline:

- [`Dockerfile`](../Dockerfile)
- [`deploy/container/docker-compose.yml`](../deploy/container/docker-compose.yml)
- [`deploy/container/.env.example`](../deploy/container/.env.example)
- [`deploy/container/WORKFLOW.container.example.md`](../deploy/container/WORKFLOW.container.example.md)
- [`deploy/container/entrypoint.sh`](../deploy/container/entrypoint.sh)

## Step By Step

1. Create an external deployment folder on the host.

Example:

```text
C:\symphony\
  config\
    WORKFLOW.md
  codex-home\
```

2. Copy [`deploy/container/WORKFLOW.container.example.md`](../deploy/container/WORKFLOW.container.example.md) to your external config location as `WORKFLOW.md`.

3. Edit the external `WORKFLOW.md`.

Required changes:

- set `tracker.owner`
- set `tracker.repo`
- set `workspace.base_branch`

Recommended container values to keep:

- `server.port: 8080`
- `workspace.root: /var/lib/symphony/workspaces`
- `workspace.shared_clone_path: /var/lib/symphony/workspaces/repo`
- `workspace.worktrees_root: /var/lib/symphony/workspaces/worktrees`
- `tracker.api_key: $GITHUB_TOKEN`

4. Copy [`deploy/container/.env.example`](../deploy/container/.env.example) to `deploy/container/.env`.

5. Edit `deploy/container/.env`.

Set at least:

- `GITHUB_TOKEN`
- `SYMPHONY_WORKFLOW_HOST_PATH`
- `CODEX_HOME_HOST_PATH`

Also set if you want overrides:

- `SYMPHONY_HTTP_PORT`
- `SYMPHONY_INSTANCE_ID`
- `CODEX_NPM_VERSION`

6. Make Codex authentication available inside `CODEX_HOME_HOST_PATH`.

Practical options:

- mount a host directory that already contains Codex CLI login/config state
- provision Codex CLI API-key auth in that directory using the Codex CLI/auth flow you already use outside the container

7. If the tracked repository is private, make Git clone/fetch authentication available in the container.

Options:

- use an SSH `workspace.remote_url` in the mounted workflow and mount SSH credentials/config into the container
- use HTTPS plus container Git credential helper/config
- use a public repository

8. Build and start the container.

```powershell
docker compose -f deploy/container/docker-compose.yml --env-file deploy/container/.env up --build -d
```

9. Check health.

```powershell
curl http://127.0.0.1:8080/api/v1/health
```

10. Inspect runtime state.

```powershell
curl http://127.0.0.1:8080/api/v1/runtime
curl http://127.0.0.1:8080/api/v1/state
```

11. Stop the service when needed.

```powershell
docker compose -f deploy/container/docker-compose.yml --env-file deploy/container/.env down
```

The named `symphony-data` volume keeps:

- SQLite state
- shared clone data
- per-issue worktrees

## External Configuration Reference

### Mounted `WORKFLOW.md`

The image does not need the repository's checked-in `WORKFLOW.md`. The default container entrypoint uses:

```text
/config/WORKFLOW.md
```

Override that with either:

- `SYMPHONY_WORKFLOW_PATH`
- explicit container command arguments
- standard ASP.NET Core config key `Workflow__Path`

### ASP.NET Core / Host Settings

Useful host-level settings:

- `Persistence__ConnectionString`
- `Orchestration__InstanceId`
- `Orchestration__LeaseName`
- `Orchestration__LeaseTtlSeconds`
- `Logging__LogLevel__Default`
- `ASPNETCORE_URLS`

These are deployment concerns and should generally stay outside `WORKFLOW.md`.

### GitHub Token

Recommended pattern in the mounted workflow:

```yaml
tracker:
  api_key: $GITHUB_TOKEN
```

Then provide `GITHUB_TOKEN` through the container environment.

### Workspace Paths

Inside the container, use absolute Linux paths in `WORKFLOW.md`. The compose example assumes:

```yaml
workspace:
  root: /var/lib/symphony/workspaces
  shared_clone_path: /var/lib/symphony/workspaces/repo
  worktrees_root: /var/lib/symphony/workspaces/worktrees
```

That keeps SQLite state and git workspaces under one persisted mount root.

## Central Orchestrator Plus One Container Per Agent

### Is it possible?

Architecturally: yes.

Today: no, not as a built-in runtime mode.

The current implementation launches the agent via the local `IAgentRunner` adapter and expects:

- direct subprocess execution
- direct access to the issue workspace on the same filesystem
- direct event streaming back into the in-process orchestrator callback

### Can this be configured in `WORKFLOW.md` today?

No.

`WORKFLOW.md` currently configures the agent command, timeouts, sandbox settings, and workflow behavior, but not deployment topology. It does not tell Symphony to dispatch work to remote worker containers.

### What would need to change?

To support one container per agent cleanly, Symphony would need a new infrastructure mode such as a remote agent runner that:

- creates agent containers/jobs on demand
- mounts or prepares the issue workspace for that container
- streams agent protocol events back to the orchestrator
- handles cancellation, retry, and cleanup across process boundaries
- keeps the existing lease/claim semantics intact

The codebase already has a useful seam for this in `IAgentRunner`, but there is no remote/container-backed implementation yet.

### Should that live in `WORKFLOW.md`?

Not by itself.

The better split is:

- `WORKFLOW.md`: repository/workflow behavior
- host/app config: deployment topology, runner kind, container runtime endpoint, queue names, image names, resource limits

If you wanted per-repo control later, a small selector in `WORKFLOW.md` such as `agent.runner_kind` could make sense, but container image names, orchestration endpoints, and infrastructure credentials should remain external deployment config.
