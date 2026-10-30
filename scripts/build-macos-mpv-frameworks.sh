#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Build libmpv and its runtime dependencies from the pinned sources in
# native/sources-linux-x64.lock and stage every resulting dylib into a macOS
# Frameworks directory. This replaces the former dependency on the prebuilt
# libraries shipped inside an installed IINA.app.
#
# Output: .cache/macos/mpv-frameworks, consumed by scripts/package-app-macos.sh.
#
# Tools expected outside the lock file (Homebrew or the runner image):
#   git cmake ninja pkg-config python3 make perl
#   Homebrew openssl@3 for FFmpeg's TLS backend; its dylibs are bundled at
#   packaging time alongside the other aria2 runtime dependencies.
# Binaries must run on the deployment target declared in Info.plist (13.0)
# instead of the runner image's macOS version.
export MACOSX_DEPLOYMENT_TARGET="${CANX_MACOS_DEPLOYMENT_TARGET:-13.0}"
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/native/sources-linux-x64.lock"
source_root="$repository_root/.cache/upstream"
macos_root="$repository_root/.cache/macos"
deps_prefix="$macos_root/mpv-deps"
build_root="$macos_root/mpv-build"
frameworks="$macos_root/mpv-frameworks"
meson_venv="$repository_root/.cache/macos-build-venv"
homebrew_root="${CANX_MACOS_HOMEBREW_ROOT:-/opt/homebrew}"
jobs="${CANX_BUILD_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '4')}"

case "$(uname -s),$(uname -m)" in
  Darwin,arm64|Darwin,x86_64) ;;
  *) echo "unsupported macOS build host: $(uname -s) $(uname -m)" >&2; exit 2 ;;
esac

for tool in git cmake ninja pkg-config python3 make perl nm otool install_name_tool; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing build tool: $tool" >&2
    exit 2
  }
done

if [[ ! -x "$meson_venv/bin/meson" ]]; then
  python3 -m venv "$meson_venv"
  "$meson_venv/bin/python" -m pip install 'meson==1.11.2'
fi
meson_executable="$meson_venv/bin/meson"

source_field() {
  local name="$1"
  local field="$2"
  awk -F '|' -v name="$name" -v field="$field" '$1 == name { print $field; found=1; exit } END { if (!found) exit 1 }' "$lock_file"
}

