#!/usr/bin/env bash
# The "Create Order" demo with real processes: Postgres in Docker (a dependency of the sample only — CodeAtlas needs
# no Docker), the collector, MinimalApiSample and its React app. Ctrl+C stops everything.
#
#   scripts/demo.sh
#   CODEATLAS_PUBLIC_BASE=https://code.example.com/absproxy scripts/demo.sh   # through code-server's /absproxy/<port>/
#   scripts/demo.sh --check                                                   # start, verify, stop
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
check=false
[ "${1:-}" = "--check" ] && check=true
collector_port="${COLLECTOR_PORT:-4318}"
api_port="${API_PORT:-5180}"
web_port="${WEB_PORT:-5181}"
db_port="${DB_PORT:-55433}"
base="${CODEATLAS_PUBLIC_BASE:-}"
base="${base%/}"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
logs="$root/artifacts/demo-logs"
mkdir -p "$logs"

for port in "$collector_port" "$api_port" "$web_port"; do
  if ss -ltn "sport = :$port" | grep -q LISTEN; then echo "demo: port $port is already in use" >&2; exit 1; fi
done

db=codeatlas-demo-db
pgids=()
cleanup() {
  # Each process runs in its own process group, so `dotnet run` and `vite` stop together with their children.
  for pgid in "${pgids[@]}"; do kill -TERM -- "-$pgid" 2> /dev/null || true; done
  docker stop "$db" > /dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM
start() { # start <log> <command...>
  local log="$1"; shift
  setsid "$@" > "$logs/$log" 2>&1 &
  pgids+=($!)
}
wait_for() { # wait_for <url> <what>
  for _ in $(seq 1 240); do curl -sf -o /dev/null "$1" && return 0; sleep 0.5; done
  echo "demo: $2 did not start; see $logs" >&2
  exit 1
}

echo "demo: building the viewer and the browser package…"
"$root/scripts/build-viewer.sh" > "$logs/build.log" 2>&1
(cd "$root/src/web" && npm ci --silent && npm run build --silent) >> "$logs/build.log" 2>&1
(cd "$root/samples/MinimalApiSample.Web" && npm ci --silent) >> "$logs/build.log" 2>&1

echo "demo: starting Postgres for the sample (Docker, port $db_port)…"
docker rm -f "$db" > /dev/null 2>&1 || true
docker run -d --rm --name "$db" -p "127.0.0.1:$db_port:5432" \
  -e POSTGRES_USER=codeatlas -e POSTGRES_PASSWORD=codeatlas-demo -e POSTGRES_DB=codeatlas_demo postgres:18 > /dev/null
for _ in $(seq 1 120); do
  docker exec "$db" psql -U codeatlas -d codeatlas_demo -c 'select 1' > /dev/null 2>&1 && break
  sleep 0.5
done

collector_args=(--urls "http://127.0.0.1:$collector_port" --source-root "$root/samples")
web_public=""
if [ -n "$base" ]; then
  collector_args+=(--public-url "$base/$collector_port/")
  web_public="$base/$web_port/"
fi

echo "demo: starting the collector, the sample API and the sample web app…"
start collector.log dotnet run --project "$root/src/CodeAtlas.Collector" -- "${collector_args[@]}"
start api.log env ASPNETCORE_URLS="http://127.0.0.1:$api_port" ASPNETCORE_ENVIRONMENT=Development \
  ConnectionStrings__Shop="Host=127.0.0.1;Port=$db_port;Database=codeatlas_demo;Username=codeatlas;Password=codeatlas-demo" \
  CodeAtlas__Endpoint="http://127.0.0.1:$collector_port/v1/traces" \
  dotnet run --project "$root/samples/MinimalApiSample" --no-launch-profile
start web.log env DEV_PUBLIC_URL="$web_public" SAMPLE_API="http://127.0.0.1:$api_port" \
  CODEATLAS_COLLECTOR="http://127.0.0.1:$collector_port" \
  bash -c "cd '$root/samples/MinimalApiSample.Web' && exec npx vite --port $web_port --strictPort --host 127.0.0.1"

web_path="/"
[ -n "$web_public" ] && web_path="$(python3 -c 'import sys,urllib.parse;print(urllib.parse.urlparse(sys.argv[1]).path)' "$web_public")"
wait_for "http://127.0.0.1:$collector_port/api/info" "the collector"
wait_for "http://127.0.0.1:$api_port/api/products" "the sample API"
wait_for "http://127.0.0.1:$web_port$web_path" "the sample web app"

app_url="${web_public:-http://127.0.0.1:$web_port/}"
viewer_url="http://127.0.0.1:$collector_port/"
[ -n "$base" ] && viewer_url="$base/$collector_port/"
echo
echo "  Window A — the app:     $app_url"
echo "  Window B — the viewer:  $viewer_url"
echo
echo "  Click \"Create Order\" in window A and watch window B. Logs: $logs"

if $check; then
  curl -sf "http://127.0.0.1:$web_port${web_path}api/products" | grep -q KB-01
  curl -sf "http://127.0.0.1:$collector_port/" | grep -q '<div id="root">'
  # A span sent through the app's proxy path reaches the collector.
  curl -sf -o /dev/null -X POST -H 'content-type: application/json' --data '{"resourceSpans":[]}' \
    "http://127.0.0.1:$web_port${web_path}codeatlas/v1/traces"
  echo "demo: check passed"
  exit 0
fi
wait
