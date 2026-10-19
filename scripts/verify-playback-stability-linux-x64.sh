#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_output="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"
duration_seconds="${CANX_PLAYBACK_STABILITY_SECONDS:-120}"
work_dir="$(mktemp -d /tmp/canx-playback-stability.XXXXXX)"
skip_publish=false
expected_presentation_backend="${CANX_EXPECTED_PRESENTATION_BACKEND:-composition-shared}"

if [[ "${1:-}" == "--skip-publish" ]]; then
  skip_publish=true
elif [[ $# -ne 0 ]]; then
  echo "usage: $0 [--skip-publish]" >&2
  exit 2
fi

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

[[ "$duration_seconds" =~ ^[0-9]+$ ]] && (( duration_seconds >= 15 && duration_seconds <= 7200 )) || {
  echo "CANX_PLAYBACK_STABILITY_SECONDS must be between 15 and 7200" >&2
  exit 2
}

for tool in ffmpeg timeout; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing playback stability tool: $tool" >&2
    exit 2
  }
done

if [[ "$skip_publish" == false ]]; then
  CANX_INCLUDE_SMOKE_TESTS=true CANX_APP_OUTPUT="$app_output" \
    "$repository_root/scripts/publish-static-app-linux-x64.sh"
fi

[[ -x "$app_output/CanX.App" ]] || {
  echo "published CanX executable not found: $app_output/CanX.App" >&2
  exit 1
}

media_file="$work_dir/stability-fixture.mkv"
media_duration=$((duration_seconds + 30))
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -f lavfi -i 'sine=frequency=440:sample_rate=48000' \
  -t "$media_duration" -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
  -c:a aac -b:a 64k "$media_file"

run_stability() {
  local backend="$1"
  shift
  local output
  if ! output="$(CANX_DATA_DIRECTORY="$work_dir/data-$backend" "$@" \
    timeout "$((duration_seconds + 90))s" "$app_output/CanX.App" \
    --playback-stability-smoke "$media_file" "$duration_seconds" 2>&1)"; then
    printf '%s\n' "$output" >&2
    return 1
  fi
  printf '%s\n' "$output"
  grep -Eq 'CANX_PLAYBACK_STABILITY_OK .*frames=[1-9][0-9]* .*seeks=[1-9][0-9]* .*resizeCycles=1 .*contextRecreations=[1-9][0-9]* presentationResumes=1' <<<"$output"
  case "$expected_presentation_backend" in
    composition-shared)
      grep -q 'renderer=composition-shared' <<<"$output"
      grep -q 'presentationBackend=composition-shared sync=automatic-gl-flush cpuReadback=false copiesPerFrame=0 compositorCopiesPerFrame=1' <<<"$output"
      grep -q 'gpuCopies=0 blockingFinishes=0' <<<"$output"
      ;;
    shared-worker-finish)
      grep -q 'renderer=shared-worker' <<<"$output"
      grep -q 'presentationBackend=shared-worker-finish sync=gl-finish cpuReadback=false copiesPerFrame=1 compositorCopiesPerFrame=1' <<<"$output"
      ;;
    shared-worker-fence)
      grep -q 'renderer=shared-worker' <<<"$output"
      grep -q 'presentationBackend=shared-worker-fence sync=gpu-fence+consumer-finish cpuReadback=false copiesPerFrame=1 compositorCopiesPerFrame=1' <<<"$output"
      ;;
    inline)
      grep -q 'renderer=inline' <<<"$output"
      grep -q 'presentationBackend=inline sync=gl-finish cpuReadback=false copiesPerFrame=0 compositorCopiesPerFrame=1' <<<"$output"
      ;;
    *)
      echo "unsupported expected presentation backend: $expected_presentation_backend" >&2
      return 2
      ;;
  esac
  printf 'CANX_PLAYBACK_STABILITY_BACKEND_OK backend=%s\n' "$backend"
}

if [[ -n "${WAYLAND_DISPLAY:-}" ]]; then
  run_stability Wayland env
fi

if [[ -n "${DISPLAY:-}" ]]; then
  run_stability X11 env CANX_FORCE_X11=1
fi

if [[ -z "${WAYLAND_DISPLAY:-}" && -z "${DISPLAY:-}" ]]; then
  command -v xvfb-run >/dev/null 2>&1 || {
    echo "neither a graphical session nor xvfb-run is available" >&2
    exit 2
  }
  run_stability X11 xvfb-run -a -s '-screen 0 1320x820x24' env CANX_FORCE_X11=1
fi

echo "CANX_PLAYBACK_STABILITY_ALL_OK platform=linux-x64 seconds=$duration_seconds"
