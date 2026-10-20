#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
native_build_dir="$($repository_root/scripts/prepare-aria2-spike.sh | tail -n 1)"
native_dependency_dir="$repository_root/.cache/native/aria2/root/usr/lib/x86_64-linux-gnu"
output_dir="$repository_root/artifacts/spikes/aria2-interop"
project="$repository_root/spikes/CanX.Spikes.Aria2Interop/CanX.Spikes.Aria2Interop.csproj"
work_dir="$(mktemp -d "$output_dir.XXXXXX")"
server_pid=""

cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

source_dir="$work_dir/source"
download_dir="$work_dir/download"
mkdir -p "$source_dir" "$download_dir" "$output_dir"
dd if=/dev/urandom of="$source_dir/payload.bin" bs=1M count=8 status=none

port="$((20000 + RANDOM % 20000))"
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$source_dir" \
  >"$work_dir/http.log" 2>&1 &
server_pid="$!"

for _ in {1..50}; do
  if curl -fs --output /dev/null "http://127.0.0.1:$port/payload.bin"; then
    break
  fi
  sleep 0.1
done

dotnet publish "$project" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained \
  --output "$output_dir"

smoke_output="$(LD_LIBRARY_PATH="$native_build_dir:$native_dependency_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  timeout 45s "$output_dir/CanX.Spikes.Aria2Interop" \
  "http://127.0.0.1:$port/payload.bin" "$download_dir" payload.bin)"
printf '%s\n' "$smoke_output"
grep -q 'CANX_ARIA2_SHIM_OK' <<<"$smoke_output"
cmp "$source_dir/payload.bin" "$download_dir/payload.bin"
