#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="$repository_root/artifacts/spikes/avalonia-aot"
project="$repository_root/spikes/CanX.Spikes.AvaloniaAot/CanX.Spikes.AvaloniaAot.csproj"

dotnet publish "$project" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained \
  --output "$output_dir"

smoke_output="$(timeout 20s "$output_dir/CanX.Spikes.AvaloniaAot" --smoke)"
printf '%s\n' "$smoke_output"
grep -q 'CANX_AVALONIA_AOT_OK' <<<"$smoke_output"

if [[ -n "${DISPLAY:-}" ]]; then
  fallback_output="$(WAYLAND_DISPLAY=canx-invalid timeout 20s "$output_dir/CanX.Spikes.AvaloniaAot" --smoke)"
  printf '%s\n' "$fallback_output"
  grep -q 'backend=XID' <<<"$fallback_output"
fi

file "$output_dir/CanX.Spikes.AvaloniaAot"
