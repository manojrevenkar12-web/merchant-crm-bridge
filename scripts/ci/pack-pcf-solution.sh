#!/usr/bin/env bash
# Packages the PCF control into a Dataverse solution (managed and unmanaged) with the Power Platform CLI.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
out="$root/artifacts/MerchantCrmBridgeControls"
rm -rf "$out" && mkdir -p "$out" && cd "$out"
pac solution init --publisher-name merchantcrmbridge --publisher-prefix mcb
pac solution add-reference --path "$root/pcf/MerchantSyncStatus"
dotnet build -c Release -p:SolutionPackageType=Both
find bin -name "*.zip" -print
