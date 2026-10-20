#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="$repository_root/.cache/macos/libplacebo-v7.349.0"
build_dir="$repository_root/.cache/macos/libplacebo-349-build"
install_dir="$repository_root/.cache/macos/libplacebo-349-install"
venv="$repository_root/.cache/macos-build-venv"
library="$install_dir/lib/libplacebo.349.dylib"
revision="1fd3c7bde7b943fe8985c893310b5269a09b46c5"

if [[ -f "$library" ]]; then
  printf '%s\n' "$library"
  exit 0
fi

if [[ ! -d "$source_dir/.git" && ! -f "$source_dir/.git" ]]; then
  git clone --depth 1 --branch v7.349.0 --single-branch \
    https://code.videolan.org/videolan/libplacebo.git "$source_dir"
fi
if [[ "$(git -C "$source_dir" rev-parse HEAD)" != "$revision" ]]; then
  echo "libplacebo source is not the pinned v7.349.0 revision" >&2
  exit 1
fi
git -C "$source_dir" submodule update --init --depth 1 \
  3rdparty/glad 3rdparty/jinja 3rdparty/markupsafe \
  3rdparty/fast_float 3rdparty/Vulkan-Headers

if [[ ! -x "$venv/bin/meson" ]]; then
  python3 -m venv "$venv"
  "$venv/bin/python" -m pip install 'meson==1.11.2'
fi

if [[ ! -f "$build_dir/build.ninja" ]]; then
  "$venv/bin/meson" setup "$build_dir" "$source_dir" \
    --prefix "$install_dir" --buildtype release -Ddefault_library=shared \
    -Dopengl=enabled -Dvulkan=disabled -Dglslang=disabled \
    -Dshaderc=disabled -Dd3d11=disabled -Ddemos=false -Dtests=false \
    -Dbench=false -Dfuzz=false -Dlcms=auto -Ddovi=disabled \
    -Dlibdovi=disabled -Dxxhash=disabled -Dunwind=disabled
fi
"$venv/bin/meson" compile -C "$build_dir"
"$venv/bin/meson" install -C "$build_dir"
nm -gU "$library" | grep ' _pl_log_create_349$' >/dev/null || {
  echo "built libplacebo does not export the required API version" >&2
  exit 1
}
printf '%s\n' "$library"
