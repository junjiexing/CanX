#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="$repository_root/artifacts/tests/canx-app-ui"

dotnet test "$repository_root/tests/CanX.Foundation.Tests/CanX.Foundation.Tests.csproj" \
  --configuration Release
dotnet publish "$repository_root/src/CanX.App/CanX.App.csproj" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained \
  -p:CanXIncludeSmokeTests=true \
  --output "$output_dir"

smoke_output="$(timeout 20s "$output_dir/CanX.App" --smoke)"
printf '%s\n' "$smoke_output"
grep -q 'CANX_APP_SHELL_OK' <<<"$smoke_output"
