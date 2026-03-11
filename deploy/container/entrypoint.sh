#!/bin/sh
set -eu

if [ "$#" -gt 0 ]; then
  exec dotnet Symphony.Host.dll "$@"
fi

exec dotnet Symphony.Host.dll "${SYMPHONY_WORKFLOW_PATH:-/config/WORKFLOW.md}"
