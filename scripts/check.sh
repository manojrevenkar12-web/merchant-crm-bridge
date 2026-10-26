#!/usr/bin/env bash
# Fast feedback before a push: build everything that builds offline, run every test project,
# check the committed metadata plan and deployment settings. CI runs the same and more.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build merchant-crm-bridge.slnx -v q -nologo
for p in tests/Bridge.Tests tests/Bridge.Api.Tests tests/Bridge.Kafka.Tests; do
  dotnet test --project "$p" --no-build
done
dotnet run --project src/Bridge.Alm --no-build -- settings --check alm/settings
if [ -d web/node_modules ]; then (cd web && npm run -s check); fi
echo "all checks passed"
