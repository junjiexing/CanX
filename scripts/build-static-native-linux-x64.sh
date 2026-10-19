#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/native/sources-linux-x64.lock"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native/linux-x64}"
source_root="$static_root/sources"
build_root="$static_root/build"
prefix_root="$static_root/prefix"
jobs="${CANX_BUILD_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '4')}"
component="${1:-all}"

case "$component" in
  all|aria2|mpv|avalonia) ;;
  *)
    echo "usage: $0 [all|aria2|mpv|avalonia]" >&2
    exit 2
    ;;
esac

for tool in git cmake ninja pkg-config autoreconf make perl python3 ar; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing build tool: $tool" >&2
    exit 2
  }
done

if [[ "$component" == all || "$component" == avalonia ]]; then
  for tool in clang clang++ ar install; do
    command -v "$tool" >/dev/null 2>&1 || {
      echo "missing SkiaSharp build tool: $tool" >&2
      exit 2
    }
  done
fi

meson_executable=""
meson_pythonpath="${CANX_MESON_PYTHONPATH:-}"
if [[ "$component" == all || "$component" == mpv || "$component" == avalonia ]]; then
  if [[ -n "${CANX_MESON:-}" ]]; then
    meson_executable="$CANX_MESON"
  else
    # Always prefer the locked source checkout. Ubuntu 22.04's packaged Meson
    # is too old for current mpv/libplacebo and would make the result depend on
    # which development host happened to run the build.
    meson_executable="$source_root/meson/meson.py"
  fi

  if [[ -z "$meson_pythonpath" ]]; then
    meson_dist_packages="$(dirname "$meson_executable")/../lib/python3/dist-packages"
    if [[ -d "$meson_dist_packages/mesonbuild" ]]; then
      meson_pythonpath="$meson_dist_packages"
    fi
  fi
fi

mkdir -p "$source_root" "$build_root" "$prefix_root"

run_meson() {
  if [[ -n "$meson_pythonpath" ]]; then
    PYTHONPATH="$meson_pythonpath${PYTHONPATH:+:$PYTHONPATH}" \
      "$meson_executable" "$@"
  else
    "$meson_executable" "$@"
  fi
}

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

  if [[ ! -d "$source_dir/.git" ]]; then
    mkdir -p "$source_dir"
    git -C "$source_dir" init --quiet
    git -C "$source_dir" remote add origin "$url"
    new_source=true
  fi

  current_revision="$(git -C "$source_dir" rev-parse HEAD 2>/dev/null || true)"
  if [[ "$new_source" == true || "$current_revision" != "$revision" ]]; then
    if [[ "$new_source" == false && -n "$(git -C "$source_dir" status --porcelain 2>/dev/null)" ]]; then
      echo "refusing to replace modified cached source: $source_dir" >&2
      exit 1
    fi
    git -C "$source_dir" fetch --depth=1 origin "$revision"
    git -C "$source_dir" -c advice.detachedHead=false checkout --detach FETCH_HEAD
  fi

  if [[ "$(git -C "$source_dir" rev-parse HEAD)" != "$revision" ]]; then
    echo "source revision mismatch: $name" >&2
    exit 1
  fi

  if [[ "$name" == skiasharp ]]; then
    git -C "$source_dir" submodule sync -- \
      externals/depot_tools externals/skia
    git -C "$source_dir" submodule update --init --depth=1 -- \
      externals/depot_tools externals/skia
  elif [[ "$name" != openssl && -f "$source_dir/.gitmodules" ]]; then
    git -C "$source_dir" submodule sync --recursive
    git -C "$source_dir" submodule update --init --recursive --depth=1
  fi
}

cmake_build() {
  local name="$1"
  local source_dir="$2"
  local prefix="$3"
  shift 3
  local build_dir="$build_root/$name"

  cmake -S "$source_dir" -B "$build_dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$prefix" \
    -DCMAKE_INSTALL_LIBDIR=lib \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    "$@"
  cmake --build "$build_dir" --parallel "$jobs"
  cmake --install "$build_dir"
}

meson_build() {
  local name="$1"
  local source_dir="$2"
  local prefix="$3"
  shift 3
  local build_dir="$build_root/$name"
  local common_options=(
    --prefix="$prefix"
    --libdir=lib
    --buildtype=release
    --default-library=static
    --prefer-static
    -Db_staticpic=true
  )

  if [[ -f "$build_dir/meson-private/coredata.dat" ]]; then
    run_meson setup --reconfigure "$build_dir" "$source_dir" "${common_options[@]}" "$@"
  else
    run_meson setup "$build_dir" "$source_dir" "${common_options[@]}" "$@"
  fi
  run_meson compile -C "$build_dir" -j "$jobs"
  run_meson install -C "$build_dir"
}

