# Symphony Package Guide

## Release Bundles

GitHub Releases now build versioned bundles for:

- Windows: `win-x64`, `win-arm64`
- Linux: `linux-x64`, `linux-arm64`
- macOS: `osx-x64`, `osx-arm64`

Each bundle is stamped from the release tag and attached back to the published GitHub Release.

The release workflow expects tags in one of these forms:

- `v1.2.3`
- `v1.2.3-rc.1`

## First-Run Setup

After extracting a bundle, start the interactive installer:

- Windows: `.\setup-symphony.cmd`
- macOS/Linux: `./setup-symphony.sh`

You can also run the binary directly:

- Windows: `.\Symphony.exe install`
- macOS/Linux: `./Symphony install`

The text-mode installer prompts for:

- GitHub token
- GitHub owner
- GitHub repository
- Base branch
- Instance folder
- HTTP port

Before the installer auto-starts Symphony, it also checks the local Codex CLI:

- `codex --version` must be present and at least the Symphony-validated version
- if npm can be reached, the installed CLI must not be behind the latest `@openai/codex` version
- Codex authentication must succeed via `codex login status`
- `auth.json` must exist under `~/.codex/` (or `CODEX_HOME` when set)

If one of those checks fails, the installer pauses and tells you what to fix before the first start. With `--no-launch`, installation still completes, but the installer prints the Codex prerequisites you need to fix before running the instance later.

It then creates an isolated instance with:

- its own `WORKFLOW.md`
- its own `appsettings.json`
- a local `.env` containing `GITHUB_TOKEN`
- a local SQLite database under `./data/`
- isolated git workspaces under `./workspaces/`
- instance-local run scripts

Each instance gets its own stable `Orchestration:InstanceId` and its own loopback URL such as `http://127.0.0.1:43123/`, so multiple instances can run on the same machine without sharing runtime state.

## Re-running an Installed Instance

From the instance folder:

- Windows: `.\run-symphony.cmd`
- macOS/Linux: `./run-symphony.sh`

You can also run `Symphony version` to confirm the packaged build version.

## Managed, Noninteractive Installation

Device agents can install without prompts or a plaintext token argument. Prepare an existing `WORKFLOW.md` whose `tracker.api_key` is an environment reference such as `$SYMPHONY_GITHUB_TOKEN`, and an `appsettings.json` JSON object. The install command copies these files into a new instance directory. It sets the requested stable instance ID, a local SQLite database, and the installed workflow path in the copied config. The requested loopback port is used by the generated run scripts.

```text
Symphony install --managed \
  --instance-id device-01 \
  --instance-dir /srv/symphony/device-01 \
  --workflow-path /srv/symphony/source/WORKFLOW.md \
  --config-path /srv/symphony/source/appsettings.json \
  --port 43123 \
  --github-token-env SYMPHONY_GITHUB_TOKEN \
  --preflight-only
```

Remove `--preflight-only` to install. The command returns one JSON object on stdout with `status`, `code`, instance details, and Codex readiness. It refuses an existing target directory, a target inside the package bundle, an unavailable loopback port, an invalid workflow or config, or a workflow whose token reference differs from `--github-token-env`. It does not overwrite an instance. Successful installation reports `started: false`; add `--launch` to start immediately. Preflight only checks and writes nothing.

Set the named token environment variable for the account that runs Symphony. That account needs read/write access to the instance directory, its SQLite database and workspaces, and access to git, the repository, and the Codex CLI. Install a supported Codex CLI version and complete `codex login` for that same account; the agent checks both CLI version and authentication before launch. Provision the environment variable through the operating system or service manager. The managed installer does not write a `.env` file or print the token value. On Windows, configure a service identity with access to these resources; on macOS and Linux, use an equivalent dedicated service account and service manager. Use the generated `run-symphony.cmd` or `run-symphony.sh` for subsequent starts.

The managed install tests run on Windows, macOS, and Linux runners, and CI publishes each of the six release RIDs: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, and `linux-arm64`.
