#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/spikes/CanX.Spikes.QuickJsInterop/native-source.lock"
cache_dir="$repository_root/.cache/native/quickjs"
archive="$cache_dir/quickjs.tar.xz"
source_dir="$cache_dir/source"
library_type="${CANX_QUICKJS_LIBRARY_TYPE:-SHARED}"
case "$library_type" in
  SHARED) build_name="quickjs-shim" ;;
  STATIC) build_name="quickjs-shim-static" ;;
  *)
    echo "CANX_QUICKJS_LIBRARY_TYPE must be SHARED or STATIC" >&2
    exit 2
    ;;
esac
build_dir="$repository_root/artifacts/native/$build_name"
deps_source="$("$repository_root/scripts/fetch-deps-source.sh")"

source "$lock_file"
mkdir -p "$cache_dir" "$source_dir" "$build_dir"

if [[ ! -f "$archive" ]] || ! printf '%s  %s\n' "$sha256" "$archive" | sha256sum --check --status; then
  curl -fsSL "$url" --output "$archive"
fi
printf '%s  %s\n' "$sha256" "$archive" | sha256sum --check --status

if [[ ! -f "$source_dir/VERSION" ]] || [[ "$(<"$source_dir/VERSION")" != "$version" ]]; then
  tar -xJf "$archive" -C "$source_dir" --strip-components=1
fi

cmake -S "$deps_source/native/quickjs-shim" -B "$build_dir" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCANX_QUICKJS_LIBRARY_TYPE="$library_type" \
  -DQUICKJS_SOURCE_DIR="$source_dir"
cmake --build "$build_dir"

printf '%s\n' "$build_dir"
