#!/usr/bin/env bash
set -euo pipefail
image="${1:?Pass the local or pulled Symphony image}"
docker run --rm --network none --entrypoint /bin/sh "$image" -ec '
  test "$(id -u)" -ne 0
  test "$(uname -m)" = x86_64
  test -s /app/Symphony.dll || { echo "Published Symphony.dll missing" >&2; exit 1; }
  test -x /entrypoint.sh
  git --version
  gh --version
  node --version
  npm --version
  codex --version
  codex app-server --help >/dev/null
'

# Exercise the image's actual entrypoint and argument forwarding without credentials.
version="$(docker run --rm --network none "$image" --version)"
printf '%s\n' "$version"
grep -q '^Symphony ' <<< "$version"
