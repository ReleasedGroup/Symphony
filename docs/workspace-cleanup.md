# Workspace lifecycle cleanup

Implements SPEC sections 8.4–8.6, 9, 10, 15 and 17. State, label and milestone
changes (including workflow filter edits) make retained work eligible for cleanup.
Running workers are canceled first. Removal takes place during reconciliation after
finalization and worker lease release, rather than using a worker's old workflow
snapshot. Missing tracker data or a failed refresh preserves the workspace.

Cleanup uses the existing durable `cleanup` dispatch claim. It prevents dispatch
of the same issue while removal is in progress, survives failed removal/restarts,
and is released when fresh tracker data confirms eligibility again. Startup scans
open and terminal issues; polling also discovers matching leftover directories
when worktrees remain. Unknown directories are never inferred to be obsolete from
absence in a candidate list. Existing worker and foreign-owner leases remain gates.

Immediately before removal, reconciliation refreshes eligibility again. A remote
change after that final read cannot be made atomic with local filesystem deletion;
a subsequent eligible dispatch recreates the workspace from the retained branch.

Git, hook and agent subprocesses have an owned lifetime. Windows private job objects
terminate contained descendants when the scope ends, including children whose
parent exited. Teardown waits up to ten seconds for job processes to exit. Processes
are assigned immediately after launch; children created before assignment are a
remaining Windows launch race. No process-name scanning or killing external lock
holders is performed. Non-Windows teardown uses the owned process tree.

Each removal attempts up to four times, waiting 200, 400 and 800 milliseconds between
failures. The cross-process clone lock waits at most ten seconds; each cleanup has
a bounded cancellation budget (30 seconds plus at most 60 seconds for its hook).
Persistent failures remain reserved and are retried on subsequent polls. Structured
`workspace_cleanup_retry`, `workspace_cleanup_preserved`, and existing cleanup
failure/deferred logs explain recovery decisions.

Containment is platform-aware. Parent identifiers, overlapping clone/worktree roots,
and symbolic links/junctions in the path or removal tree are refused. A worktree must
reference the configured shared clone. Uncommitted tracked changes and non-ignored
untracked files are preserved for manual recovery. Ignored build output remains
disposable. A sibling `.symphony-cleanup` marker records a verified clean worktree
before deletion, including file hashes. Later retries after `.git` is partially removed preserve any new or modified files rather than relying solely on the old path marker. Ownership and cleanliness are verified under the shared-clone lock before hooks, and again before removal; copied Git references must match the registration backpointer. Local
branches are retained. Unknown or dirty directories need operator review; they are
not forcibly erased. Workspace roots must remain writable only by trusted workers
and administrators; path checks are not a sandbox against concurrent hostile edits.

No database migration or service restart is needed to review/test this change.
Tests use temporary repositories, temporary SQLite files and disposable Windows
lock fixtures. Applying the new behavior to a deployed service remains a separate
operational action.
