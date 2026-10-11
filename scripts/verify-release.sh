#!/usr/bin/env bash
# Proves a target app's Release build contains no CodeAtlas code or CodeAtlas-only packages,
# and keeps the app's own OpenTelemetry. Debug is checked too, so a broken fixture cannot pass silently.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

"$root/scripts/pack-local.sh" > /dev/null
export NUGET_PACKAGES="$work/packages"   # never reuse a stale cached 0.1.0
fixture="$root/tests/release-fixture/ReleaseFixtureApp.csproj"
for configuration in Debug Release; do
  dotnet publish "$fixture" -c "$configuration" -o "$work/$configuration" --nologo -v q > /dev/null
done

failed=0
check() {
  if eval "$2"; then echo "PASS  $1"; else echo "FAIL  $1"; failed=1; fi
}
check "Debug contains CodeAtlas.AspNetCore.dll"              '[ -f "$work/Debug/CodeAtlas.AspNetCore.dll" ]'
check "Release has no CodeAtlas assemblies"                   '! ls "$work/Release" | grep -qi codeatlas'
# Exact names, not "codeatlas": the build path itself (…/codeatlas-live/…) is embedded in every assembly's PDB entry.
check "Release deps.json never mentions CodeAtlas packages"   '! grep -q "CodeAtlas\." "$work/Release/ReleaseFixtureApp.deps.json"'
check "Release app assembly has no CodeAtlas reference"       '! grep -aq "CodeAtlas.AspNetCore" "$work/Release/ReleaseFixtureApp.dll"'
check "Release has no Google.Protobuf (CodeAtlas-only here)"  '[ ! -f "$work/Release/Google.Protobuf.dll" ]'
check "Release keeps the app's OpenTelemetry.dll"             '[ -f "$work/Release/OpenTelemetry.dll" ]'
check "Release keeps the app's OTLP exporter"                 '[ -f "$work/Release/OpenTelemetry.Exporter.OpenTelemetryProtocol.dll" ]'
# The samples: Debug and Release published into separate folders from the same tree.
for sample in MinimalApiSample ControllerSample; do
  for configuration in Debug Release; do
    dotnet publish "$root/samples/$sample" -c "$configuration" -o "$work/$sample-$configuration" --nologo -v q > /dev/null
  done
  check "$sample Debug contains CodeAtlas.AspNetCore.dll"          "[ -f \"\$work/$sample-Debug/CodeAtlas.AspNetCore.dll\" ]"
  check "$sample Release has no CodeAtlas assemblies"               "! ls \"\$work/$sample-Release\" | grep -qi codeatlas"
  check "$sample Release deps.json never mentions CodeAtlas"         "! grep -q 'CodeAtlas\.' \"\$work/$sample-Release/$sample.deps.json\""
  check "$sample Release app assembly has no CodeAtlas reference"    "! grep -aq 'CodeAtlas.AspNetCore' \"\$work/$sample-Release/$sample.dll\""
done
check "ControllerSample Release keeps its own OpenTelemetry.dll"   '[ -f "$work/ControllerSample-Release/OpenTelemetry.dll" ]'

# The sample web app's production build.
(cd "$root/src/web" && npm ci --silent && npm run build --silent) > /dev/null
(cd "$root/samples/MinimalApiSample.Web" && npm ci --silent && npx vite build --outDir "$work/web" --emptyOutDir --logLevel silent) > /dev/null
check "sample web build contains the app"                          'grep -rq "Create Order" "$work/web/assets"'
check "sample web build has no CodeAtlas runtime or collector path" '! grep -rqE "traceparent|codeatlas-web|interaction: |codeatlas/v1" "$work/web/assets"'
exit $failed
