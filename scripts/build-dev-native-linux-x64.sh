#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native/linux-x64}"
prefix_root="$static_root/prefix"
aria_install="$prefix_root/aria2"
aria_deps="$prefix_root/aria2-dependencies"
output_dir="${CANX_DEV_NATIVE_OUTPUT:-$repository_root/artifacts/native/aria2-shim-dev-linux-x64}"
sidecar="$output_dir/libcanx_aria2.so"
archives=(
  "$aria_install/lib/libaria2.a"
  "$aria_deps/lib/libssh2.a"
  "$aria_deps/lib/libcares.a"
  "$aria_deps/lib/libsqlite3.a"
  "$aria_deps/lib/libssl.a"
  "$aria_deps/lib/libcrypto.a"
  "$aria_deps/lib/libz.a"
  "$aria_deps/lib/libexpat.a"
)

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
  echo "the development aria2 sidecar currently supports only Linux x64" >&2
  exit 2
fi

for tool in cmake ldd ninja; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing development native build tool: $tool" >&2
    exit 2
  }
done

validate_sidecar() {
  local dependency_report
  [[ -f "$sidecar" ]] || {
    echo "development aria2 sidecar was not produced: $sidecar" >&2
    exit 1
  }

  if ! dependency_report="$(ldd -r "$sidecar" 2>&1)"; then
    printf '%s\n' "$dependency_report" >&2
    echo "could not inspect development aria2 sidecar dependencies" >&2
    exit 1
  fi
  if grep -q 'undefined symbol:' <<<"$dependency_report"; then
    printf '%s\n' "$dependency_report" >&2
    echo "development aria2 sidecar contains unresolved symbols" >&2
    exit 1
  fi
}

newer_shim_input=""
newer_archive_input=""
if [[ -f "$sidecar" ]]; then
  newer_shim_input="$(find \
    "$repository_root/native/aria2-shim/CMakeLists.txt" \
    "$repository_root/native/aria2-shim/include" \
    "$repository_root/native/aria2-shim/src" \
    -type f -newer "$sidecar" -print -quit)"
  newer_archive_input="$(find "${archives[@]}" -type f -newer "$sidecar" -print -quit 2>/dev/null || true)"
fi
if [[ -f "$sidecar" && -z "$newer_shim_input" && -z "$newer_archive_input" && \
      ! "$repository_root/native/sources-linux-x64.lock" -nt "$sidecar" ]]; then
  validate_sidecar
  printf 'CANX_DEV_ARIA2_SIDECAR_OK platform=linux-x64 output=%s cached=true\n' "$sidecar"
  exit 0
fi

for archive in "${archives[@]}"; do
  if [[ ! -f "$archive" ]]; then
    echo "Building the pinned aria2 dependency closure for the development sidecar..."
    CANX_STATIC_ROOT="$static_root" \
      "$repository_root/scripts/build-static-native-linux-x64.sh" aria2
    break
  fi
done

for archive in "${archives[@]}"; do
  [[ -f "$archive" ]] || {
    echo "required development archive was not produced: $archive" >&2
    exit 1
  }
done

dependencies=("${archives[@]:1}" m dl)
dependency_list="$(IFS=';'; printf '%s' "${dependencies[*]}")"
cmake -S "$repository_root/native/aria2-shim" -B "$output_dir" -G Ninja \
  -DCMAKE_BUILD_TYPE=RelWithDebInfo \
  -DCANX_ARIA2_LIBRARY_TYPE=SHARED \
  -DCANX_ARIA2_EXTENDED_API=ON \
  -DARIA2_INCLUDE_DIR="$aria_install/include" \
  -DARIA2_LIBRARY="${archives[0]}" \
  -DARIA2_DEPENDENCIES="$dependency_list"
cmake --build "$output_dir"

validate_sidecar
printf 'CANX_DEV_ARIA2_SIDECAR_OK platform=linux-x64 output=%s\n' "$sidecar"
