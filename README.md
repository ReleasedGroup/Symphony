# Symphony

Symphony is a `.NET 10` service that orchestrates coding-agent work from GitHub issues.

Current scaffold includes:

- Worker + HTTP API host (`src/Symphony.Host`)
- EF Core + SQLite persistence baseline with migrations
- Multi-project architecture (`Core`, infrastructure adapters, tests)
- Health and runtime endpoints:
  - `GET /api/v1/health`
  - `GET /api/v1/runtime`

## Build and Test

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' restore Symphony.slnx
& 'C:\Program Files\dotnet\dotnet.exe' build Symphony.slnx
& 'C:\Program Files\dotnet\dotnet.exe' test Symphony.slnx --no-build
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
