#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"

if [[ "${1:-}" != "--skip-publish" ]]; then
  CANX_INCLUDE_SMOKE_TESTS=true CANX_APP_OUTPUT="$output_dir" \
    "$repository_root/scripts/publish-static-app-linux-x64.sh" "${@}"
fi

if [[ ! -x "$output_dir/CanX.App" ]]; then
  printf 'CanX.App was not found at %s\n' "$output_dir" >&2
  exit 2
fi

work_dir="$(mktemp -d /tmp/canx-download-failures.XXXXXX)"
server_pid=""
cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  if [[ "${CANX_KEEP_TEST_WORK:-0}" == "1" ]]; then
    printf 'CANX_DOWNLOAD_FAILURE_WORK=%s\n' "$work_dir"
  else
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

allocate_port() {
  python3 - <<'PY'
import socket
with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    print(sock.getsockname()[1])
PY
}

start_server() {
  local ready_file="$1"
  shift
  local port
  port="$(allocate_port)"
  python3 "$repository_root/scripts/support/range_http_server.py" \
    --root "$work_dir/source" --port "$port" --ready-file "$ready_file" "$@" \
    >"$ready_file.log" 2>&1 &
  server_pid="$!"
  for _ in {1..50}; do
    [[ -s "$ready_file" ]] && break
    sleep 0.1
  done
  [[ -s "$ready_file" ]]
  printf '%s' "$port"
}

stop_server() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
    server_pid=""
  fi
}

mkdir -p "$work_dir/source" "$work_dir/download-recovery" "$work_dir/download-retries"
dd if=/dev/urandom of="$work_dir/source/payload.bin" bs=1M count=3 status=none

recovery_ready="$work_dir/recovery.ready"
recovery_port="$(start_server "$recovery_ready" --delay-ms 2 --disconnect-once-after-bytes 262144)"
recovery_log="$work_dir/recovery.log"
CANX_DATA_DIRECTORY="$work_dir/data-recovery" timeout 60s "$output_dir/CanX.App" \
  --download-fixture-smoke uri \
  "http://127.0.0.1:$recovery_port/payload.bin" \
  "$work_dir/download-recovery" \
  --option=max-tries=3 \
  --option=retry-wait=0 \
  --expect-files=1 \
  --expect-server \
  --timeout-seconds=50 | tee "$recovery_log"
grep -q 'CANX_SOURCE_DISCONNECTED' "$recovery_ready.log"
grep -q 'CANX_APP_FIXTURE_OK kind=uri' "$recovery_log"
cmp "$work_dir/source/payload.bin" "$work_dir/download-recovery/payload.bin"
stop_server

retry_ready="$work_dir/retry.ready"
retry_port="$(start_server "$retry_ready" --disconnect-once-after-bytes 16384 --disconnect-first-requests 100 --delay-ms 0)"
retry_log="$work_dir/retry-exhausted.log"
CANX_DATA_DIRECTORY="$work_dir/data-retries" timeout 30s "$output_dir/CanX.App" \
  --download-fixture-smoke uri \
  "http://127.0.0.1:$retry_port/payload.bin" \
  "$work_dir/download-retries" \
  --option=max-tries=3 \
  --option=retry-wait=0 \
  --expect-files=1 \
  --expect-any-error \
  --timeout-seconds=20 | tee "$retry_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri .*error=[1-9][0-9]*' "$retry_log"
retry_attempts="$(rg -c 'CANX_SOURCE_DISCONNECTED' "$retry_ready.log")"
if (( retry_attempts < 3 )); then
  printf 'Expected at least 3 rejected requests, got %s.\n' "$retry_attempts" >&2
  exit 1
fi
stop_server

disk_ready="$work_dir/disk.ready"
disk_port="$(start_server "$disk_ready" --delay-ms 0)"
disk_log="$work_dir/disk-full.log"
CANX_DATA_DIRECTORY="$work_dir/data-disk" timeout 30s "$output_dir/CanX.App" \
  --download-fixture-smoke uri \
  "http://127.0.0.1:$disk_port/payload.bin" \
  /dev \
  --output-name=full \
  --option=allow-overwrite=true \
  --option=auto-file-renaming=false \
  --option=file-allocation=none \
  --expect-files=1 \
  --expect-any-error \
  --timeout-seconds=20 | tee "$disk_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri tasks=1 files=1 .*error=[1-9][0-9]*' "$disk_log"

printf 'CANX_DOWNLOAD_FAILURES_OK recovery=true retries=%s diskFull=true\n' "$retry_attempts"
