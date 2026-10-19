#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_output="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"
tool_root="${CANX_PROTOCOL_TEST_ROOT:-$repository_root/.cache/download-protocol-tests}"
work_dir="$(mktemp -d /tmp/canx-ftp-media.XXXXXX)"
skip_publish=false
ftp_pid=""

if [[ "${1:-}" == "--skip-publish" ]]; then
  skip_publish=true
elif [[ $# -ne 0 ]]; then
  echo "usage: $0 [--skip-publish]" >&2
  exit 2
fi

cleanup() {
  if [[ -n "$ftp_pid" ]]; then
    kill "$ftp_pid" 2>/dev/null || true
    wait "$ftp_pid" 2>/dev/null || true
  fi
  if [[ "${CANX_KEEP_TEST_WORK:-0}" == "1" ]]; then
    printf 'CANX_FTP_MEDIA_WORK=%s\n' "$work_dir"
  else
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

for tool in ffmpeg python3 timeout; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing FTP media verification tool: $tool" >&2
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

if [[ ! -x "$tool_root/bin/python" ]]; then
  python3 -m venv "$tool_root"
fi
if ! "$tool_root/bin/python" -c 'import pyftpdlib' 2>/dev/null; then
  "$tool_root/bin/pip" install --disable-pip-version-check \
    -r "$repository_root/scripts/support/download-test-requirements.txt"
fi

mkdir -p "$work_dir/source"
media_name="ftp-playback-fixture.mkv"
subtitle_name="ftp-playback-fixture.zh-Hans.srt"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -f lavfi -i 'sine=frequency=440:sample_rate=48000' \
  -t 8 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
  -c:a aac -b:a 64k "$work_dir/source/$media_name"
printf '%s\n' \
  '1' \
  '00:00:00,000 --> 00:00:07,500' \
  'CanX FTP subtitle stream' \
  >"$work_dir/source/$subtitle_name"

ftp_port="$(python3 - <<'PY'
import socket
with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    print(sock.getsockname()[1])
PY
)"
username="canx-media-fixture"
password="canx-stage4-ftp-secret"
"$tool_root/bin/python" "$repository_root/scripts/support/download_protocol_server.py" ftp \
  --root "$work_dir/source" --port "$ftp_port" --username "$username" --password "$password" \
  --ready-file "$work_dir/ftp.ready" --hide-rest-feature >"$work_dir/ftp.log" 2>&1 &
ftp_pid="$!"

for _ in {1..100}; do
  [[ -s "$work_dir/ftp.ready" ]] && break
  if ! kill -0 "$ftp_pid" 2>/dev/null; then
    sed -n '1,240p' "$work_dir/ftp.log" >&2
    exit 1
  fi
  sleep 0.1
done
[[ -s "$work_dir/ftp.ready" ]]

run_smoke() {
  local backend="$1"
  shift
  local output
  if ! output="$(CANX_DATA_DIRECTORY="$work_dir/data-$backend" "$@" timeout 75s \
    "$app_output/CanX.App" --ftp-playback-smoke \
    "ftp://127.0.0.1:$ftp_port/" "$username" "$password" \
    "$media_name" "$subtitle_name" 2>&1)"; then
    printf '%s\n' "$output" >&2
    return 1
  fi
  printf '%s\n' "$output"
  grep -Eq 'CANX_FTP_PLAYBACK_OK files=2 subtitles=1 frames=[1-9][0-9]* reads=[1-9][0-9]* seeks=[1-9][0-9]*' <<<"$output"
  if grep -q "$password" <<<"$output"; then
    echo 'FTP password leaked into application output.' >&2
    return 1
  fi
  printf 'CANX_FTP_MEDIA_BACKEND_OK backend=%s\n' "$backend"
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

echo 'CANX_FTP_MEDIA_PLAYBACK_OK platform=linux-x64'
