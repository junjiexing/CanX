#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/spikes/CanX.Spikes.MpvAvalonia/native-packages.lock"
package_dir="$repository_root/.cache/native/mpv/packages"
native_root="$repository_root/.cache/native/mpv/root"

mkdir -p "$package_dir" "$native_root"

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

library_dir="$native_root/usr/lib/x86_64-linux-gnu"
if LD_LIBRARY_PATH="$library_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    ldd "$library_dir/libmpv.so.2" | grep -q 'not found'; then
  LD_LIBRARY_PATH="$library_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    ldd "$library_dir/libmpv.so.2"
  exit 1
fi

printf '%s\n' "$library_dir"
