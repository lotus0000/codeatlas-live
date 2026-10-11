#!/usr/bin/env bash
# Packs the integration packages into ./artifacts/nuget (a local folder feed) and ./artifacts/npm. Nothing is published.
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf artifacts/nuget
dotnet pack src/CodeAtlas.Otlp -c Release -o artifacts/nuget --nologo -v q
dotnet pack src/CodeAtlas.AspNetCore -c Release -o artifacts/nuget --nologo -v q
dotnet pack src/CodeAtlas.Collector -c Release -o artifacts/nuget --nologo -v q
mkdir -p artifacts/npm
(cd src/web && npm ci --silent && npm run build --silent && npm pack --silent --pack-destination ../../artifacts/npm)
ls artifacts/npm
ls artifacts/nuget
