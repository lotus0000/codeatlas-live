#!/usr/bin/env bash
# Builds the viewer into the collector's wwwroot, which the collector serves. Nothing is downloaded at run time.
set -euo pipefail
cd "$(dirname "$0")/../src/viewer"
npm ci --silent
npm run build --silent
out=../CodeAtlas.Collector/wwwroot
test -f "$out/index.html" || { echo "build-viewer: $out/index.html missing" >&2; exit 1; }
echo "viewer built into src/CodeAtlas.Collector/wwwroot"
