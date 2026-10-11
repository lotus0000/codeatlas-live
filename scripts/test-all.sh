#!/usr/bin/env bash
# Every automated check, in one command. Needs .NET 10, Node and Docker (Postgres for the samples and Testcontainers).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
step() { printf '\n== %s\n' "$1"; }

step "@codeatlas/web";        (cd src/web && npm ci --silent && npx vitest run && npx tsc -p tsconfig.json --noEmit && npm run build --silent)
step "viewer";                (cd src/viewer && npm ci --silent && npx vitest run && npx tsc -p tsconfig.json --noEmit)
step "sample web app";        (cd samples/MinimalApiSample.Web && npm ci --silent && npx vitest run && npx tsc -p tsconfig.json --noEmit)
step ".NET tests";            dotnet test
step "release builds";        scripts/verify-release.sh
step "packed tool";           scripts/verify-tool.sh
step "demo starts and stops"; COLLECTOR_PORT=24318 API_PORT=25180 WEB_PORT=25181 DB_PORT=55435 scripts/demo.sh --check
step "end to end";            tests/e2e/run.sh
printf '\nAll checks passed.\n'
