#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"

CANX_INCLUDE_SMOKE_TESTS=true CANX_APP_OUTPUT="$output_dir" \
  "$repository_root/scripts/publish-static-app-linux-x64.sh" "$@"

smoke_output="$(timeout 20s "$output_dir/CanX.App" --smoke)"
printf '%s\n' "$smoke_output"
grep -q 'CANX_APP_SHELL_OK' <<<"$smoke_output"

work_dir="$(mktemp -d /tmp/canx-static-app.XXXXXX)"
server_pid=""
cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

mkdir -p "$work_dir/source" "$work_dir/download" "$work_dir/data"
dd if=/dev/urandom of="$work_dir/source/payload.bin" bs=1M count=2 status=none
port="$((20000 + RANDOM % 20000))"
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$work_dir/source" \
  >"$work_dir/http.log" 2>&1 &
server_pid="$!"
for _ in {1..50}; do
  if curl -fs --output /dev/null "http://127.0.0.1:$port/payload.bin"; then
    break
  fi
  sleep 0.1
done

download_output="$(CANX_DATA_DIRECTORY="$work_dir/data" timeout 60s "$output_dir/CanX.App" \
  --download-smoke "http://127.0.0.1:$port/payload.bin" "$work_dir/download" \
  "http://localhost:$port/payload.bin")"
printf '%s\n' "$download_output"
grep -q 'CANX_APP_DOWNLOAD_OK' <<<"$download_output"
grep -Eq 'CANX_APP_CAPABILITIES abi=6 extended=true features=[1-9][0-9]* options=[1-9][0-9]* uncovered=0' <<<"$download_output"
grep -q 'CANX_APP_OPTION_SCOPE total=200 initial=115 active=7 reserved=108 global=120 hidden=0 readOnly=72 uncovered=0' <<<"$download_output"
cmp "$work_dir/source/payload.bin" "$work_dir/download/payload.bin"

printf 'CANX_STATIC_APP_OK platform=linux-x64 output=%s\n' "$output_dir"
