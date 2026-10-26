#!/usr/bin/env bash
# Packages the PCF control into a Dataverse solution (managed and unmanaged) with the Power Platform CLI.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
# Outside the repository, so the repository's Directory.*.props (central package versions) do not apply.
work=$(mktemp -d)/MerchantCrmBridgeControls
mkdir -p "$work" && cd "$work"
pac solution init --publisher-name merchantcrmbridge --publisher-prefix mcb
pac solution add-reference --path "$root/pcf/MerchantSyncStatus"
dotnet build -c Release -p:SolutionPackageType=Both
mkdir -p "$root/artifacts/pcf-solution"
find bin -name "*.zip" -print -exec cp {} "$root/artifacts/pcf-solution/" \;