assert_archive() {
  local archive="$1"
  [[ -f "$archive" ]] || {
    echo "expected static archive was not produced: $archive" >&2
    exit 1
  }
}

assert_config_enabled() {
  local config_file="$1"
  shift
  local feature
  for feature in "$@"; do
    if ! grep -Eq "^#define ${feature} 1$" "$config_file"; then
      echo "required build feature is not enabled: $feature ($config_file)" >&2
      exit 1
    fi
  done
}

assert_no_shared_libraries() {
  local prefix="$1"
  local shared
  shared="$(find "$prefix" -type f \( -name '*.so' -o -name '*.so.*' \) -print -quit)"
  if [[ -n "$shared" ]]; then
    echo "shared library unexpectedly installed: $shared" >&2
    exit 1
  fi
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

build_expat() {
  local name="$1"
  local prefix="$2"
  cmake_build "$name" "$source_root/expat/expat" "$prefix" \
    -DEXPAT_SHARED_LIBS=OFF \
    -DEXPAT_BUILD_DOCS=OFF \
    -DEXPAT_BUILD_EXAMPLES=OFF \
    -DEXPAT_BUILD_FUZZERS=OFF \
    -DEXPAT_BUILD_TESTS=OFF \
    -DEXPAT_BUILD_TOOLS=OFF \
    -DEXPAT_BUILD_PKGCONFIG=ON
}

build_zlib() {
  local prefix="$1"
  if [[ ! -f "$prefix/lib/libz.a" ]]; then
    rm -rf "$build_root/zlib-mpv"
    mkdir -p "$build_root/zlib-mpv"
    (
      cd "$build_root/zlib-mpv"
      CFLAGS='-O2 -fPIC' "$source_root/zlib/configure" \
        --static --prefix="$prefix" --libdir="$prefix/lib"
      make -j "$jobs"
      make install
    )
  fi
}

build_openssl() {
  local prefix="$1"
  if [[ ! -f "$prefix/lib/libcrypto.a" ]]; then
    (
      cd "$source_root/openssl"
      if [[ -f Makefile ]]; then
        make distclean
      fi
      ./Configure \
        --prefix="$prefix" --openssldir="$prefix/ssl" --libdir=lib \
        -fPIC no-apps no-docs no-dso no-module no-shared no-tests no-zlib
      make -j "$jobs"
      make install_sw
    )
  fi
}

build_aria2() {
  local deps="$prefix_root/aria2-dependencies"
  local install="$prefix_root/aria2"
  local pkg_config_path="$deps/lib/pkgconfig"

  for source in aria2 c-ares expat libssh2 openssl sqlite zlib; do
    checkout_source "$source"
  done
  apply_source_patch "$source_root/aria2" \
    "$repository_root/native/patches/aria2-runtime-priority-clear.patch"
  mkdir -p "$deps" "$install"

  if [[ ! -f "$deps/lib/libz.a" ]]; then
    (
      cd "$source_root/zlib"
      CFLAGS='-O2 -fPIC' ./configure --static --prefix="$deps" --libdir="$deps/lib"
      make -j "$jobs"
      make install
    )
  fi

  if [[ ! -f "$deps/lib/libcrypto.a" ]]; then
    (
      cd "$source_root/openssl"
      ./Configure --prefix="$deps" --openssldir="$deps/ssl" --libdir=lib \
        -fPIC no-apps no-docs no-dso no-module no-shared no-tests no-zlib
      make -j "$jobs"
      make install_sw
    )
  fi

  if [[ ! -f "$deps/lib/libsqlite3.a" ]]; then
    mkdir -p "$build_root/sqlite"
    (
      cd "$build_root/sqlite"
      CFLAGS='-O2 -fPIC' "$source_root/sqlite/configure" \
        --prefix="$deps" --disable-shared --disable-tcl --disable-readline
      make -j "$jobs"
      make install
    )
  fi

  cmake_build c-ares "$source_root/c-ares" "$deps" \
    -DCARES_STATIC=ON -DCARES_SHARED=OFF \
    -DCARES_BUILD_TOOLS=OFF -DCARES_BUILD_TESTS=OFF
  build_expat expat-aria2 "$deps"
  cmake_build libssh2 "$source_root/libssh2" "$deps" \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON \
    -DBUILD_EXAMPLES=OFF -DBUILD_TESTING=OFF \
    -DCRYPTO_BACKEND=OpenSSL \
    -DOPENSSL_ROOT_DIR="$deps" \
    -DZLIB_LIBRARY="$deps/lib/libz.a" \
    -DZLIB_INCLUDE_DIR="$deps/include" \
    -DCMAKE_PREFIX_PATH="$deps"

  (
    cd "$source_root/aria2"
    autoreconf -fi
  )
  mkdir -p "$build_root/aria2"
  (
    cd "$build_root/aria2"
    PKG_CONFIG_PATH="$pkg_config_path" \
    CPPFLAGS="-I$deps/include" \
    CFLAGS='-O2 -fPIC' \
    CXXFLAGS='-O2 -fPIC' \
    LDFLAGS="-L$deps/lib" \
      "$source_root/aria2/configure" \
        --prefix="$install" \
        --enable-libaria2 --disable-aria2c \
        --enable-static --disable-shared \
        --disable-nls --without-libuv --without-gnutls \
        --with-openssl --without-libxml2 --with-libexpat \
        --with-sqlite3 --with-libcares --with-libssh2 --with-libz
    make -j "$jobs"
    make install
  )

  for archive in \
    "$install/lib/libaria2.a" \
    "$deps/lib/libcares.a" \
    "$deps/lib/libcrypto.a" \
    "$deps/lib/libexpat.a" \
    "$deps/lib/libsqlite3.a" \
    "$deps/lib/libssh2.a" \
    "$deps/lib/libssl.a" \
    "$deps/lib/libz.a"; do
    assert_archive "$archive"
  done
  assert_no_shared_libraries "$deps"
  assert_no_shared_libraries "$install"
}

build_mpv() {
  local deps="$prefix_root/mpv-dependencies"
  local install="$prefix_root/mpv"
  local pkg_config_path="$deps/lib/pkgconfig"

  for source in \
    mpv ffmpeg nv-codec-headers amf onevpl libdrm libva \
    vulkan-headers glslang opencl-headers opencl-loader \
    libdisplay-info nasm meson dav1d libvpx opus ogg vorbis \
    libjpeg-turbo openjpeg zimg lcms2 uchardet mujs lua libarchive \
    alsa-lib openssl zlib libass libplacebo harfbuzz freetype fribidi \
    fontconfig libunibreak expat gperf; do
    checkout_source "$source"
  done
  mkdir -p "$deps" "$install"

  build_zlib "$deps"
  build_openssl "$deps"

  if [[ ! -x "$deps/bin/nasm" ]]; then
    (
      cd "$source_root/nasm"
      ./autogen.sh
    )
    rm -rf "$build_root/nasm"
    mkdir -p "$build_root/nasm"
    (
      cd "$build_root/nasm"
      "$source_root/nasm/configure" --prefix="$deps"
      make -j "$jobs"
      install -d "$deps/bin"
      install -m 0755 nasm ndisasm "$deps/bin/"
    )
  fi
  export PATH="$deps/bin:$PATH"

  make -C "$source_root/nv-codec-headers" PREFIX="$deps" install

  # AMF is header-only at build time on Linux. FFmpeg loads the AMD AMF
  # runtime on demand, so machines without an AMD driver remain supported.
  install -d "$deps/include/AMF"
  cp -a "$source_root/amf/amf/public/include/." "$deps/include/AMF/"

  # Vulkan-Headers intentionally contains no loader implementation. Linux's
  # Khronos loader is a system/driver ABI and upstream only supports a shared
  # loader on Linux, so the final application links libvulkan.so.1 while all
  # controllable media libraries remain static.
  cmake_build vulkan-headers "$source_root/vulkan-headers" "$deps" \
    -DVULKAN_HEADERS_ENABLE_TESTS=OFF \
    -DVULKAN_HEADERS_ENABLE_INSTALL=ON \
    -DVULKAN_HEADERS_ENABLE_MODULE=OFF
  mkdir -p "$deps/lib/pkgconfig"
  # Link the stable loader SONAME directly. Ubuntu installs the runtime
  # loader as libvulkan.so.1, while the unversioned libvulkan.so symlink is
  # only supplied by the optional development package. The loader itself is
  # deliberately a runtime dependency; all Vulkan headers remain pinned.
  printf '%s\n' \
    "prefix=$deps" \
    'includedir=${prefix}/include' \
    '' \
    'Name: Vulkan' \
    'Description: Vulkan loader ABI with pinned CanX headers' \
    'Version: 1.4.321' \
    'Libs: -l:libvulkan.so.1' \
    'Cflags: -I${includedir}' > "$deps/lib/pkgconfig/vulkan.pc"

  cmake_build opencl-headers "$source_root/opencl-headers" "$deps" \
    -DOPENCL_HEADERS_BUILD_TESTING=OFF \
    -DOPENCL_HEADERS_BUILD_CXX_TESTS=OFF \
    -DBUILD_TESTING=OFF
  cmake_build opencl-loader "$source_root/opencl-loader" "$deps" \
    -DOPENCL_ICD_LOADER_BUILD_SHARED_LIBS=OFF \
    -DOPENCL_ICD_LOADER_BUILD_TESTING=OFF \
    -DOPENCL_ICD_LOADER_HEADERS_DIR="$source_root/opencl-headers" \
    -DBUILD_TESTING=OFF

  PKG_CONFIG_PATH="$pkg_config_path" meson_build libdrm "$source_root/libdrm" "$deps" \
    -Dintel=disabled -Dradeon=disabled -Damdgpu=disabled \
    -Dnouveau=disabled -Dvmwgfx=disabled -Domap=disabled \
    -Dexynos=disabled -Dfreedreno=disabled -Dtegra=disabled \
    -Dvc4=disabled -Detnaviv=disabled -Dcairo-tests=disabled \
    -Dman-pages=disabled -Dvalgrind=disabled -Dtests=false -Dudev=false

  # libva's core and DRM frontend are static. Only the vendor driver selected
  # at runtime remains dynamic. Ubuntu x64 keeps those drivers in this stable
  # multiarch directory; LIBVA_DRIVERS_PATH can override it when packaging.
  apply_source_patch "$source_root/libva" \
    "$repository_root/native/patches/libva-static-libraries.patch"
  rm -rf "$build_root/libva"
  find "$deps/lib" -maxdepth 1 \( -type f -o -type l \) \
    -name 'libva*.so*' -delete
  PKG_CONFIG_PATH="$pkg_config_path" meson_build libva "$source_root/libva" "$deps" \
    -Ddisable_drm=false -Dwith_x11=no -Dwith_glx=no -Dwith_wayland=no \
    -Dwith_win32=no -Dwith_legacy=[] -Denable_docs=false \
    -Ddriverdir=/usr/lib/x86_64-linux-gnu/dri

  cmake_build onevpl "$source_root/onevpl" "$deps" \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_DISPATCHER=ON -DBUILD_DEV=ON \
    -DBUILD_TOOLS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_TESTS=OFF \
    -DBUILD_PREVIEW=OFF -DINSTALL_EXAMPLE_CODE=OFF

  # glslang's known_good.json pins SPIRV-Tools and SPIRV-Headers. Fetch those
  # exact revisions rather than using whatever shader compiler the host has.
  if [[ ! -d "$source_root/glslang/External/spirv-tools/.git" ]]; then
    (
      cd "$source_root/glslang"
      python3 ./update_glslang_sources.py --dir .
    )
  fi
  cmake_build glslang "$source_root/glslang" "$deps" \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_EXTERNAL=ON \
    -DGLSLANG_TESTS=OFF -DGLSLANG_ENABLE_INSTALL=ON \
    -DENABLE_GLSLANG_BINARIES=OFF -DENABLE_HLSL=ON \
    -DENABLE_OPT=ON -DENABLE_SPVREMAPPER=ON -DENABLE_PCH=OFF
  # SPIRV-Tools always emits a shared companion even when its static target is
  # selected. It is not part of the CanX closure and must not leak into it.
  find "$deps/lib" -maxdepth 1 \( -type f -o -type l \) \
    -name 'libSPIRV-Tools-shared.so*' -delete

  PKG_CONFIG_PATH="$pkg_config_path" meson_build libdisplay-info \
    "$source_root/libdisplay-info" "$deps"

  PKG_CONFIG_PATH="$pkg_config_path" meson_build dav1d "$source_root/dav1d" "$deps" \
    -Denable_tools=false -Denable_tests=false -Denable_examples=false

  if [[ ! -f "$deps/lib/libvpx.a" ]]; then
    rm -rf "$build_root/libvpx"
    mkdir -p "$build_root/libvpx"
    (
      cd "$build_root/libvpx"
      PATH="$deps/bin:$PATH" "$source_root/libvpx/configure" \
        --prefix="$deps" --libdir="$deps/lib" \
        --disable-shared --enable-static --enable-pic \
        --disable-examples --disable-tools --disable-docs \
        --disable-unit-tests --enable-vp9-highbitdepth --enable-vp9-postproc
      make -j "$jobs"
      make install
    )
  fi

  cmake_build opus "$source_root/opus" "$deps" \
    -DOPUS_BUILD_SHARED_LIBRARY=OFF -DOPUS_BUILD_PROGRAMS=OFF \
    -DOPUS_BUILD_TESTING=OFF -DOPUS_INSTALL_PKG_CONFIG_MODULE=ON
  cmake_build ogg "$source_root/ogg" "$deps" \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF -DINSTALL_DOCS=OFF
  PKG_CONFIG_PATH="$pkg_config_path" cmake_build vorbis "$source_root/vorbis" "$deps" \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DOGG_INCLUDE_DIR="$deps/include" -DOGG_LIBRARY="$deps/lib/libogg.a"
  PATH="$deps/bin:$PATH" cmake_build libjpeg-turbo "$source_root/libjpeg-turbo" "$deps" \
    -DENABLE_SHARED=OFF -DENABLE_STATIC=ON -DWITH_TURBOJPEG=OFF \
    -DWITH_TOOLS=OFF -DWITH_TESTS=OFF
  cmake_build openjpeg "$source_root/openjpeg" "$deps" \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_CODEC=OFF -DBUILD_DOC=OFF \
    -DBUILD_TESTING=OFF

  if [[ ! -f "$deps/lib/libzimg.a" ]]; then
    (
      cd "$source_root/zimg"
      ./autogen.sh
    )
    rm -rf "$build_root/zimg"
    mkdir -p "$build_root/zimg"
    (
      cd "$build_root/zimg"
      CXXFLAGS='-O2 -fPIC' "$source_root/zimg/configure" \
        --prefix="$deps" --libdir="$deps/lib" --enable-static --disable-shared
      make -j "$jobs"
      make install
    )
  fi

  PKG_CONFIG_PATH="$pkg_config_path" meson_build lcms2 "$source_root/lcms2" "$deps" \
    -Dtests=disabled -Djpeg=enabled -Dtiff=disabled -Dutils=false \
    -Dfastfloat=false -Dthreaded=false
  cmake_build uchardet "$source_root/uchardet" "$deps" \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_BINARY=OFF

  if [[ ! -f "$deps/lib/libmujs.a" ]]; then
    make -C "$source_root/mujs" -j "$jobs" \
      CFLAGS='-O2 -fPIC' HAVE_READLINE=no \
      build/release/libmujs.a build/release/mujs.pc
    install -m 0644 "$source_root/mujs/build/release/libmujs.a" "$deps/lib/libmujs.a"
    install -m 0644 "$source_root/mujs/mujs.h" "$deps/include/mujs.h"
  fi
  mkdir -p "$deps/lib/pkgconfig"
  printf '%s\n' \
    "prefix=$deps" \
    'libdir=${prefix}/lib' \
    'includedir=${prefix}/include' \
    '' \
    'Name: mujs' \
    'Description: MuJS embeddable JavaScript interpreter' \
    'Version: 1.3.7' \
    'Libs: -L${libdir} -lmujs' \
    'Libs.private: -lm' \
    'Cflags: -I${includedir}' > "$deps/lib/pkgconfig/mujs.pc"

  if [[ ! -f "$deps/lib/liblua.a" ]]; then
    local lua_build="$build_root/lua-5.2.4"
    local lua_sources=(
      lapi lcode lctype ldebug ldo ldump lfunc lgc llex lmem lobject
      lopcodes lparser lstate lstring ltable ltm lundump lvm lzio
      lauxlib lbaselib lbitlib lcorolib ldblib liolib lmathlib loslib
      lstrlib ltablib loadlib linit
    )
    local lua_objects=()
    mkdir -p "$lua_build"
    for lua_source in "${lua_sources[@]}"; do
      cc -O2 -fPIC -DLUA_USE_LINUX -I"$source_root/lua" \
        -c "$source_root/lua/$lua_source.c" -o "$lua_build/$lua_source.o"
      lua_objects+=("$lua_build/$lua_source.o")
    done
    ar rcs "$deps/lib/liblua.a" "${lua_objects[@]}"
    install -m 0644 "$source_root/lua"/{lua.h,luaconf.h,lualib.h,lauxlib.h} "$deps/include/"
  fi
  mkdir -p "$deps/lib/pkgconfig"
  printf '%s\n' \
    "prefix=$deps" \
    'libdir=${prefix}/lib' \
    'includedir=${prefix}/include' \
    '' \
    'Name: Lua' \
    'Description: Lua language engine' \
    'Version: 5.2.4' \
    'Libs: -L${libdir} -llua' \
    'Libs.private: -ldl -lm' \
    'Cflags: -I${includedir}' > "$deps/lib/pkgconfig/lua.pc"

  (
    cd "$source_root/alsa-lib"
    autoreconf -fi
  )
  rm -rf "$build_root/alsa-lib"
  mkdir -p "$build_root/alsa-lib"
  (
    cd "$build_root/alsa-lib"
    CFLAGS='-O2 -fPIC' "$source_root/alsa-lib/configure" \
      --prefix="$deps" --libdir="$deps/lib" \
      --enable-static --disable-shared --disable-python
    make -j "$jobs"
    make install
  )

  build_expat expat-mpv "$deps"
  meson_build freetype "$source_root/freetype" "$deps" \
    -Dauto_features=disabled -Dzlib=internal -Dharfbuzz=disabled \
    -Dbrotli=disabled -Dbzip2=disabled -Dpng=disabled -Dtests=disabled
  rm -rf "$build_root/harfbuzz"
  PKG_CONFIG_PATH="$pkg_config_path" meson_build harfbuzz "$source_root/harfbuzz" "$deps" \
    -Dauto_features=disabled -Dglib=disabled -Dgobject=disabled \
    -Dcairo=disabled -Dchafa=disabled \
    -Dicu=disabled -Dfreetype=enabled \
    -Dtests=disabled -Dintrospection=disabled -Ddocs=disabled \
    -Dutilities=disabled

  # Avalonia's statically linked HarfBuzzSharp already contains the HarfBuzz
  # 8.3.1 core but intentionally omits its FreeType adapter. Keep a tiny
  # archive containing only that adapter for the monolithic NativeAOT link.
  # The standalone libmpv sidecar continues to use the complete libharfbuzz.a.
  local harfbuzz_bridge_build="$build_root/harfbuzz-ft-bridge"
  rm -rf "$harfbuzz_bridge_build"
  mkdir -p "$harfbuzz_bridge_build"
  (
    cd "$harfbuzz_bridge_build"
    ar x "$deps/lib/libharfbuzz.a" hb-ft.cc.o
    ar rcs "$deps/lib/libharfbuzz-ft.a" hb-ft.cc.o
  )
  meson_build fribidi "$source_root/fribidi" "$deps" \
    -Ddocs=false -Dbin=false -Dtests=false

  if [[ ! -e "$source_root/fontconfig/subprojects/gperf" ]]; then
    ln -s ../../gperf "$source_root/fontconfig/subprojects/gperf"
  fi
  PKG_CONFIG_PATH="$pkg_config_path" meson_build fontconfig "$source_root/fontconfig" "$deps" \
    -Dauto_features=disabled -Ddoc=disabled -Ddoc-txt=disabled \
    -Ddoc-man=disabled -Ddoc-pdf=disabled -Ddoc-html=disabled \
    -Dnls=disabled -Dtests=disabled -Dtools=disabled \
    -Dcache-build=disabled -Dxml-backend=expat

  if [[ ! -f "$deps/lib/libunibreak.a" ]]; then
    (
      cd "$source_root/libunibreak"
      autoreconf -fi
    )
    mkdir -p "$build_root/libunibreak"
    (
      cd "$build_root/libunibreak"
      CFLAGS='-O2 -fPIC' "$source_root/libunibreak/configure" \
        --prefix="$deps" --libdir="$deps/lib" --enable-static --disable-shared
      make -j "$jobs"
      make install
    )
  fi

  PKG_CONFIG_PATH="$pkg_config_path" meson_build libass "$source_root/libass" "$deps" \
    -Dauto_features=disabled -Dfontconfig=enabled -Dlibunibreak=enabled \
    -Ddirectwrite=disabled -Dcoretext=disabled -Dasm=enabled \
    -Dtest=disabled -Dcompare=disabled -Dprofile=disabled \
    -Dfuzz=disabled -Dcheckasm=disabled
  # libplacebo's glslang probe uses compiler/library searches instead of
  # pkg-config, so expose the pinned prefix explicitly. Recreate the build
  # directory to prevent stale negative feature probes from being cached.
  rm -rf "$build_root/libplacebo"
  CFLAGS="-I$deps/include" \
  CXXFLAGS="-I$deps/include" \
  LDFLAGS="-L$deps/lib" \
  PKG_CONFIG_PATH="$pkg_config_path" \
    meson_build libplacebo "$source_root/libplacebo" "$deps" \
    -Dauto_features=disabled -Ddemos=false -Dtests=false -Dbench=false \
    -Dfuzz=false -Dopengl=enabled -Dgl-proc-addr=enabled -Dlcms=enabled \
    -Dvulkan=enabled -Dvk-proc-addr=enabled -Dglslang=enabled \
    -Dshaderc=disabled -Dvulkan-sdk="$deps"
  # libplacebo 7's generated metadata is incomplete for this fully static
  # glslang build: it omits two archives, emits absolute build-prefix paths,
  # orders cyclic glslang/SPIRV dependencies incorrectly, and omits the C++
  # runtime. Publish a relocatable, grouped static link closure instead.
  sed -i \
    's|^Libs:.*|Libs: -L${libdir} -Wl,--start-group -lplacebo -lglslang-default-resource-limits -lglslang -lMachineIndependent -lOSDependent -lGenericCodeGen -lSPVRemapper -lSPIRV -lSPIRV-Tools-opt -lSPIRV-Tools -Wl,--end-group -lstdc++ -lm -pthread -ldl|' \
    "$deps/lib/pkgconfig/libplacebo.pc"

  PKG_CONFIG_PATH="$pkg_config_path" cmake_build libarchive "$source_root/libarchive" "$deps" \
    -DBUILD_SHARED_LIBS=OFF -DENABLE_TEST=OFF \
    -DENABLE_INSTALL=ON -DENABLE_OPENSSL=ON -DENABLE_ZLIB=ON \
    -DENABLE_BZip2=OFF -DENABLE_LZMA=OFF -DENABLE_LZ4=OFF \
    -DENABLE_ZSTD=OFF -DENABLE_LIBXML2=OFF -DENABLE_EXPAT=OFF \
    -DENABLE_PCREPOSIX=OFF -DENABLE_CNG=OFF \
    -DOPENSSL_ROOT_DIR="$deps" -DZLIB_ROOT="$deps"

  mkdir -p "$build_root/ffmpeg"
  (
    cd "$build_root/ffmpeg"
    PATH="$deps/bin:$PATH" \
    PKG_CONFIG_PATH="$pkg_config_path" \
    "$source_root/ffmpeg/configure" \
      --prefix="$deps" --libdir="$deps/lib" \
      --disable-shared --enable-static --enable-pic \
      --disable-programs --disable-doc --disable-debug \
      --disable-autodetect --enable-x86asm --enable-pthreads \
      --enable-gpl --enable-version3 \
      --enable-ffnvcodec --enable-nvdec --enable-cuvid --enable-nvenc \
      --enable-amf --enable-libvpl \
      --enable-libdrm --enable-vaapi --disable-vdpau \
      --enable-vulkan --enable-opencl --enable-libglslang \
      --enable-libplacebo \
      --enable-openssl --enable-zlib \
      --enable-libdav1d --enable-libvpx --enable-libopus \
      --enable-libvorbis --enable-libopenjpeg \
      --enable-libass --enable-libfontconfig --enable-libfreetype \
      --enable-libfribidi --enable-libharfbuzz --enable-libzimg \
      --enable-lcms2 --enable-alsa \
      --pkg-config-flags=--static \
      --extra-cflags="-I$deps/include" \
      --extra-ldflags="-L$deps/lib" \
      --extra-libs='-lpthread -ldl -lm'
    make -j "$jobs"
    make install
  )

  # These are distribution capabilities, not probes of the build machine's
  # current GPU. Fail the build if an upstream/configuration change silently
  # drops any vendor-neutral Linux x64 acceleration path.
  assert_config_enabled "$build_root/ffmpeg/config.h" \
    CONFIG_AMF CONFIG_FFNVCODEC CONFIG_NVDEC CONFIG_CUVID CONFIG_NVENC \
    CONFIG_LIBVPL CONFIG_QSV CONFIG_LIBDRM CONFIG_VAAPI \
    CONFIG_VULKAN CONFIG_OPENCL CONFIG_LIBGLSLANG CONFIG_LIBPLACEBO
  assert_config_enabled "$build_root/ffmpeg/config_components.h" \
    CONFIG_H264_AMF_DECODER CONFIG_HEVC_AMF_DECODER \
    CONFIG_H264_QSV_DECODER CONFIG_HEVC_QSV_DECODER \
    CONFIG_H264_VAAPI_HWACCEL CONFIG_HEVC_VAAPI_HWACCEL \
    CONFIG_H264_VULKAN_HWACCEL CONFIG_HEVC_VULKAN_HWACCEL

  PKG_CONFIG_PATH="$pkg_config_path" \
  PYTHONPATH="$meson_pythonpath${PYTHONPATH:+:$PYTHONPATH}" \
  CANX_INSTALL_PREFIX="$install" \
  CANX_BUILD_DIR="$build_root/mpv" \
  CANX_MESON="$meson_executable" \
  CANX_BUILD_JOBS="$jobs" \
    "$source_root/mpv/TOOLS/canx-static-libmpv" \
      --libdir=lib -Db_staticpic=true \
      -Dalsa=enabled \
      -Dcuda-hwaccel=enabled -Dcuda-interop=enabled \
      -Ddrm=enabled -Degl=enabled \
      -Dvaapi=enabled -Dvaapi-drm=enabled -Dvulkan=enabled \
      -Dgbm=disabled -Dvaapi-wayland=disabled -Dvaapi-x11=disabled \
      -Dvdpau=disabled -Dx11=disabled -Dwayland=disabled \
      -Ddvbin=enabled -Diconv=enabled -Djavascript=enabled -Djpeg=enabled \
      -Dlcms2=enabled -Dlibarchive=enabled -Dlibavdevice=enabled \
      -Dlua=lua -Doss-audio=disabled \
      -Duchardet=enabled -Dvector=enabled -Dzimg=enabled -Dzlib=enabled

  assert_config_enabled "$build_root/mpv/config.h" \
    HAVE_CUDA_HWACCEL HAVE_CUDA_INTEROP HAVE_DRM HAVE_EGL \
    HAVE_VAAPI HAVE_VAAPI_DRM HAVE_DMABUF_INTEROP_GL \
    HAVE_VULKAN HAVE_LIBPLACEBO

  for archive in \
    "$install/lib/libmpv.a" \
    "$deps/lib/libass.a" \
    "$deps/lib/libasound.a" \
    "$deps/lib/libavcodec.a" \
    "$deps/lib/libavdevice.a" \
    "$deps/lib/libavfilter.a" \
    "$deps/lib/libavformat.a" \
    "$deps/lib/libavutil.a" \
    "$deps/lib/libdisplay-info.a" \
    "$deps/lib/libdrm.a" \
    "$deps/lib/libexpat.a" \
    "$deps/lib/libfontconfig.a" \
    "$deps/lib/libfreetype.a" \
    "$deps/lib/libfribidi.a" \
    "$deps/lib/libharfbuzz-ft.a" \
    "$deps/lib/libharfbuzz.a" \
    "$deps/lib/libplacebo.a" \
    "$deps/lib/libva.a" \
    "$deps/lib/libva-drm.a" \
    "$deps/lib/libvpl.a" \
    "$deps/lib/libOpenCL.a" \
    "$deps/lib/libglslang-default-resource-limits.a" \
    "$deps/lib/libglslang.a" \
    "$deps/lib/libMachineIndependent.a" \
    "$deps/lib/libOSDependent.a" \
    "$deps/lib/libGenericCodeGen.a" \
    "$deps/lib/libSPIRV.a" \
    "$deps/lib/libSPVRemapper.a" \
    "$deps/lib/libSPIRV-Tools.a" \
    "$deps/lib/libSPIRV-Tools-opt.a" \
    "$deps/lib/libswresample.a" \
    "$deps/lib/libswscale.a" \
    "$deps/lib/libunibreak.a"; do
    assert_archive "$archive"
  done
  assert_no_shared_libraries "$deps"
  assert_no_shared_libraries "$install"
}

build_avalonia_native() {
  local mpv_deps="$prefix_root/mpv-dependencies"
  local install="$prefix_root/avalonia"
  local source_dir="$source_root/skiasharp"
  local skia_dir="$source_dir/externals/skia"
  local skia_build="$skia_dir/out/canx/linux-x64"
  local pkg_config_path="$mpv_deps/lib/pkgconfig"
  local archive
  local archives=(
    libHarfBuzzSharp.a
    libSkiaSharp.a
    libdng_sdk.a
    libexpat.a
    libfreetype2.a
    libjpeg.a
    libpiex.a
    libpng.a
    libskcms.a
    libskia.a
    libskottie.a
    libskresources.a
    libsksg.a
    libskshaper.a
    libwebp.a
    libwebp_avx2.a
    libwebp_sse41.a
    libwuffs.a
    libzlib.a
  )

  # The static fontconfig used by both mpv and SkiaSharp is built as part of
  # the mpv dependency closure. Build it on demand for an avalonia-only run.
  if [[ ! -f "$mpv_deps/lib/libfontconfig.a" ]]; then
    build_mpv
  fi

  checkout_source skiasharp
  (
    cd "$skia_dir"
    python3 tools/git-sync-deps
    PKG_CONFIG_PATH="$pkg_config_path" bin/gn gen "$skia_build" --args="
      is_official_build=true
      skia_enable_tools=false
      is_static_skiasharp=true
      use_PIC=true
      target_os=\"linux\"
      target_cpu=\"x64\"
      skia_enable_ganesh=true
      skia_use_harfbuzz=false
      skia_use_icu=false
      skia_use_piex=true
      skia_use_sfntly=false
      skia_use_system_expat=false
      skia_use_system_freetype2=false
      skia_use_system_libjpeg_turbo=false
      skia_use_system_libpng=false
      skia_use_system_libwebp=false
      skia_use_system_zlib=false
      skia_enable_skottie=true
      skia_use_vulkan=true
      extra_asmflags=[]
      extra_cflags=[\"-DSKIA_C_DLL\",\"-DHAVE_SYSCALL_GETRANDOM\",\"-DXML_DEV_URANDOM\"]
      cc=\"clang\"
      cxx=\"clang++\"
      ar=\"ar\"
    "
    "$source_dir/externals/depot_tools/ninja" \
      -C "$skia_build" -j "$jobs" SkiaSharp HarfBuzzSharp
  )

  mkdir -p "$install/lib"
  for archive in "${archives[@]}"; do
    assert_archive "$skia_build/$archive"
    install -m 0644 "$skia_build/$archive" "$install/lib/$archive"
  done
  assert_no_shared_libraries "$install"
}

if [[ "$component" == all || "$component" == aria2 ]]; then
  build_aria2
fi
if [[ "$component" == all || "$component" == mpv ]]; then
  build_mpv
fi
if [[ "$component" == all || "$component" == avalonia ]]; then
  build_avalonia_native
fi

echo "CANX_STATIC_NATIVE_BUILD_OK platform=linux-x64 component=$component root=$static_root"
