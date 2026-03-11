# Symphony

Symphony is a `.NET 10` service that orchestrates coding-agent work from GitHub issues.

Current scaffold includes:

- Worker + HTTP API host (`src/Symphony.Host`)
- EF Core + SQLite persistence baseline with migrations
- Multi-project architecture (`Core`, infrastructure adapters, tests)
- Runtime endpoints:
  - `GET /api/v1/health`
  - `GET /api/v1/runtime`
  - `GET /api/v1/state`
  - `GET /api/v1/<issue_identifier>`
  - `POST /api/v1/refresh`

## User Guide

See the full guide at [docs/UserGuide.md](/C:/s/ReleasedGroup/Symphony/docs/UserGuide.md).

## Runtime Behavior

- GitHub issue normalization now includes linked branch metadata, blocker references, milestone data, and optional PR metadata for prompt rendering and orchestration.
- SQLite persists workflow snapshots, issue cache, runs, run attempts, sessions, retry queue entries, workspace records, event log entries, leases, and dispatch claims for restart recovery and debugging.
- Dispatch enforces exact active-state matching, per-state concurrency caps, continuation retries, exponential-backoff retries, and the `Todo` blocker rule.
- Reconciliation refreshes active issue states every tick, stops non-active or terminal runs, cleans terminal workspaces, and reschedules stalled runs from the last Codex activity timestamp.
- Codex app-server sessions now support streamed multi-turn execution on a shared thread, permissive auto-approval, structured unsupported-tool failures, and the `github_graphql` client-side tool.
- Runtime state, recent events, token totals, and latest rate-limit payloads are available through the HTTP API and are derived from persisted orchestrator state.

## Build and Test

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' restore Symphony.slnx
& 'C:\Program Files\dotnet\dotnet.exe' build Symphony.slnx
& 'C:\Program Files\dotnet\dotnet.exe' test Symphony.slnx --no-build
```

Opt-in real GitHub integration:

```powershell
$env:SYMPHONY_RUN_REAL_INTEGRATION_TESTS = "1"
$env:GITHUB_TOKEN = "<token>"
& 'C:\Program Files\dotnet\dotnet.exe' test tests/Symphony.Integration.Tests/Symphony.Integration.Tests.csproj --filter RealIntegrationTests
```

## Running Locally

From the repository root, the host defaults to `./WORKFLOW.md` in the current working directory:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project src/Symphony.Host -- ./WORKFLOW.md
```

You can omit the positional path when the current working directory already contains `WORKFLOW.md`.

HTTP port precedence is:

- CLI `--port <value>`
- `server.port` in `WORKFLOW.md`
- standard ASP.NET Core URL configuration when neither override is set

When `--port` or `server.port` is used, Symphony binds loopback on `127.0.0.1`.

## Local Tooling

This repository uses a local `dotnet-ef` tool manifest.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' tool restore
& 'C:\Program Files\dotnet\dotnet.exe' tool run dotnet-ef migrations list --project src/Symphony.Infrastructure.Persistence.Sqlite --startup-project src/Symphony.Host
```
