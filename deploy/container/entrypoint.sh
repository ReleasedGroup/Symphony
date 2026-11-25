#!/bin/sh
set -eu

if [ "$#" -gt 0 ]; then
  exec dotnet Symphony.dll "$@"
fi

exec dotnet Symphony.dll "${SYMPHONY_WORKFLOW_PATH:-/config/WORKFLOW.md}"
