#!/usr/bin/env bash
# Stands in for code-server in the E2E suite: records the arguments the collector passes, opens nothing.
mkdir -p "$(dirname "${CODEATLAS_RECORDER_FILE:?}")"
printf '%s\n' "$@" > "$CODEATLAS_RECORDER_FILE"
