# Upgrade Symphony in WSL without replacing your configuration

Keep the instance directory and its data in place. Extract a new release into a
separate staging directory, stop the drained instance, back it up, then update
**only the application payload**. Do not unpack a release over your instance or
rerun first-time setup as an upgrade.

## Checked on this machine, 28 October 2026

- Distribution: `Ubuntu-26.04`; architecture: `x86_64` → release RID `linux-x64`.
- Active process executable and working directory: `/home/nickbeau/symphony-fpsoftware`.
  That executable reports `Symphony 0.1.12`. Another installation,
  `/home/nickbeau/symphony-bceen`, reports 0.1.11; do not mix their state/configuration.
- The active process was a child of a Bash process. `symphony.service` reported
  inactive/MainPID 0. Its exact launcher/supervisor was not established, so no
  particular systemd stop/start command is assumed below.
- Codex binary used by the observed worker reports 0.165.0.
- The active instance's shared clone and seven registered issue worktrees were
  under `workspaces/repo` and `workspaces/worktrees`. Preserve those absolute paths.
- Installed configuration contents and secrets were not inspected. The installer
  normally uses `WORKFLOW.md`, `appsettings.json`, `.env`, `data/symphony.db`, and
  `run-symphony.sh`. **Confirm your actual external paths and overrides privately**.

The commands below are for a future maintenance window, not actions performed by
this investigation. Run them in Bash inside that WSL distribution as the account
that normally runs Symphony. Windows paths such as `C:\...` are not Linux paths;
keep the live instance in `/home/...`, not in a newly relocated Windows folder.

## 1. Stage and verify the release while current work continues

