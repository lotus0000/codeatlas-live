#!/usr/bin/env bash
# Builds the viewer into the collector's wwwroot, which the collector serves. Nothing is downloaded at run time.
set -euo pipefail
cd "$(dirname "$0")/../src/viewer"
npm ci --silent
npm run build --silent
out=../CodeAtlas.Collector/wwwroot
test -f "$out/index.html" || { echo "build-viewer: $out/index.html missing" >&2; exit 1; }
# §7.5: no outbound requests. Fonts, Monaco and its worker must be in the build, not fetched from a CDN.
if grep -rlE 'fonts\.googleapis|fonts\.gstatic|cdn\.jsdelivr|unpkg\.com|cdnjs' "$out" > /dev/null; then
  echo "build-viewer: the build references a CDN:" >&2
  grep -rlE 'fonts\.googleapis|fonts\.gstatic|cdn\.jsdelivr|unpkg\.com|cdnjs' "$out" >&2
  exit 1
fi
ls "$out"/assets/*worker*.js > /dev/null 2>&1 || { echo "build-viewer: Monaco editor worker missing from the build" >&2; exit 1; }
ls "$out"/assets/*.woff2 > /dev/null 2>&1 || { echo "build-viewer: bundled fonts missing from the build" >&2; exit 1; }
echo "viewer built into src/CodeAtlas.Collector/wwwroot"
