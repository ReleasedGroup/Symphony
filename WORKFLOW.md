---
tracker:
  kind: github
  endpoint: https://api.github.com/graphql
  api_key: $GITHUB_TOKEN
  owner: releasedgroup
  repo: nextmedia-manager-copilot
  milestone: null
  include_pull_requests: true
  labels: []
  active_states:
    - Open
    - In Progress
  terminal_states:
    - Closed
polling:
  interval_ms: 600000
agent:
  max_concurrent_agents: 5
  max_turns: 20
  max_retry_backoff_ms: 300000
  max_concurrent_agents_by_state: {}
server:
  # Optional HTTP override. CLI --port wins when both are present.
  port: null
codex:
  command: codex app-server
  turn_timeout_ms: 3600000
  approval_policy: never
  thread_sandbox: danger-full-access
  turn_sandbox_policy: danger-full-access
  read_timeout_ms: 5000
  stall_timeout_ms: 300000
workspace:
  root: /s/workspaces
  shared_clone_path: /s/workspaces/repo
  worktrees_root: /s/workspaces/worktrees
  base_branch: main
  remote_url: null
hooks:
  after_create: null
  before_run: null
  after_run: null
  # Runs during workspace cleanup (startup terminal sweep).
  before_remove: null
  timeout_ms: 60000
---

You are working on a GitHub issue for this repository.

- Read the issue details, labels, milestone, and linked pull request context. Ensure you read any comments and also any embedded images or attachments.
- If this change is dependent on a change that has not yet completed, wait until that change complets before starting.
- Implement the requested change in the current worktree.
- Run relevant build/tests before completion.
- Keep changes minimal, correct, and safe.
- Ensure on completion you publish a PR with detailed descriptions of what was done and mark it as ready to review. Also add the text Closes #xx where xx is the issue number.
- Add a comment to the issue to show you are working on it
- Once you publish a PR, Copilot will review it and add comments. This may take some time.
- Once Copilot has commented, review and resolve the comments
- Once the comments are resolved, build and test works and there are no more issues, merge the PR
