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
