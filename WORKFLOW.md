---
tracker:
  kind: github
  endpoint: https://api.github.com/graphql
  api_key: $GITHUB_TOKEN
  owner: your-github-owner
  repo: your-github-repo
  milestone: null
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
codex:
  command: codex app-server
  timeout_ms: 3600000
workspace:
  root: ./workspaces
  shared_clone_path: ./workspaces/repo
  worktrees_root: ./workspaces/worktrees
  base_branch: main
  remote_url: null
---

You are working on a GitHub issue for this repository.

- Read the issue details, labels, milestone, and linked pull request context.
- Implement the requested change in the current worktree.
- Run relevant build/tests before completion.
- Keep changes minimal, correct, and safe.
