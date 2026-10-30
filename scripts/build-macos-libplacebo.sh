#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Build the pinned libplacebo revision from native/sources-linux-x64.lock as a
# shared macOS dylib (OpenGL only, no Vulkan / glslang / shaderc), installed to
# .cache/macos/libplacebo-install. Consumed by
# scripts/build-macos-mpv-frameworks.sh; lcms2 support is picked up from the
# pkg-config paths configured there.
# Binaries must run on the deployment target declared in Info.plist (13.0)
# instead of the runner image's macOS version.
export MACOSX_DEPLOYMENT_TARGET="${CANX_MACOS_DEPLOYMENT_TARGET:-13.0}"
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/native/sources-linux-x64.lock"
source_dir="$repository_root/.cache/upstream/libplacebo"
build_dir="$repository_root/.cache/macos/libplacebo-build"
install_dir="$repository_root/.cache/macos/libplacebo-install"
venv="$repository_root/.cache/macos-build-venv"
revision="$(awk -F '|' '$1 == "libplacebo" { print $3; found=1; exit } END { if (!found) exit 1 }' "$lock_file")"

if [[ -f "$install_dir/lib/libplacebo.371.dylib" ]]; then
  printf '%s\n' "$install_dir/lib/libplacebo.371.dylib"
  exit 0
fi

if [[ ! -d "$source_dir/.git" ]]; then
  mkdir -p "$source_dir"
  git -C "$source_dir" init --quiet
  git -C "$source_dir" remote add origin https://code.videolan.org/videolan/libplacebo.git
fi
if [[ "$(git -C "$source_dir" rev-parse HEAD 2>/dev/null || true)" != "$revision" ]]; then
  git -C "$source_dir" fetch --depth=1 origin "$revision"
  git -C "$source_dir" -c advice.detachedHead=false checkout --detach FETCH_HEAD
fi
if [[ "$(git -C "$source_dir" rev-parse HEAD)" != "$revision" ]]; then
  echo "libplacebo source is not the pinned revision $revision" >&2
  exit 1
fi
git -C "$source_dir" submodule sync -- \
  3rdparty/glad 3rdparty/jinja 3rdparty/markupsafe \
  3rdparty/fast_float 3rdparty/Vulkan-Headers
git -C "$source_dir" submodule update --init --depth=1 -- \
  3rdparty/glad 3rdparty/jinja 3rdparty/markupsafe \
  3rdparty/fast_float

if [[ ! -x "$venv/bin/meson" ]]; then
  python3 -m venv "$venv"
  "$venv/bin/python" -m pip install 'meson==1.11.2'
fi

if [[ ! -f "$build_dir/build.ninja" ]]; then
  "$venv/bin/meson" setup "$build_dir" "$source_dir" \
    --prefix "$install_dir" --libdir lib --buildtype release -Ddefault_library=shared \
    -Dopengl=enabled -Dvulkan=disabled -Dglslang=disabled \
    -Dshaderc=disabled -Dd3d11=disabled -Ddemos=false -Dtests=false \
    -Dbench=false -Dfuzz=false -Dlcms=auto -Ddovi=disabled \
    -Dlibdovi=disabled -Dxxhash=disabled -Dunwind=disabled
fi
"$venv/bin/meson" compile -C "$build_dir"
"$venv/bin/meson" install -C "$build_dir"
library="$install_dir/lib/libplacebo.371.dylib"
nm -gU "$library" | grep ' _pl_log_create_[0-9][0-9]*$' >/dev/null || {
  echo "built libplacebo does not export the required API version" >&2
  exit 1
}
printf '%s\n' "$library"
