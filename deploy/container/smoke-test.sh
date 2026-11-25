#!/usr/bin/env bash
set -euo pipefail
image="${1:?Pass the local or pulled Symphony image}"
docker run --rm --network none --entrypoint /bin/sh "$image" -ec '
  test "$(id -u)" -ne 0
  test "$(uname -m)" = x86_64
  test -s /app/Symphony.Host.dll
  test -x /entrypoint.sh
  git --version
  gh --version
  node --version
  npm --version
  codex --version
  codex app-server --help >/dev/null
'
