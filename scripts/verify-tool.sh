#!/usr/bin/env bash
# Installs the codeatlas tool from the local feed into a temp folder, starts it, and checks it answers on loopback only.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'kill "${pid:-}" 2>/dev/null || true; rm -rf "$work"' EXIT

"$root/scripts/pack-local.sh" > /dev/null
export NUGET_PACKAGES="$work/packages"
# dotnet tools find the runtime through DOTNET_ROOT when .NET is a per-user install (e.g. ~/.dotnet).
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
dotnet tool install CodeAtlas.Collector --version 0.1.0 --tool-path "$work/tool" --add-source "$root/artifacts/nuget" > /dev/null

port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
"$work/tool/codeatlas" --urls "http://127.0.0.1:$port" --source-root "$root" > "$work/run.log" 2>&1 &
pid=$!
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$port/api/info" > "$work/info.json" && break; sleep 0.2; done

failed=0
check() { if eval "$2"; then echo "PASS  $1"; else echo "FAIL  $1"; failed=1; fi; }
check "tool answers api/info"                  'grep -q "\"version\":\"0.1.0\"" "$work/info.json"'
set +e; "$work/tool/codeatlas" --urls "http://0.0.0.0:$port" > "$work/refuse.log" 2>&1; refused=$?; set -e
check "tool refuses a non-loopback address with exit code 2"  '[ "$refused" = 2 ] && grep -q "^codeatlas: .*loopback" "$work/refuse.log"'
check "the refusal is a message, not a crash"                 '! grep -q "Unhandled exception" "$work/refuse.log"'
exit $failed
