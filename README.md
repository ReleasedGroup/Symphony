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

## Local Tooling

This repository uses a local `dotnet-ef` tool manifest.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' tool restore
& 'C:\Program Files\dotnet\dotnet.exe' tool run dotnet-ef migrations list --project src/Symphony.Infrastructure.Persistence.Sqlite --startup-project src/Symphony.Host
```
