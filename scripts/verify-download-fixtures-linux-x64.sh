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

work_dir="$(mktemp -d /tmp/canx-download-fixtures.XXXXXX)"
http_pid=""
tracker_pid=""
seeder_pid=""
cleanup() {
  for process_id in "$seeder_pid" "$tracker_pid" "$http_pid"; do
    if [[ -n "$process_id" ]]; then
      kill "$process_id" 2>/dev/null || true
      wait "$process_id" 2>/dev/null || true
    fi
  done
  if [[ "${CANX_KEEP_TEST_WORK:-0}" == "1" ]]; then
    printf 'CANX_DOWNLOAD_FIXTURE_WORK=%s\n' "$work_dir"
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

http_port="$(allocate_port)"
tracker_port="$(allocate_port)"
seeder_port="$(allocate_port)"
downloader_port="$(allocate_port)"

mkdir -p "$work_dir/source" "$work_dir/metadata" "$work_dir/download-selected" \
  "$work_dir/download-seeder" "$work_dir/download-magnet" \
  "$work_dir/download-magnet-direct" "$work_dir/download-http-torrent" \
  "$work_dir/download-metalink" "$work_dir/temp"

python3 "$repository_root/scripts/support/create_download_fixtures.py" \
  --source-dir "$work_dir/source" \
  --output-dir "$work_dir/metadata" \
  --base-url "http://127.0.0.1:$http_port" \
  --tracker-url "http://127.0.0.1:$tracker_port/announce"
cp "$work_dir/metadata/canx-fixture.torrent" "$work_dir/source/canx-fixture.torrent"

python3 -m http.server "$http_port" --bind 127.0.0.1 --directory "$work_dir/source" \
  >"$work_dir/http.log" 2>&1 &
http_pid="$!"
python3 "$repository_root/scripts/support/bittorrent_tracker.py" \
  --port "$tracker_port" --peer-port "$seeder_port" >"$work_dir/tracker.log" 2>&1 &
tracker_pid="$!"

for _ in {1..50}; do
  if curl -fs --output /dev/null "http://127.0.0.1:$http_port/metalink/metalink-one.bin" && \
      curl -fs --output /dev/null "http://127.0.0.1:$tracker_port/announce"; then
    break
  fi
  sleep 0.1
done

aria2c \
  --dir="$work_dir/source" \
  --check-integrity=true \
  --file-allocation=none \
  --seed-time=60 \
  --seed-ratio=0.0 \
  --enable-dht=false \
  --enable-dht6=false \
  --bt-enable-lpd=false \
  --enable-peer-exchange=false \
  --listen-port="$seeder_port" \
  --dht-listen-port="$seeder_port" \
  --summary-interval=0 \
  "$work_dir/metadata/canx-fixture.torrent" >"$work_dir/seeder.log" 2>&1 &
seeder_pid="$!"

for _ in {1..150}; do
  if grep -q 'Verification finished successfully' "$work_dir/seeder.log"; then
    break
  fi
  if ! kill -0 "$seeder_pid" 2>/dev/null; then
    sed -n '1,240p' "$work_dir/seeder.log" >&2
    exit 1
  fi
  sleep 0.1
done
grep -q 'Verification finished successfully' "$work_dir/seeder.log"

common_torrent_options=(
  --option=enable-dht=false
  --option=enable-dht6=false
  --option=bt-enable-lpd=false
  --option=enable-peer-exchange=false
  --option="listen-port=$downloader_port"
  --expect-files=2
  --expect-tracker
  --expect-peer
  --timeout-seconds=90
)

selected_log="$work_dir/torrent-selected.log"
TMPDIR="$work_dir/temp" CANX_DATA_DIRECTORY="$work_dir/data-selected" timeout 100s "$output_dir/CanX.App" \
  --download-fixture-smoke torrent "$work_dir/metadata/canx-fixture.torrent" \
  "$work_dir/download-selected" \
  "${common_torrent_options[@]}" \
  --option=select-file=2 \
  --option=seed-time=0 \
  --option=max-download-limit=256K \
  --prepare \
  --expect-selected=2 | tee "$selected_log"
grep -q 'CANX_APP_FIXTURE_OK kind=torrent' "$selected_log"
cmp "$work_dir/source/canx-torrent-fixture/beta.bin" \
  "$work_dir/download-selected/canx-torrent-fixture/beta.bin"
if [[ -s "$work_dir/download-selected/canx-torrent-fixture/alpha.bin" ]]; then
  printf 'Unselected torrent file unexpectedly contains data.\n' >&2
  exit 1
fi

seeding_log="$work_dir/torrent-seeding.log"
TMPDIR="$work_dir/temp" CANX_DATA_DIRECTORY="$work_dir/data-seeding" timeout 100s "$output_dir/CanX.App" \
  --download-fixture-smoke torrent "$work_dir/metadata/canx-fixture.torrent" \
  "$work_dir/download-seeder" \
  "${common_torrent_options[@]}" \
  --option=seed-time=1 \
  --option=max-download-limit=1M \
  --prepare \
  --expect-selected=1,2 \
  --expect-seeder | tee "$seeding_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=torrent .*seeder=true' "$seeding_log"
cmp "$work_dir/source/canx-torrent-fixture/alpha.bin" \
  "$work_dir/download-seeder/canx-torrent-fixture/alpha.bin"
cmp "$work_dir/source/canx-torrent-fixture/beta.bin" \
  "$work_dir/download-seeder/canx-torrent-fixture/beta.bin"

info_hash="$(<"$work_dir/metadata/canx-fixture.infohash")"
magnet_uri="magnet:?xt=urn:btih:$info_hash&dn=canx-torrent-fixture&tr=http%3A%2F%2F127.0.0.1%3A${tracker_port}%2Fannounce"
magnet_log="$work_dir/magnet.log"
TMPDIR="$work_dir/temp" CANX_DATA_DIRECTORY="$work_dir/data-magnet" timeout 100s "$output_dir/CanX.App" \
  --download-fixture-smoke uri "$magnet_uri" \
  "$work_dir/download-magnet" \
  "${common_torrent_options[@]}" \
  --option=seed-time=0 \
  --option=max-download-limit=256K \
  --expect-name=canx-torrent-fixture \
  --expect-total-length=2097152 \
  --high-priority=1 \
  --low-priority=2 \
  --prepare \
  --expect-selected=1,2 | tee "$magnet_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri tasks=1 files=2' "$magnet_log"
cmp "$work_dir/source/canx-torrent-fixture/alpha.bin" \
  "$work_dir/download-magnet/canx-torrent-fixture/alpha.bin"
cmp "$work_dir/source/canx-torrent-fixture/beta.bin" \
  "$work_dir/download-magnet/canx-torrent-fixture/beta.bin"

magnet_direct_log="$work_dir/magnet-direct.log"
TMPDIR="$work_dir/temp" CANX_DATA_DIRECTORY="$work_dir/data-magnet-direct" timeout 100s "$output_dir/CanX.App" \
  --download-fixture-smoke uri "$magnet_uri" \
  "$work_dir/download-magnet-direct" \
  "${common_torrent_options[@]}" \
  --option=seed-time=0 \
  --option=max-download-limit=256K \
  --expect-name=canx-torrent-fixture \
  --expect-total-length=2097152 \
  --commit-before-metadata \
  --expect-selected=1,2 | tee "$magnet_direct_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri tasks=1 files=2' "$magnet_direct_log"
cmp "$work_dir/source/canx-torrent-fixture/alpha.bin" \
  "$work_dir/download-magnet-direct/canx-torrent-fixture/alpha.bin"
cmp "$work_dir/source/canx-torrent-fixture/beta.bin" \
  "$work_dir/download-magnet-direct/canx-torrent-fixture/beta.bin"

http_torrent_log="$work_dir/http-torrent.log"
TMPDIR="$work_dir/temp" CANX_DATA_DIRECTORY="$work_dir/data-http-torrent" timeout 100s "$output_dir/CanX.App" \
  --download-fixture-smoke uri "http://127.0.0.1:$http_port/canx-fixture.torrent" \
  "$work_dir/download-http-torrent" \
  "${common_torrent_options[@]}" \
  --option=seed-time=0 \
  --option=max-download-limit=256K \
  --expect-name=canx-torrent-fixture \
  --expect-total-length=2097152 \
  --prepare \
  --expect-selected=1,2 | tee "$http_torrent_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=uri tasks=1 files=2' "$http_torrent_log"
cmp "$work_dir/source/canx-torrent-fixture/alpha.bin" \
  "$work_dir/download-http-torrent/canx-torrent-fixture/alpha.bin"
cmp "$work_dir/source/canx-torrent-fixture/beta.bin" \
  "$work_dir/download-http-torrent/canx-torrent-fixture/beta.bin"

metalink_log="$work_dir/metalink.log"
TMPDIR="$work_dir/temp" CANX_DATA_DIRECTORY="$work_dir/data-metalink" timeout 100s "$output_dir/CanX.App" \
  --download-fixture-smoke metalink "$work_dir/metadata/canx-fixture.meta4" \
  "$work_dir/download-metalink" \
  --option=max-download-limit=128K \
  --expect-tasks=2 \
  --expect-files=1 \
  --expect-server \
  --timeout-seconds=90 | tee "$metalink_log"
grep -Eq 'CANX_APP_FIXTURE_OK kind=metalink tasks=2 files=2 .*servers=[1-9]' "$metalink_log"
cmp "$work_dir/source/metalink/metalink-one.bin" "$work_dir/download-metalink/metalink-one.bin"
cmp "$work_dir/source/metalink/metalink-two.bin" "$work_dir/download-metalink/metalink-two.bin"

printf 'CANX_DOWNLOAD_FIXTURES_OK torrent=5 metalink=1\n'
