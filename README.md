# Symphony

Symphony is a `.NET 10` service that orchestrates coding-agent work from GitHub issues.

Current scaffold includes:

- Worker + HTTP API host (`src/Symphony.Host`)
- Responsive dashboard UI at `GET /`
- EF Core + SQLite persistence baseline with migrations
- Multi-project architecture (`Core`, infrastructure adapters, tests)
- Runtime endpoints:
  - `GET /`
  - `GET /api/v1/health`
  - `GET /api/v1/runtime`
  - `GET /api/v1/state`
  - `GET /api/v1/<issue_identifier>`
  - `POST /api/v1/refresh`

## User Guide

See the full guide at [docs/UserGuide.md](docs/UserGuide.md).

Container deployment guidance and sample artifacts are in [docs/ContainerGuide.md](docs/ContainerGuide.md).

## Runtime Behavior

- A Tailwind-powered dashboard now renders orchestration health, live agent activity, tracked issue distribution, rate limits, leases, and per-issue drill-down from the durable API state.
- GitHub issue normalization now includes linked branch metadata, blocker references, milestone data, and optional PR metadata for prompt rendering and orchestration.
- SQLite persists workflow snapshots, issue cache, runs, run attempts, sessions, retry queue entries, workspace records, event log entries, leases, and dispatch claims for restart recovery and debugging.
- Dispatch enforces exact active-state matching, per-state concurrency caps, continuation retries, exponential-backoff retries, and the `Todo` blocker rule.
- Reconciliation refreshes active issue states every tick, stops non-active or terminal runs, cleans terminal workspaces, and reschedules stalled runs from the last Codex activity timestamp.
- Codex app-server sessions now support streamed multi-turn execution on a shared thread, permissive auto-approval, structured tool-call failures, and the `github_graphql` client-side tool.
- Runtime state, tracked issue distribution, recent events, lease snapshots, token totals, and latest rate-limit payloads are available through the HTTP API and are derived from persisted orchestrator state.

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

Set `GITHUB_TOKEN` in the same shell session that will launch the host. With `tracker.api_key: $GITHUB_TOKEN`, Symphony fails fast at startup if that process cannot resolve the variable.

For local development from the repository root, the checked-in launch profile already points the host at the repo `WORKFLOW.md`:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project src/Symphony.Host
```

If you disable launch profiles or want to pass the workflow path explicitly, use the path relative to `src/Symphony.Host`:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project src/Symphony.Host -- ../../WORKFLOW.md
```

When the host process starts without an explicit path, the CLI default remains `WORKFLOW.md` in the process working directory.

The default SQLite connection string is `Data Source=./data/symphony.db;...`, so `dotnet run --project src/Symphony.Host` creates the database under `src/Symphony.Host/data/` if it does not already exist.

Open the dashboard at `http://127.0.0.1:<port>/` or the raw health probe at `http://127.0.0.1:<port>/api/v1/health`.

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

To rebuild the dashboard CSS after editing the Tailwind source:

```powershell
Set-Location src/Symphony.Host
npm install
npm run build:css
```
