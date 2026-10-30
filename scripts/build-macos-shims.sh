#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Build the shared libcanx_quickjs and libcanx_aria2 dylibs that CanX.Spp loads
# at runtime together with the bundled libmpv Frameworks on macOS.
#
#  - QuickJS: tarball from spikes/CanX.Spikes.QuickJsInterop/native-source.lock,
#    compiled through native/quickjs-shim CMake into
#    .cache/native/quickjs/macos-build/libcanx_quickjs.dylib
#  - aria2: pinned fork from native/sources-linux-x64.lock, configured against
#    Homebrew openssl@3 / sqlite / libssh2 / c-ares (the same dylibs that
#    scripts/package-app-macos.sh bundles) and wrapped by native/aria2-shim into
#    .cache/mac-native/aria2-shim/libcanx_aria2.dylib
#
# Tools expected outside the lock file (Homebrew or the runner image):
#   git cmake ninja pkg-config make perl autoconf automake libtool
# Binaries must run on the deployment target declared in Info.plist (13.0)
# instead of the runner image's macOS version.
export MACOSX_DEPLOYMENT_TARGET="${CANX_MACOS_DEPLOYMENT_TARGET:-13.0}"
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/native/sources-linux-x64.lock"
source_root="$repository_root/.cache/upstream"
build_root="$repository_root/.cache/mac-native"
aria2_prefix="$build_root/aria2/prefix"
aria2_shim_build="$build_root/aria2-shim"
aria2_shim="$aria2_shim_build/libcanx_aria2.dylib"
quickjs_root="$repository_root/.cache/native/quickjs"
quickjs_build="$quickjs_root/macos-build"
quickjs_library="$quickjs_build/libcanx_quickjs.dylib"
jobs="${CANX_BUILD_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '4')}"

case "$(uname -s),$(uname -m)" in
  Darwin,arm64|Darwin,x86_64) ;;
  *) echo "unsupported macOS build host: $(uname -s) $(uname -m)" >&2; exit 2 ;;
esac

for tool in git cmake ninja pkg-config make perl autoreconf xcrun; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing build tool: $tool" >&2
    exit 2
  }
done

source_field() {
  local name="$1"
  local field="$2"
  awk -F '|' -v name="$name" -v field="$field" '$1 == name { print $field; found=1; exit } END { if (!found) exit 1 }' "$lock_file"
}

apply_source_patch() {
  local source_dir="$1"
  local patch_file="$2"
  if git -C "$source_dir" apply --reverse --check "$patch_file" >/dev/null 2>&1; then
    return
  fi
  git -C "$source_dir" apply --check "$patch_file"
  git -C "$source_dir" apply "$patch_file"
}

# ---------------------------------------------------------------------------
# QuickJS
# ---------------------------------------------------------------------------
source "$repository_root/spikes/CanX.Spikes.QuickJsInterop/native-source.lock"
archive="$quickjs_root/quickjs.tar.xz"

if [[ ! -f "$archive" ]] || [[ "$(shasum -a 256 "$archive" | awk '{ print $1 }')" != "$sha256" ]]; then
  curl -fsSL "$url" --output "$archive"
fi
[[ "$(shasum -a 256 "$archive" | awk '{ print $1 }')" == "$sha256" ]] || {
  echo 'QuickJS source checksum mismatch' >&2
  exit 1
}
if [[ ! -f "$quickjs_root/source/VERSION" ]] || [[ "$(<"$quickjs_root/source/VERSION")" != "$version" ]]; then
  rm -rf "$quickjs_root/source"
  tar -xJf "$archive" -C "$quickjs_root/source" --strip-components=1
fi

if [[ ! -f "$quickjs_library" ]]; then
  cmake -S "$repository_root/native/quickjs-shim" -B "$quickjs_build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCANX_QUICKJS_LIBRARY_TYPE=SHARED \
    -DQUICKJS_SOURCE_DIR="$quickjs_root/source"
  cmake --build "$quickjs_build" --parallel "$jobs"
