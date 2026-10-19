#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
native_build_dir="$($repository_root/scripts/prepare-quickjs-spike.sh | tail -n 1)"
output_dir="$repository_root/artifacts/spikes/quickjs-interop"
project="$repository_root/spikes/CanX.Spikes.QuickJsInterop/CanX.Spikes.QuickJsInterop.csproj"

dotnet publish "$project" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained \
  --output "$output_dir"

smoke_output="$(LD_LIBRARY_PATH="$native_build_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  timeout 20s "$output_dir/CanX.Spikes.QuickJsInterop")"
printf '%s\n' "$smoke_output"
grep -q 'CANX_QUICKJS_OK' <<<"$smoke_output"
