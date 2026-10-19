#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_output="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"
work_dir="$(mktemp -d /tmp/canx-playback.XXXXXX)"
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

for tool in ffmpeg timeout; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing playback verification tool: $tool" >&2
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

media_file="$work_dir/playback-fixture.mkv"
second_media_file="$work_dir/playback-fixture-second.mkv"
subtitle_file="$work_dir/external-subtitle.zh.srt"
chapter_file="$work_dir/chapters.ffmeta"
shader_file="$work_dir/smoke-shader.hook"
printf '1\n00:00:00,200 --> 00:00:07,500\nCanX external subtitle smoke test\n' >"$subtitle_file"
printf ';FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=4000\ntitle=Opening\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=4000\nEND=8000\ntitle=Main\n' >"$chapter_file"
printf '//!HOOK MAIN\n//!BIND HOOKED\nvec4 hook() { return HOOKED_texOff(0); }\n' >"$shader_file"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -f lavfi -i 'sine=frequency=440:sample_rate=48000' \
  -f lavfi -i 'sine=frequency=880:sample_rate=48000' \
  -f ffmetadata -i "$chapter_file" \
  -map 0:v -map 1:a -map 2:a -map_metadata 3 \
  -metadata:s:a:0 language=eng -metadata:s:a:0 title='English' \
  -metadata:s:a:1 language=jpn -metadata:s:a:1 title='Japanese' \
  -t 8 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
  -c:a aac -b:a 64k "$media_file"
cp -- "$media_file" "$second_media_file"

run_smoke() {
  local backend="$1"
  shift
  local output
  if ! output="$(CANX_DATA_DIRECTORY="$work_dir/data-$backend" "$@" timeout 60s \
    "$app_output/CanX.App" --playback-smoke "$media_file" "$subtitle_file" "$second_media_file" "$shader_file" 2>&1)"; then
    printf '%s\n' "$output" >&2
    return 1
  fi
  printf '%s\n' "$output"
  grep -Eq 'CANX_APP_PLAYBACK_OK state=Playing .*frames=(1[2-9]|[2-9][0-9]|[1-9][0-9]{2,})' <<<"$output"
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
  grep -q 'tracks=4 audioTracks=2 externalSubtitle=true' <<<"$output"
  grep -Eq 'playlist=2 playlistNavigation=true recent=[1-9][0-9]*' <<<"$output"
  grep -q 'chapters=2 chapterNavigation=true bookmark=true' <<<"$output"
  grep -q 'frameStep=true abLoop=true screenshot=true audioDevices=true filters=true shader=true' <<<"$output"
  grep -q 'diagnostics=true color=true runtimeProperty=true runtimeCommand=true' <<<"$output"
  grep -Eq 'contextRecovery=true contextGeneration=[2-9][0-9]*' <<<"$output"
  grep -Eq 'capabilities=true propertyCapabilities=[1-9][0-9]{2,} .*commandCapabilities=[5-9][0-9]+ .*capabilityUncovered=0' <<<"$output"
  printf 'CANX_PLAYBACK_BACKEND_OK backend=%s\n' "$backend"
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

echo 'CANX_PLAYBACK_OK platform=linux-x64'
