#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
aria_build_dir="$($repository_root/scripts/prepare-aria2-spike.sh | tail -n 1)"
aria_dependency_dir="$repository_root/.cache/native/aria2/root/usr/lib/x86_64-linux-gnu"
mpv_library_dir="$($repository_root/scripts/prepare-mpv-spike.sh | tail -n 1)"
stream_output="$repository_root/artifacts/spikes/streaming"
mpv_output="$repository_root/artifacts/spikes/mpv-avalonia"
mkdir -p "$stream_output" "$mpv_output"
work_dir="$(mktemp -d "$stream_output.XXXXXX")"
source_pid=""
gateway_pid=""

cleanup() {
  stop_gateway
  stop_source
  rm -rf -- "$work_dir"
}

stop_gateway() {
  if [[ -n "$gateway_pid" ]]; then
    kill "$gateway_pid" 2>/dev/null || true
    wait "$gateway_pid" 2>/dev/null || true
    gateway_pid=""
  fi
}

stop_source() {
  if [[ -n "$source_pid" ]]; then
    kill "$source_pid" 2>/dev/null || true
    wait "$source_pid" 2>/dev/null || true
    source_pid=""
  fi
}

allocate_port() {
  printf '%s\n' "$((20000 + RANDOM % 18000))"
}

wait_for_http() {
  local url="$1"
  for _ in {1..200}; do
    if curl -fsI "$url" >/dev/null; then
      return 0
    fi
    sleep 0.05
  done
  printf 'Timed out waiting for %s\n' "$url" >&2
  return 1
}

wait_for_log() {
  local log_file="$1"
  local marker="$2"
  local process_id="$3"
  local attempts="${4:-600}"
  for ((attempt = 0; attempt < attempts; attempt++)); do
    if grep -q "$marker" "$log_file"; then
      return 0
    fi
    if ! kill -0 "$process_id" 2>/dev/null; then
      cat "$log_file"
      return 1
    fi
    sleep 0.1
  done
  printf 'Timed out waiting for %s in %s\n' "$marker" "$log_file" >&2
  cat "$log_file"
  return 1
}

start_source() {
  local port="$1"
  local log_file="$2"
  shift 2
  python3 "$repository_root/scripts/support/range_http_server.py" \
    --root "$work_dir/source" --port "$port" --chunk-size 16384 --delay-ms 12 "$@" \
    >"$log_file" 2>&1 &
  source_pid="$!"
}

start_gateway() {
  local source_url="$1"
  local destination="$2"
  local port="$3"
  local log_file="$4"
  LD_LIBRARY_PATH="$aria_build_dir:$aria_dependency_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    "$stream_output/CanX.Spikes.Streaming" "$source_url" "$destination" "$port" \
    >"$log_file" 2>&1 &
  gateway_pid="$!"
  wait_for_log "$log_file" CANX_GATEWAY_READY "$gateway_pid" 200
}

run_mpv_smoke() {
  local port="$1"
  local output
  output="$(LD_LIBRARY_PATH="$mpv_library_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    timeout 45s "$mpv_output/CanX.Spikes.MpvAvalonia" \
    --smoke "http://127.0.0.1:$port/media")"
  printf '%s\n' "$output"
  grep -q CANX_MPV_RENDER_OK <<<"$output"
}

finish_gateway() {
  local port="$1"
  local log_file="$2"
  wait_for_log "$log_file" CANX_STREAM_DOWNLOAD_COMPLETE "$gateway_pid"
  curl -fsS "http://127.0.0.1:$port/shutdown" >/dev/null
  wait "$gateway_pid"
  gateway_pid=""
  grep -q CANX_STREAM_GATEWAY_OK "$log_file"
}

trap cleanup EXIT

mkdir -p "$work_dir/source" "$work_dir/download"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -t 20 -c:v mpeg4 -q:v 2 "$work_dir/source/tail-index.mp4"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -t 20 -c:v mpeg4 -q:v 2 -movflags +faststart "$work_dir/source/fast-start.mp4"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -t 20 -c:v mpeg4 -q:v 2 "$work_dir/source/media.mkv"

dotnet publish "$repository_root/spikes/CanX.Spikes.Streaming/CanX.Spikes.Streaming.csproj" \
  --configuration Release --runtime linux-x64 --self-contained --output "$stream_output"
dotnet publish "$repository_root/spikes/CanX.Spikes.MpvAvalonia/CanX.Spikes.MpvAvalonia.csproj" \
  --configuration Release --runtime linux-x64 --self-contained --output "$mpv_output"

source_port="$(allocate_port)"
source_log="$work_dir/range-source.log"
start_source "$source_port" "$source_log"
wait_for_http "http://127.0.0.1:$source_port/tail-index.mp4"

