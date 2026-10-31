#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/spikes/CanX.Spikes.Aria2Interop/native-packages.lock"
package_dir="$repository_root/.cache/native/aria2/packages"
native_root="$repository_root/.cache/native/aria2/root"
build_dir="$repository_root/artifacts/native/aria2-shim"
deps_source="$("$repository_root/scripts/fetch-deps-source.sh")"

mkdir -p "$package_dir" "$native_root" "$build_dir"

while IFS= read -r package_spec; do
  [[ -z "$package_spec" || "$package_spec" == \#* ]] && continue
  (
    cd "$package_dir"
    apt-get download "$package_spec"
  )
done < "$lock_file"

for package_file in "$package_dir"/*.deb; do
  dpkg-deb --extract "$package_file" "$native_root"
done

cmake -S "$deps_source/native/aria2-shim" -B "$build_dir" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DARIA2_INCLUDE_DIR="$native_root/usr/include" \
  -DARIA2_LIBRARY="$native_root/usr/lib/x86_64-linux-gnu/libaria2.so"
cmake --build "$build_dir"

printf '%s\n' "$build_dir"
