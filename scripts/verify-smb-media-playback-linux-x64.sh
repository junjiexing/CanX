#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_output="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"
work_dir="$(mktemp -d /tmp/canx-smb-media.XXXXXX)"
skip_publish=false
smb_pid=""

if [[ "${1:-}" == "--skip-publish" ]]; then
  skip_publish=true
elif [[ $# -ne 0 ]]; then
  echo "usage: $0 [--skip-publish]" >&2
  exit 2
fi

cleanup() {
  if [[ -n "$smb_pid" ]]; then
    kill -TERM "$smb_pid" 2>/dev/null || true
    for _ in {1..20}; do
      kill -0 "$smb_pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$smb_pid" 2>/dev/null || true
    wait "$smb_pid" 2>/dev/null || true
  fi
  if [[ "${CANX_KEEP_TEST_WORK:-0}" == "1" ]]; then
    printf 'CANX_SMB_MEDIA_WORK=%s\n' "$work_dir"
  else
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

for tool in dotnet ffmpeg python3 timeout; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing SMB media verification tool: $tool" >&2
    exit 2
  }
done

if [[ "$skip_publish" == false ]]; then
  CANX_INCLUDE_SMOKE_TESTS=true CANX_APP_OUTPUT="$app_output" \
    CANX_ALLOW_NEWER_GLIBC="${CANX_ALLOW_NEWER_GLIBC:-0}" \
    "$repository_root/scripts/publish-static-app-linux-x64.sh"
fi

[[ -x "$app_output/CanX.App" ]] || {
  echo "published CanX executable not found: $app_output/CanX.App" >&2
  exit 1
}

dotnet build "$repository_root/tests/CanX.SmbFixture/CanX.SmbFixture.csproj" \
  --configuration Release --nologo

mkdir -p "$work_dir/source/series"
media_name="smb-playback-fixture.mkv"
subtitle_name="smb-playback-fixture.zh-Hans.srt"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -f lavfi -i 'sine=frequency=660:sample_rate=48000' \
  -t 8 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
  -c:a aac -b:a 64k "$work_dir/source/series/$media_name"
printf '%s\n' \
  '1' \
  '00:00:00,000 --> 00:00:07,500' \
  'CanX SMB subtitle stream' \
  >"$work_dir/source/series/$subtitle_name"

smb_port="$(python3 - <<'PY'
import socket
with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    print(sock.getsockname()[1])
PY
)"
username="canx-media-fixture"
password="canx-stage4-smb-secret"
dotnet "$repository_root/tests/CanX.SmbFixture/bin/Release/net10.0/CanX.SmbFixture.dll" \
  "$work_dir/source" "$smb_port" "$username" "$password" "$work_dir/smb.ready" \
  >"$work_dir/smb.log" 2>&1 &
smb_pid="$!"

for _ in {1..100}; do
  [[ -s "$work_dir/smb.ready" ]] && break
  if ! kill -0 "$smb_pid" 2>/dev/null; then
    sed -n '1,240p' "$work_dir/smb.log" >&2
    exit 1
  fi
  sleep 0.1
done
[[ -s "$work_dir/smb.ready" ]]

run_smoke() {
  local backend="$1"
  shift
  local output
  if ! output="$(CANX_DATA_DIRECTORY="$work_dir/data-$backend" "$@" timeout 75s \
    "$app_output/CanX.App" --smb-playback-smoke \
    "smb://127.0.0.1:$smb_port/media/series/" "$username" "$password" \
    "$media_name" "$subtitle_name" 2>&1)"; then
    printf '%s\n' "$output" >&2
    return 1
  fi
  printf '%s\n' "$output"
  grep -Eq 'CANX_SMB_PLAYBACK_OK files=2 subtitles=1 frames=[1-9][0-9]* reads=[1-9][0-9]* seeks=[1-9][0-9]*' <<<"$output"
  if grep -q "$password" <<<"$output"; then
    echo 'SMB password leaked into application output.' >&2
    return 1
  fi
  printf 'CANX_SMB_MEDIA_BACKEND_OK backend=%s\n' "$backend"
}

if [[ -n "${WAYLAND_DISPLAY:-}" ]]; then
  run_smoke Wayland env
elif [[ -n "${DISPLAY:-}" ]]; then
  run_smoke X11 env CANX_FORCE_X11=1
else
  command -v xvfb-run >/dev/null 2>&1 || {
    echo "neither a graphical session nor xvfb-run is available" >&2
    exit 2
  }
  run_smoke X11 xvfb-run -a -s '-screen 0 1320x820x24' env CANX_FORCE_X11=1
fi

echo 'CANX_SMB_MEDIA_PLAYBACK_OK platform=linux-x64'