fi

# ---------------------------------------------------------------------------
# aria2
# ---------------------------------------------------------------------------
url="$(source_field aria2 2)"
revision="$(source_field aria2 3)"
if [[ -z "$url" || -z "$revision" ]]; then
  echo 'missing pinned aria2 source' >&2
  exit 1
fi

source_dir="$source_root/aria2"
if [[ ! -d "$source_dir/.git" ]]; then
  mkdir -p "$source_dir"
  git -C "$source_dir" init --quiet
  git -C "$source_dir" remote add origin "$url"
fi
if [[ "$(git -C "$source_dir" rev-parse HEAD 2>/dev/null || true)" != "$revision" ]]; then
  git -C "$source_dir" fetch --depth=1 origin "$revision"
  git -C "$source_dir" -c advice.detachedHead=false checkout --detach FETCH_HEAD
fi
if [[ "$(git -C "$source_dir" rev-parse HEAD)" != "$revision" ]]; then
  echo 'aria2 source revision mismatch' >&2
  exit 1
fi
apply_source_patch "$source_dir" \
  "$repository_root/native/patches/aria2-runtime-priority-clear.patch"

if [[ ! -f "$aria2_prefix/lib/libaria2.a" ]]; then
  autotools_dir="$build_root/aria2/autotools"
  build_dir="$build_root/aria2/build"
  rm -rf "$autotools_dir" "$build_dir"
  mkdir -p "$(dirname "$autotools_dir")"
  cp -R "$source_dir" "$autotools_dir"
  (
    cd "$autotools_dir"
    autoreconf -fi
  )
  mkdir -p "$build_dir" "$aria2_prefix"
  (
    cd "$build_dir"
    PKG_CONFIG_PATH="/opt/homebrew/opt/openssl@3/lib/pkgconfig:/opt/homebrew/opt/libssh2/lib/pkgconfig:/opt/homebrew/opt/c-ares/lib/pkgconfig:/opt/homebrew/opt/sqlite/lib/pkgconfig" \
    CPPFLAGS="-I/opt/homebrew/opt/openssl@3/include -I/opt/homebrew/include" \
    LDFLAGS="-L/opt/homebrew/opt/openssl@3/lib -L/opt/homebrew/lib" \
    CFLAGS='-O2 -fPIC' \
    CXXFLAGS='-O2 -fPIC' \
      "$autotools_dir/configure" \
        --prefix="$aria2_prefix" \
        --enable-libaria2 --disable-aria2c \
        --enable-static --disable-shared \
        --disable-nls --without-libuv --without-gnutls \
        --with-openssl --without-libxml2 --with-libexpat \
        --with-sqlite3 --with-libcares --with-libssh2 --with-libz
    make -j "$jobs"
    make install
  )
fi

if [[ ! -f "$aria2_shim" ]]; then
  rm -rf "$aria2_shim_build"
  cmake -S "$repository_root/native/aria2-shim" -B "$aria2_shim_build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DARIA2_INCLUDE_DIR="$aria2_prefix/include" \
    -DARIA2_LIBRARY="$aria2_prefix/lib/libaria2.a" \
    -DARIA2_DEPENDENCIES="-Wl,-framework,CoreFoundation;-Wl,-framework,Security;-lz;-lexpat;/opt/homebrew/opt/sqlite/lib/libsqlite3.dylib;/opt/homebrew/opt/libssh2/lib/libssh2.dylib;/opt/homebrew/opt/c-ares/lib/libcares.dylib" \
    -DCANX_ARIA2_LIBRARY_TYPE=SHARED \
    -DCANX_ARIA2_EXTENDED_API=ON
  cmake --build "$aria2_shim_build" --parallel "$jobs"
fi

printf 'CANX_MACOS_SHIMS_OK quickjs=%s aria2=%s\n' "$quickjs_library" "$aria2_shim"
