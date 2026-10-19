#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
native_library_dir="$($repository_root/scripts/prepare-mpv-spike.sh | tail -n 1)"
output_dir="$repository_root/artifacts/spikes/mpv-avalonia"
media_file="$output_dir/test-source.mkv"
project="$repository_root/spikes/CanX.Spikes.MpvAvalonia/CanX.Spikes.MpvAvalonia.csproj"

mkdir -p "$output_dir"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -t 3 -c:v ffv1 "$media_file"

dotnet publish "$project" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained \
  --output "$output_dir"

smoke_output="$(LD_LIBRARY_PATH="$native_library_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  timeout 30s "$output_dir/CanX.Spikes.MpvAvalonia" --smoke "$media_file")"
printf '%s\n' "$smoke_output"
grep -q 'CANX_MPV_RENDER_OK backend=Wayland' <<<"$smoke_output"

if [[ -n "${DISPLAY:-}" ]]; then
  fallback_output="$(WAYLAND_DISPLAY=canx-invalid \
    LD_LIBRARY_PATH="$native_library_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    timeout 30s "$output_dir/CanX.Spikes.MpvAvalonia" --smoke "$media_file")"
  printf '%s\n' "$fallback_output"
  grep -q 'CANX_MPV_RENDER_OK backend=XID' <<<"$fallback_output"
fi