Read the target release notes for required Codex versions, configuration changes
and database migrations. Use the published release, not an arbitrary build.
Download `Symphony-<VERSION>-linux-x64.tar.gz` **and its `.sha256` file** from the
[Symphony releases page](https://github.com/ReleasedGroup/Symphony/releases).
The package workflow generates those names and SHA-256 files. Substitute the actual
version and fresh directories below; do not type the angle-bracket placeholders.

```bash
cd /path/to/downloads
sha256sum -c 'Symphony-<VERSION>-linux-x64.tar.gz.sha256'
mkdir -p '/home/nickbeau/symphony-releases/<VERSION>'
tar -xzf 'Symphony-<VERSION>-linux-x64.tar.gz' \
  -C '/home/nickbeau/symphony-releases/<VERSION>'
'/home/nickbeau/symphony-releases/<VERSION>/Symphony' version
```

Use an empty staging directory. Stop if the checksum fails or version is not the
expected release. A checksum from the same trusted release detects download damage;
it is not an independent publisher signature. Do not launch the staged server:
it contains example configuration and could start a second worker against the wrong
state. `version` is the read-only version subcommand.

## 2. Drain, then stop the correct instance

Use its existing loopback dashboard/API address; do not expose a new port. The
management API supports drain (pause dispatch and wait for running attempts):

```bash
BASE_URL='http://127.0.0.1:<YOUR_EXISTING_PORT>'
curl -sS -X POST "$BASE_URL/api/v1/management/drain?timeout_ms=300000"
curl -fsS "$BASE_URL/api/v1/management/status"
```

Wait for `paused: true`, `activeRuns: 0`, `quiescent: true`. HTTP 202 means the
drain timed out while work remains; it does **not** mean safe to stop. Repeat the
status check and let jobs finish. Pending retries may remain and will be preserved.
Quiescent counts running rows, so also allow worker finalization/hooks to complete
and confirm no active worker lifetime lease before taking the offline backup.
If a managed backplane supervises the instance, put it into its maintenance workflow
so it does not relaunch/resume it unexpectedly.

Stop through the **actual existing launcher** after drain. For a foreground process,
use Ctrl+C in its original terminal and wait for exit. If a service manager really
owns it, use that manager's normal stop action. Do not use `killall codex`, terminate
WSL, or stop unrelated instances. Pause alone is not a filesystem freeze: cleanup
and reconciliation can continue while paused. Back up only after process exit and
all worker finalization. No restart/upgrade has been performed for you.

## 3. Back up the complete stopped instance

Keep the backup outside the instance with owner-only access; it includes credentials,
SQLite state, local branches and potentially unpublished work. Check available disk
space first. Example (the timestamp makes a new archive):

```bash
umask 077
mkdir -p /home/nickbeau/symphony-backups
tar -czf "/home/nickbeau/symphony-backups/fpsoftware-$(date -u +%Y%m%dT%H%M%SZ).tar.gz" \
  -C /home/nickbeau symphony-fpsoftware
```

Confirm the archive can be listed/read and record its filename. Include separately
any configured external database/workspace/config paths or service secret source.
Preserve all SQLite files together (`.db`, and any `-wal`/`-shm` sidecars); copying
only a live `.db` is not a consistent backup. Do not email this archive or upload
its secrets. Preserve the original directory layout: Git worktrees contain
backreferences to their shared clone and registrations contain worktree paths.

## 4. Update the payload without overwriting configuration

Preserve at least `WORKFLOW.md`, `.env`, all `appsettings*.json`, `data/`, `workspaces/`,
your generated `run-symphony.sh`/`.cmd`, and any custom scripts, logs or external
state. Keep the instance ID, workflow path, database connection and loopback port.
Compare release defaults separately for documented new requirements; do not replace
your files with the defaults. No universal in-place `upgrade` subcommand is assumed.

If `rsync` is installed, preview this payload copy first. The source must be the
verified fresh release staging directory. Exclusions are deliberately conservative;
add exclusions for any custom files whose names overlap release files.

```bash
INSTANCE=/home/nickbeau/symphony-fpsoftware
STAGED='/home/nickbeau/symphony-releases/<VERSION>'
rsync -a --dry-run --itemize-changes \
  --exclude='/WORKFLOW.md' --exclude='/.env*' --exclude='/appsettings*.json' \
  --exclude='/data/' --exclude='/workspaces/' --exclude='/logs/' \
  --exclude='/run-symphony.sh' --exclude='/run-symphony.cmd' \
  "$STAGED/" "$INSTANCE/"
```

Review every listed overwrite. Then repeat the exact command without `--dry-run`.
Do not add `--delete`: it could remove instance-only state. If release notes require
removing obsolete binaries, handle only their exact named payload files after backup.
If `rsync` is unavailable, copy only the verified release payload with the same
exclusions; do not improvise a recursive overwrite of the whole release folder.

## 5. Start paused, validate, then resume

From the unchanged instance directory, use its existing launcher and account:

```bash
cd /home/nickbeau/symphony-fpsoftware
./Symphony version
./run-symphony.sh
```

Use the real service manager instead when one is configured; do not launch a second
copy beside it. The generated script loads the instance `.env`, changes to the
instance directory, and executes Symphony; preserve any local customizations.
Host startup validates the workflow and automatically applies SQLite migrations.
This is why the offline backup must precede the first new-version start.

In a second terminal, check the same existing address:

```bash
curl -fsS "$BASE_URL/api/v1/health"
curl -fsS "$BASE_URL/api/v1/management/status"
```

Confirm the expected version in the dashboard, correct repository and configured
five-worker capacity, preserved workflow prompt/hooks, preserved pause state and
retry history, successful migrations, and no new startup/auth/config errors. Check
only redacted logs. Health alone does not prove agent dispatch or Git authentication.
Cleanup can run even while paused; if a release changes cleanup behavior, review
its migration/lifecycle notes before starting it against production state.

When satisfied, resume from the dashboard or:

```bash
curl -fsS -X POST "$BASE_URL/api/v1/management/resume"
```

Observe the first normal issue through workspace preparation and Codex session start.
Do not create an artificial production job for the smoke test. Keep the backup until
you have confidence in the release.

## If the upgrade fails

Keep dispatch paused and stop the new process normally. Save redacted diagnostics.
An older executable may not understand a database migrated by the new release:
**do not simply swap binaries back against the migrated database**. The recovery
point is the old payload plus its matching stopped-instance backup, restored to the
same original paths after preserving any post-upgrade changes separately. If jobs
have run since the backup, assess their local commits and GitHub actions before
restoring; remote issue/PR changes are not rolled back by restoring local files.
Test recovery on a disconnected copy when compatibility is uncertain.

This procedure is separate from the proposed `workspace.cleanup` settings. Those
settings are not available merely by editing an older release's WORKFLOW.md.

Verified against repository sources: `docs/PackageGuide.md`,
`.github/workflows/release-packages.yml`, `SymphonyInstallCommand`,
`SymphonyHostApplication`, `ManagedControlService`, and `MigrationExtensions`.