# Tail-index MP4 plus random middle/tail reads while aria2 is still downloading.
gateway_port="$(allocate_port)"
gateway_log="$work_dir/tail-index.log"
destination="$work_dir/download/tail-index.mp4"
start_gateway "http://127.0.0.1:$source_port/tail-index.mp4" "$destination" "$gateway_port" "$gateway_log"
source_size="$(stat -c %s "$work_dir/source/tail-index.mp4")"
range_start="$((source_size / 2 + 7919))"
range_end="$((range_start + 131071))"
curl -fsS --range "$range_start-$range_end" "http://127.0.0.1:$gateway_port/media" \
  --output "$work_dir/random-middle.bin"
cmp -n "$((range_end - range_start + 1))" -i "$range_start:0" \
  "$work_dir/source/tail-index.mp4" "$work_dir/random-middle.bin"
curl -fsS --range -65536 "http://127.0.0.1:$gateway_port/media" \
  --output "$work_dir/random-tail.bin"
cmp -n 65536 -i "$((source_size - 65536)):0" \
  "$work_dir/source/tail-index.mp4" "$work_dir/random-tail.bin"
run_mpv_smoke "$gateway_port"
finish_gateway "$gateway_port" "$gateway_log"
grep -q CANX_GATEWAY_PROGRESSIVE_CONFIRMED "$gateway_log"
grep -q CANX_GATEWAY_ORIGIN_RANGE "$gateway_log"
cmp "$work_dir/source/tail-index.mp4" "$destination"
printf 'CANX_STREAM_CASE_OK case=tail-index-random-seek\n'

# Fast-start MP4 must render before its throttled persistent download completes.
gateway_port="$(allocate_port)"
gateway_log="$work_dir/fast-start.log"
destination="$work_dir/download/fast-start.mp4"
start_gateway "http://127.0.0.1:$source_port/fast-start.mp4" "$destination" "$gateway_port" "$gateway_log"
run_mpv_smoke "$gateway_port"
finish_gateway "$gateway_port" "$gateway_log"
grep -q CANX_GATEWAY_PROGRESSIVE_CONFIRMED "$gateway_log"
cmp "$work_dir/source/fast-start.mp4" "$destination"
printf 'CANX_STREAM_CASE_OK case=fast-start-mp4\n'

# Matroska follows the same Gateway contract and is identified with its own MIME type.
gateway_port="$(allocate_port)"
gateway_log="$work_dir/mkv.log"
destination="$work_dir/download/media.mkv"
start_gateway "http://127.0.0.1:$source_port/media.mkv" "$destination" "$gateway_port" "$gateway_log"
test "$(curl -fsSI "http://127.0.0.1:$gateway_port/media" | tr -d '\r' | awk -F': ' 'tolower($1) == "content-type" { print $2 }')" = "video/x-matroska"
run_mpv_smoke "$gateway_port"
finish_gateway "$gateway_port" "$gateway_log"
grep -q CANX_GATEWAY_PROGRESSIVE_CONFIRMED "$gateway_log"
cmp "$work_dir/source/media.mkv" "$destination"
printf 'CANX_STREAM_CASE_OK case=mkv\n'

stop_source

# A non-Range origin must yield an explicit 503 for unavailable random access.
source_port="$(allocate_port)"
source_log="$work_dir/no-range-source.log"
start_source "$source_port" "$source_log" --disable-ranges
wait_for_http "http://127.0.0.1:$source_port/tail-index.mp4"
gateway_port="$(allocate_port)"
gateway_log="$work_dir/no-range.log"
destination="$work_dir/download/no-range.mp4"
start_gateway "http://127.0.0.1:$source_port/tail-index.mp4" "$destination" "$gateway_port" "$gateway_log"
range_status="$(curl -sS --range -65536 "http://127.0.0.1:$gateway_port/media" \
  --output "$work_dir/no-range-response.txt" --write-out '%{http_code}')"
test "$range_status" = "503"
finish_gateway "$gateway_port" "$gateway_log"
grep -q 'CANX_GATEWAY_RANGE_UNAVAILABLE status=200' "$gateway_log"
cmp "$work_dir/source/tail-index.mp4" "$destination"
printf 'CANX_STREAM_CASE_OK case=no-range\n'

stop_source

# aria2 must resume a truncated response and still persist an identical file.
source_port="$(allocate_port)"
source_log="$work_dir/disconnect-source.log"
start_source "$source_port" "$source_log" --disconnect-once-after-bytes 524288
wait_for_http "http://127.0.0.1:$source_port/fast-start.mp4"
gateway_port="$(allocate_port)"
gateway_log="$work_dir/disconnect.log"
destination="$work_dir/download/disconnect.mp4"
start_gateway "http://127.0.0.1:$source_port/fast-start.mp4" "$destination" "$gateway_port" "$gateway_log"
finish_gateway "$gateway_port" "$gateway_log"
grep -q CANX_SOURCE_DISCONNECTED "$source_log"
cmp "$work_dir/source/fast-start.mp4" "$destination"
printf 'CANX_STREAM_CASE_OK case=disconnect-resume\n'

printf 'CANX_STREAM_GATEWAY_MATRIX_OK\n'
