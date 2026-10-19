#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_output="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"
work_dir="$(mktemp -d /tmp/canx-playback-stream.XXXXXX)"

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

for tool in dotnet ffmpeg timeout; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing playback stream verification tool: $tool" >&2
    exit 2
  }
done

dotnet build "$repository_root/src/CanX.App/CanX.App.csproj" \
  -c Release -r linux-x64 --self-contained false --no-restore

if [[ -x "$app_output/CanX.App" ]]; then
  app_command=("$app_output/CanX.App")
elif [[ -f "$app_output/CanX.App.dll" ]]; then
  app_command=(dotnet "$app_output/CanX.App.dll")
else
  echo "CanX application not found in: $app_output" >&2
  exit 1
fi

media_file="$work_dir/stream-callback-fixture.mkv"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -f lavfi -i 'sine=frequency=440:sample_rate=48000' \
  -t 8 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
  -c:a aac -b:a 64k "$media_file"

run_smoke() {
  local backend="$1"
  shift
  local output
  if ! output="$(CANX_DATA_DIRECTORY="$work_dir/data-$backend" "$@" timeout 60s \
    "${app_command[@]}" --playback-stream-smoke "$media_file" 2>&1)"; then
    printf '%s\n' "$output" >&2
    return 1
  fi

  printf '%s\n' "$output"
  grep -Eq 'CANX_PLAYBACK_STREAM_OK frames=[1-9][0-9]* opens=[1-9][0-9]* reads=[1-9][0-9]* seeks=[2-9][0-9]* sizes=[1-9][0-9]* cancels=[1-9][0-9]* closes=[1-9][0-9]* eof=[1-9][0-9]*' <<<"$output"
  printf 'CANX_PLAYBACK_STREAM_BACKEND_OK backend=%s\n' "$backend"
}

if [[ -n "${WAYLAND_DISPLAY:-}" ]]; then
  run_smoke Wayland env
fi

if [[ -n "${DISPLAY:-}" ]]; then
  run_smoke X11 env CANX_FORCE_X11=1
fi

if [[ -z "${WAYLAND_DISPLAY:-}" && -z "${DISPLAY:-}" ]]; then
  command -v xvfb-run >/dev/null 2>&1 || {
    echo "neither a graphical session nor xvfb-run is available" >&2
    exit 2
  }
  run_smoke X11 xvfb-run -a -s '-screen 0 1320x820x24' env CANX_FORCE_X11=1
fi

echo 'CANX_PLAYBACK_STREAM_ALL_OK platform=linux-x64'
