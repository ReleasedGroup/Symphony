# AGENTS.md

## Mission

Implement and maintain Symphony according to `SPEC.md` using:

- `.NET 10` (`net10.0`)
- SQLite for durable orchestrator state
- GitHub as the source of truth for issues, pull requests, milestones, and repository versioning

## Source of Truth

- Functional behavior: `SPEC.md`
- Implementation sequencing: `IMPLEMENTATION_PLAN.md`
- If plan and spec conflict, follow `SPEC.md` and update the plan.

## Current Product Decisions

Locked on 2026-03-05:

1. Ship Worker + HTTP API in v1.
2. Use EF Core + SQLite.
3. Design for multi-instance safety.
4. Use GitHub PAT auth in v1.
5. Candidate filter includes state + label + milestone.
6. Dispatch only issues (no PR-only work items).
7. Success state uses GitHub issue `Closed`.
8. Use shared clone + Git worktrees for per-issue workspaces.
9. Use permissive auto-approve policy in v1.
10. Ship `github_graphql` extension in v1.
11. Default capacity: `5` agents, `10` minute poll interval.
12. Run as Windows Service in target deployment.

## Non-Negotiable Constraints

1. Do not weaken safety constraints from sections 9, 10, and 15 of the spec.
2. Keep workspace path containment checks mandatory.
3. Keep protocol parsing strict on stdout; never parse stderr as protocol events.
4. Never log secrets (`GITHUB_TOKEN`, workflow secrets, auth headers).
5. Track writes are agent/tool driven; do not add hidden orchestrator-side business writes unless explicitly requested.

## Architecture Rules

1. Keep clear boundaries:
- Core domain/orchestration logic must not depend on concrete infra APIs.
- Infrastructure adapters implement interfaces from core.

2. Prefer composition over shared mutable globals:
- Orchestrator state must be explicit and testable.
- Background services should use scoped dependencies per tick/run.

3. Persistence:
- Use SQLite with migrations.
- Persist only state needed for recovery, observability, and debugging.
- Include DB-backed lease/claim semantics for multi-instance safety.

4. GitHub integration:
- Use GraphQL endpoint by default (`https://api.github.com/graphql`).
- Normalize all tracker payloads to the spec domain model before use.
- Use PAT auth for v1.
- Filter candidates by configured state + label + milestone.
- Exclude PR-only items from dispatch.

## Coding Standards

- C# latest language version supported by .NET 10 SDK.
- Nullable reference types enabled.
- Async all the way for I/O paths.
- Cancellation tokens respected in polling, subprocess, and HTTP calls.
- Keep classes focused and small; split large orchestration behaviors into feature services.
- Prefer built-in ASP.NET Core and .NET primitives over third-party packages unless justified.

## Config and Options

- Read workflow from `WORKFLOW.md`.
- Resolve `$ENV_VAR` values in config.
- Fail fast on invalid required config.
- Validate options at startup and before dispatch cycles where required by spec.
- Default to `max_concurrent_agents=5` and `polling.interval_ms=600000` unless explicitly overridden.

## Testing Expectations

Minimum for any non-trivial change:

1. Unit tests for business logic and state transitions.
2. Integration tests for infra boundaries touched (SQLite, GitHub adapter, protocol client).
3. Update/add conformance tests mapped to section 17 of `SPEC.md`.

Prefer SQLite-backed integration tests over fake in-memory DB providers.

## Observability Expectations

- Structured logs with issue/session correlation fields.
- Clear event names for dispatch, retry, stop, cleanup, and protocol errors.
- Snapshot/status output must be derived from orchestrator state, not ad hoc caches.
- Include lease ownership/heartbeat visibility for multi-instance troubleshooting.

## Delivery Workflow

1. Start by citing relevant spec section(s) in PR description.
2. Implement smallest vertical slice that can be validated.
3. Add or update tests.
4. Run build + tests locally before handing off.
5. Document behavior changes in `README.md` or `docs/` when applicable.

## Suggested Commands

```powershell
dotnet restore
dotnet build
dotnet test
```

If migrations are used:

```powershell
dotnet ef migrations add <Name> --project src/Symphony.Infrastructure/Persistence.Sqlite
dotnet ef database update --project src/Symphony.Infrastructure/Persistence.Sqlite
```

## Definition of Done (Per Change)

1. Behavior aligns with `SPEC.md`.
2. Tests prove the behavior or failure mode.
3. Logging and error paths are explicit.
4. No secrets exposed.
5. Reviewer can trace change from spec clause to implementation.
