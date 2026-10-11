#!/usr/bin/env bash
# End-to-end tests with real processes: Postgres in Docker (the sample's database only), the collector, both samples,
# the sample web app and Chromium. Extra arguments go to Playwright (e.g. --grep "Create Order").
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"

db=codeatlas-e2e-db
docker rm -f "$db" > /dev/null 2>&1 || true
docker run -d --rm --name "$db" -p 127.0.0.1:55434:5432 \
  -e POSTGRES_USER=codeatlas -e POSTGRES_PASSWORD=codeatlas-e2e -e POSTGRES_DB=codeatlas_e2e postgres:18 > /dev/null
trap 'docker stop "$db" > /dev/null 2>&1 || true' EXIT
for _ in $(seq 1 120); do
  docker exec "$db" psql -U codeatlas -d codeatlas_e2e -c 'select 1' > /dev/null 2>&1 && break
  sleep 0.5
done

"$root/scripts/build-viewer.sh" > /dev/null
(cd "$root/src/web" && npm ci --silent && npm run build --silent)
(cd "$root/samples/MinimalApiSample.Web" && npm ci --silent)
cd "$root/tests/e2e"
npm ci --silent
rm -rf .recorder
npx playwright test "$@"
