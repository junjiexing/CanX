#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"
tool_root="${CANX_PROTOCOL_TEST_ROOT:-$repository_root/.cache/download-protocol-tests}"

if [[ "${1:-}" != "--skip-publish" ]]; then
  CANX_INCLUDE_SMOKE_TESTS=true CANX_APP_OUTPUT="$output_dir" \
    "$repository_root/scripts/publish-static-app-linux-x64.sh" "${@}"
fi

if [[ ! -x "$output_dir/CanX.App" ]]; then
  printf 'CanX.App was not found at %s\n' "$output_dir" >&2
  exit 2
fi

if [[ ! -x "$tool_root/bin/python" ]]; then
  python3 -m venv "$tool_root"
fi
if ! "$tool_root/bin/python" -c 'import paramiko, pyftpdlib' 2>/dev/null; then
  "$tool_root/bin/pip" install --disable-pip-version-check \
    -r "$repository_root/scripts/support/download-test-requirements.txt"
fi

work_dir="$(mktemp -d /tmp/canx-ftp-sftp.XXXXXX)"
ftp_pid=""
sftp_pid=""
retry_ftp_pid=""
cleanup() {
  for process_id in "$retry_ftp_pid" "$sftp_pid" "$ftp_pid"; do
    if [[ -n "$process_id" ]]; then
      kill "$process_id" 2>/dev/null || true
      wait "$process_id" 2>/dev/null || true
    fi
  done
  if [[ "${CANX_KEEP_TEST_WORK:-0}" == "1" ]]; then
    printf 'CANX_FTP_SFTP_WORK=%s\n' "$work_dir"
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

ftp_port="$(allocate_port)"
sftp_port="$(allocate_port)"
retry_ftp_port="$(allocate_port)"
username="canx-fixture"
password="canx-stage2-secret"
mkdir -p "$work_dir/source" "$work_dir/download-ftp" "$work_dir/download-sftp"
dd if=/dev/urandom of="$work_dir/source/ftp-payload.bin" bs=1M count=1 status=none
dd if=/dev/urandom of="$work_dir/source/sftp-payload.bin" bs=1M count=1 status=none

"$tool_root/bin/python" "$repository_root/scripts/support/download_protocol_server.py" ftp \
  --root "$work_dir/source" --port "$ftp_port" --username "$username" --password "$password" \
  --ready-file "$work_dir/ftp.ready" >"$work_dir/ftp.log" 2>&1 &
ftp_pid="$!"
"$tool_root/bin/python" "$repository_root/scripts/support/download_protocol_server.py" sftp \
  --root "$work_dir/source" --port "$sftp_port" --username "$username" --password "$password" \
  --ready-file "$work_dir/sftp.ready" >"$work_dir/sftp.log" 2>&1 &
sftp_pid="$!"

for _ in {1..100}; do
  if [[ -s "$work_dir/ftp.ready" && -s "$work_dir/sftp.ready" ]]; then
    break
  fi
  if ! kill -0 "$ftp_pid" 2>/dev/null || ! kill -0 "$sftp_pid" 2>/dev/null; then
    sed -n '1,240p' "$work_dir/ftp.log" >&2
    sed -n '1,240p' "$work_dir/sftp.log" >&2
    exit 1
  fi
  sleep 0.1
done
[[ -s "$work_dir/ftp.ready" && -s "$work_dir/sftp.ready" ]]

"$tool_root/bin/python" "$repository_root/scripts/support/download_protocol_server.py" ftp \
  --root "$work_dir/source" --port "$retry_ftp_port" --username "$username" --password "$password" \
  --reject-auth-count 1 --ready-file "$work_dir/retry-ftp.ready" >"$work_dir/retry-ftp.log" 2>&1 &
retry_ftp_pid="$!"
for _ in {1..50}; do
  [[ -s "$work_dir/retry-ftp.ready" ]] && break
  sleep 0.1
done
[[ -s "$work_dir/retry-ftp.ready" ]]

ftp_log="$work_dir/ftp-download.log"
CANX_DATA_DIRECTORY="$work_dir/data-ftp" timeout 90s "$output_dir/CanX.App" \
  --download-fixture-smoke uri \
  "ftp://$username:$password@127.0.0.1:$ftp_port/ftp-payload.bin" \
  "$work_dir/download-ftp" \
  --option=max-download-limit=128K \
  --expect-files=1 \
  --expect-server \
  --timeout-seconds=80 | tee "$ftp_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri tasks=1 files=1 .*servers=[1-9]' "$ftp_log"
cmp "$work_dir/source/ftp-payload.bin" "$work_dir/download-ftp/ftp-payload.bin"

host_fingerprint="$(tr -d '\r\n' < "$work_dir/sftp.ready")"
sftp_log="$work_dir/sftp-download.log"
CANX_DATA_DIRECTORY="$work_dir/data-sftp" timeout 90s "$output_dir/CanX.App" \
  --download-fixture-smoke uri \
  "sftp://$username:$password@127.0.0.1:$sftp_port/sftp-payload.bin" \
  "$work_dir/download-sftp" \
  --option="ssh-host-key-md=$host_fingerprint" \
  --option=max-download-limit=128K \
  --expect-files=1 \
  --expect-server \
  --timeout-seconds=80 | tee "$sftp_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri tasks=1 files=1 .*servers=[1-9]' "$sftp_log"
cmp "$work_dir/source/sftp-payload.bin" "$work_dir/download-sftp/sftp-payload.bin"

authentication_log="$work_dir/ftp-authentication-failure.log"
CANX_DATA_DIRECTORY="$work_dir/data-authentication-failure" timeout 30s "$output_dir/CanX.App" \
  --download-fixture-smoke uri \
  "ftp://$username:wrong-password@127.0.0.1:$ftp_port/ftp-payload.bin" \
  "$work_dir/download-authentication-failure" \
  --expect-files=1 \
  --expect-error=21 \
  --timeout-seconds=20 | tee "$authentication_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri tasks=1 files=1 .*error=21' "$authentication_log"

retry_log="$work_dir/ftp-retry.log"
CANX_DATA_DIRECTORY="$work_dir/data-retry" timeout 45s "$output_dir/CanX.App" \
  --download-fixture-smoke uri \
  "ftp://$username:$password@127.0.0.1:$retry_ftp_port/ftp-payload.bin" \
  "$work_dir/download-retry" \
  --option=max-tries=1 \
  --option=max-download-limit=128K \
  --expect-files=1 \
  --expect-server \
  --retry-on-error \
  --timeout-seconds=35 | tee "$retry_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri tasks=1 files=1 .*retried=true error=0' "$retry_log"
cmp "$work_dir/source/ftp-payload.bin" "$work_dir/download-retry/ftp-payload.bin"

if rg -q "$password" "$ftp_log" "$sftp_log" "$authentication_log" "$retry_log"; then
  printf 'Protocol credential leaked into application output.\n' >&2
  exit 1
fi
if rg -q "$password|wrong-password|$username" "$work_dir"/data-*/downloads.json; then
  printf 'Protocol credential leaked into downloads.json.\n' >&2
  exit 1
fi

printf 'CANX_FTP_SFTP_OK ftp=true sftp=true authentication=true\n'