checkout_source() {
  local name="$1"
  local url revision source_dir current_revision
  local new_source=false
  url="$(source_field "$name" 2)"
  revision="$(source_field "$name" 3)"
  source_dir="$source_root/$name"

  if [[ -z "$url" || -z "$revision" ]]; then
    echo "missing pinned macOS source for: $name" >&2
    exit 1
  fi

  if [[ ! -d "$source_dir/.git" ]]; then
    mkdir -p "$source_dir"
    git -C "$source_dir" init --quiet
    git -C "$source_dir" remote add origin "$url"
    new_source=true
  fi

  current_revision="$(git -C "$source_dir" rev-parse --verify -q HEAD 2>/dev/null || true)"
  if [[ "$new_source" == true || "$current_revision" != "$revision" ]]; then
    if [[ "$new_source" == false && -n "$(git -C "$source_dir" status --porcelain 2>/dev/null)" ]]; then
      echo "refusing to replace modified cached source: $source_dir" >&2
      exit 1
    fi
    if [[ "$new_source" == false && -n "$current_revision" ]]; then
      git -C "$source_dir" reset -q --hard "$current_revision"
    fi
    local attempt fetched=false
    for attempt in 1 2 3; do
      if git -C "$source_dir" fetch --depth=1 origin "$revision"; then
        fetched=true
        break
      fi
      echo "git fetch retry $attempt/3 failed for $name" >&2
      sleep 3
    done
    if [[ "$fetched" != true ]]; then
      echo "unable to fetch pinned source $name at $revision" >&2
      exit 1
    fi
    git -C "$source_dir" -c advice.detachedHead=false checkout --detach FETCH_HEAD
  fi

  if [[ "$(git -C "$source_dir" rev-parse HEAD)" != "$revision" ]]; then
    echo "source revision mismatch: $name" >&2
    exit 1
  fi

  if [[ "$name" == libplacebo ]]; then
    git -C "$source_dir" submodule sync -- \
      3rdparty/glad 3rdparty/jinja 3rdparty/markupsafe \
      3rdparty/fast_float 3rdparty/Vulkan-Headers
    git -C "$source_dir" submodule update --init --depth=1 -- \
      3rdparty/glad 3rdparty/jinja 3rdparty/markupsafe \
      3rdparty/fast_float
  elif [[ "$name" == mpv ]]; then
    apply_source_patch "$source_dir" \
      "$repository_root/native/patches/mpv-macos-no-swift.patch"
  elif [[ -f "$source_dir/.gitmodules" ]]; then
    git -C "$source_dir" submodule sync --recursive
    git -C "$source_dir" submodule update --init --recursive --depth=1
  fi

  printf 'CANX_MACOS_SOURCE_OK name=%s revision=%s\n' "$name" "$revision"
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

built() {
  mkdir -p "$build_root"
  [[ ! -f "$build_root/.built-$1" ]]
}

mark_built() {
  mkdir -p "$build_root"
  touch "$build_root/.built-$1"
}

meson_build() {
  local name="$1"
  local source_dir="$2"
  local prefix="$3"
  shift 3
  local build_dir="$build_root/$name"

  if [[ ! -f "$build_dir/build.ninja" ]]; then
    "$meson_executable" setup "$build_dir" "$source_dir" \
      --prefix="$prefix" --libdir=lib --buildtype release \
      --default-library=shared "$@"
  fi
  "$meson_executable" compile -C "$build_dir" -j "$jobs"
  "$meson_executable" install -C "$build_dir"
}

for name in mpv ffmpeg libass harfbuzz freetype fribidi libunibreak lcms2 vulkan-headers; do
  checkout_source "$name"
done

mkdir -p "$deps_prefix" "$build_root"

# --- LittleCMS 2 (shared; linked by libplacebo and mpv) ----------------------
if built lcms2; then
  meson_build lcms2 "$source_root/lcms2" "$deps_prefix"
  mark_built lcms2
fi

# --- Vulkan-Headers (header-only; required by libplacebo's disabled-Vulkan stubs)
if built vulkan-headers; then
  mkdir -p "$deps_prefix/include"
  cp -R "$source_root/vulkan-headers/include/"* "$deps_prefix/include/"
  mark_built vulkan-headers
fi

# --- HarfBuzz ----------------------------------------------------------------
if built harfbuzz; then
  meson_build harfbuzz "$source_root/harfbuzz" "$deps_prefix" \
    -Dauto_features=disabled -Dglib=disabled -Dgobject=disabled \
    -Dcairo=disabled -Dicu=disabled -Dchafa=disabled -Dtests=disabled \
    -Dintrospection=disabled -Ddocs=disabled -Dutilities=disabled \
    -Dbenchmark=disabled
  mark_built harfbuzz
fi

# --- FreeType (system zlib; no external image codecs) ------------------------
if built freetype; then
  CPPFLAGS="-I$deps_prefix/include" \
  LDFLAGS="-L$deps_prefix/lib" \
  PKG_CONFIG_PATH="$deps_prefix/lib/pkgconfig" \
    meson_build freetype "$source_root/freetype" "$deps_prefix" \
      -Dharfbuzz=disabled -Dbrotli=disabled -Dbzip2=disabled \
      -Dpng=disabled -Dzlib=enabled
  mark_built freetype
fi

# --- FriBidi -----------------------------------------------------------------
if built fribidi; then
  meson_build fribidi "$source_root/fribidi" "$deps_prefix" \
    -Ddocs=false -Dtests=false -Dbin=false
  mark_built fribidi
fi

# --- libunibreak (static, linked into libass) ---------------------------------
if built libunibreak; then
  mkdir -p "$build_root/libunibreak"
  (
    cd "$build_root/libunibreak"
    CFLAGS='-O2 -fPIC' "$source_root/libunibreak/configure" \
      --prefix="$deps_prefix" --enable-static --disable-shared
    make -j "$jobs"
    make install
  )
  mark_built libunibreak
fi

# --- libass ------------------------------------------------------------------
if built libass; then
  PKG_CONFIG_PATH="$deps_prefix/lib/pkgconfig" \
    meson_build libass "$source_root/libass" "$deps_prefix" \
      -Dauto_features=disabled -Dcoretext=enabled -Ddirectwrite=disabled \
      -Dfontconfig=disabled -Dasm=enabled -Dlibunibreak=enabled \
      -Dtest=disabled -Dcompare=disabled -Dprofile=disabled \
      -Dfuzz=disabled -Dcheckasm=disabled
  mark_built libass
fi

# --- libplacebo (pinned lock revision, shared, OpenGL only) -------------------
placebo_install="$macos_root/libplacebo-install"
if [[ ! -f "$placebo_install/lib/libplacebo.371.dylib" ]]; then
  PKG_CONFIG_PATH="$deps_prefix/lib/pkgconfig" \
    "$repository_root/scripts/build-macos-libplacebo.sh"
fi

# --- FFmpeg ------------------------------------------------------------------
if built ffmpeg; then
  mkdir -p "$build_root/ffmpeg"
  (
    cd "$build_root/ffmpeg"
    PKG_CONFIG_PATH="$deps_prefix/lib/pkgconfig:$homebrew_root/opt/openssl@3/lib/pkgconfig" \
    "$source_root/ffmpeg/configure" \
      --prefix="$deps_prefix" --libdir="$deps_prefix/lib" \
      --enable-shared --disable-static \
      --disable-programs --disable-doc --disable-debug \
      --disable-autodetect --enable-pthreads \
      --enable-gpl --enable-version3 \
      --enable-zlib --enable-bzlib \
      --enable-videotoolbox --enable-audiotoolbox \
      --enable-openssl \
      --extra-cflags="-I$deps_prefix/include" \
      --extra-ldflags="-L$deps_prefix/lib -L$homebrew_root/opt/openssl@3/lib"
    make -j "$jobs"
    make install
  )
  mark_built ffmpeg
fi

# --- mpv (libmpv only: Render API, no CLI player, no scripting) ---------------
if ! PKG_CONFIG_PATH="$placebo_install/lib/pkgconfig" \
     pkg-config --atleast-version=7.360.1 libplacebo; then
  found_version="$(PKG_CONFIG_PATH="$placebo_install/lib/pkgconfig" \
    pkg-config --modversion libplacebo 2>/dev/null || printf 'unknown')"
  echo "mpv requires libplacebo >= 7.360.1, found: $found_version" >&2
  exit 1
fi

if built mpv; then
  PKG_CONFIG_PATH="$placebo_install/lib/pkgconfig:$deps_prefix/lib/pkgconfig" \
  LDFLAGS="-L$deps_prefix/lib" \
    meson_build mpv "$source_root/mpv" "$deps_prefix" \
      -Dgpl=true -Dcplayer=false -Dlibmpv=true \
      -Dtests=false -Dfuzzers=false \
      -Dlua=disabled -Djavascript=disabled \
      -Dauto_features=disabled \
      -Dcocoa=enabled -Dgl=enabled -Dplain-gl=enabled -Dgl-cocoa=enabled \
      -Dswift-build=disabled \
      -Dcoreaudio=enabled \
      -Dvideotoolbox-gl=enabled \
      -Dvideotoolbox-pl=disabled \
      -Diconv=enabled -Dzlib=enabled -Dlcms2=enabled
  nm -gU "$deps_prefix/lib/libmpv.2.dylib" | grep ' _mpv_client_api_version$' >/dev/null || {
    echo "built libmpv does not export the client API" >&2
    exit 1
  }
  mark_built mpv
fi

# --- Stage shared dylibs and make all references rpath-based ------------------
rm -rf "$frameworks"
mkdir -p "$frameworks"

# Copy versioned dylibs only: the install prefixes also hold unversioned
# symlinks (e.g. libavcodec.dylib -> libavcodec.62.dylib) that must stay out
# of the bundle, and cp would otherwise duplicate or dangle on them.
for library_file in "$deps_prefix/lib/"*.dylib "$placebo_install/lib/"*.dylib; do
  [[ -f "$library_file" && ! -L "$library_file" ]] || continue
  cp "$library_file" "$frameworks/"
done

# A staged dylib's file name must match the name its consumers reference.
# FFmpeg installs full-version file names (libavcodec.62.11.100.dylib) while
# its own install id and sibling references use short names
# (libavcodec.62.dylib), so rename staged copies to their install id basename.
for library in "$frameworks"/*.dylib; do
  own_id="$(otool -D "$library" | tail -n +2 | head -n 1 | awk '{print $1}')"
  if [[ -z "$own_id" ]]; then
    echo "staged dylib without install id: $library" >&2
    exit 1
  fi
  desired_name="$(basename "$own_id")"
  staged_name="$(basename "$library")"
  if [[ "$staged_name" != "$desired_name" ]]; then
    if [[ -e "$frameworks/$desired_name" ]]; then
      echo "duplicate staged dylib name: $desired_name" >&2
      exit 1
    fi
    mv "$library" "$frameworks/$desired_name"
  fi
done

is_system_path() {
  case "$1" in
    /usr/lib/*|/System/*) return 0 ;;
    *) return 1 ;;
  esac
}

# bash 3.2 has no associative arrays: track referenced dylib names as one
# name per line in a scratch file.
reference_tmp="$build_root/staged-references.txt"
reference_next="$reference_tmp.next"
: > "$reference_tmp"

for library in "$frameworks"/*.dylib; do
  name="$(basename "$library")"
  install_name_tool -id "@rpath/$name" "$library"
  otool -L "$library" | tail -n +2 | awk '{print $1}' > "$reference_next"
  while IFS= read -r reference_path; do
    [[ -n "$reference_path" ]] || continue
    if is_system_path "$reference_path"; then
      continue
    fi
    reference_name="$(basename "$reference_path")"
    install_name_tool -change "$reference_path" "@rpath/$reference_name" "$library"
    printf '%s\n' "$reference_name" >> "$reference_tmp"
  done < "$reference_next"
done
rm -f "$reference_next"

# Every non-system dependency must own a staged dylib or be provided by the
# Homebrew runtime set that package-app-macos.sh bundles: OpenSSL (FFmpeg TLS),
# SQLite, libssh2 and c-ares (libcanx_aria2, shared with the mpv dylibs).
for referenced_name in $(sort -u "$reference_tmp"); do
  case "$referenced_name" in
    libssl.3.dylib|libcrypto.3.dylib|libsqlite3.dylib|libssh2.1.dylib|libcares.2.dylib)
      continue
      ;;
  esac
  if [[ ! -f "$frameworks/$referenced_name" ]]; then
    echo "unresolved staged dependency: $referenced_name" >&2
    exit 1
  fi
done
rm -f "$reference_tmp"

printf 'CANX_MACOS_MPV_FRAMEWORKS_OK dir=%s\n' "$frameworks"
