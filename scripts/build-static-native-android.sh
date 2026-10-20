#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
android_sdk="${ANDROID_SDK_ROOT:-$repository_root/.cache/android-sdk}"
android_ndk_version="${CANX_ANDROID_NDK_VERSION:-28.2.13676358}"
default_android_ndk="$android_sdk/ndk/$android_ndk_version"
[[ -d "$default_android_ndk" ]] || default_android_ndk="$android_sdk/ndk-bundle"
android_ndk="${ANDROID_NDK_ROOT:-$default_android_ndk}"
api="${CANX_ANDROID_API:-23}"
jobs="${CANX_BUILD_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '4')}"
abi="${1:-arm64-v8a}"
component="${2:-all}"

case "$abi" in
  arm64-v8a)
    host="aarch64-linux-android"
    ffmpeg_arch="aarch64"
    openssl_target="android-arm64"
    meson_cpu_family="aarch64"
    meson_cpu="aarch64"
    ;;
  x86_64)
    host="x86_64-linux-android"
    ffmpeg_arch="x86_64"
    openssl_target="android-x86_64"
    meson_cpu_family="x86_64"
    meson_cpu="x86_64"
    ;;
  *)
    echo "usage: $0 [arm64-v8a|x86_64] [all|quickjs|aria2|mpv]" >&2
    exit 2
    ;;
esac

case "$component" in all|quickjs|aria2|mpv) ;; *)
  echo "usage: $0 [arm64-v8a|x86_64] [all|quickjs|aria2|mpv]" >&2
  exit 2
esac

case "$(uname -s)" in
  Linux) ndk_host="linux-x86_64" ;;
  Darwin) ndk_host="darwin-x86_64" ;;
  *) echo "unsupported Android build host: $(uname -s)" >&2; exit 2 ;;
esac
toolchain="$android_ndk/toolchains/llvm/prebuilt/$ndk_host"
bin="$toolchain/bin"
sysroot="$toolchain/sysroot"
cc="$bin/${host}${api}-clang"
cxx="$bin/${host}${api}-clang++"
ar="$bin/llvm-ar"
ranlib="$bin/llvm-ranlib"
strip="$bin/llvm-strip"
root="${CANX_ANDROID_NATIVE_ROOT:-$repository_root/.cache/android-native/$abi}"
build="$root/build"
prefix="$root/prefix"
source="$repository_root/.cache/upstream"
quickjs_source="$repository_root/.cache/native/quickjs/source"
meson_pythonpath="$repository_root/.cache/python-tools"
meson=(python3 -m mesonbuild.mesonmain)

for tool in cmake ninja make perl python3 pkg-config autoreconf; do
  command -v "$tool" >/dev/null || { echo "missing build tool: $tool" >&2; exit 2; }
done
for tool in "$cc" "$cxx" "$ar" "$ranlib" "$strip"; do
  [[ -x "$tool" ]] || { echo "missing Android NDK tool: $tool" >&2; exit 2; }
done
[[ -d "$meson_pythonpath/mesonbuild" ]] || {
  echo "Meson 1.9.1 is required under $meson_pythonpath (python3 -m pip install --target '$meson_pythonpath' meson==1.9.1)." >&2
  exit 2
}

mkdir -p "$build" "$prefix/lib/pkgconfig" "$prefix/include"
export PATH="$bin:$PATH"
export ANDROID_NDK_ROOT="$android_ndk"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
unset PKG_CONFIG_SYSROOT_DIR
export CC="$cc" CXX="$cxx" AR="$ar" RANLIB="$ranlib" STRIP="$strip"

run_meson() {
  PYTHONPATH="$meson_pythonpath${PYTHONPATH:+:$PYTHONPATH}" "${meson[@]}" "$@"
}

lock_file="$repository_root/native/sources-linux-x64.lock"

require_source() {
  [[ -d "$source/$1" ]] || {
    echo "missing pinned source checkout: $source/$1; run scripts/build-static-native-linux-x64.sh once to populate sources" >&2
    exit 2
  }
  # The Android build reuses the Linux checkouts; refuse to build from anything other than the pinned
  # revision so released APKs always match the published corresponding source.
  local expected actual
  expected="$(awk -F '|' -v name="$1" '$1 == name { print $3; found=1; exit } END { if (!found) exit 1 }' "$lock_file")" || {
    echo "source $1 is not pinned in $lock_file" >&2
    exit 2
  }
  actual="$(git -C "$source/$1" rev-parse HEAD 2>/dev/null || true)"
  if [[ "$actual" != "$expected" ]]; then
    echo "source revision mismatch: $1 is at ${actual:-<not a git checkout>}, expected $expected" >&2
    exit 2
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

cmake_build() {
  local name="$1" source_dir="$2"
  shift 2
  cmake -S "$source_dir" -B "$build/$name" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$android_ndk/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$api" \
    -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON "$@"
  cmake --build "$build/$name" --parallel "$jobs"
  cmake --install "$build/$name"
}

write_cross_file() {
  local path="$1"
  {
    printf '%s\n' '[binaries]'
    printf "c = '%s'\n" "$cc"
    printf "cpp = '%s'\n" "$cxx"
    printf "ar = '%s'\n" "$ar"
    printf "strip = '%s'\n" "$strip"
    printf "pkg-config = '%s'\n" "$(command -v pkg-config)"
    printf '%s\n' '' '[host_machine]' "system = 'android'" "cpu_family = '$meson_cpu_family'" "cpu = '$meson_cpu'" "endian = 'little'"
    printf '%s\n' '' '[built-in options]' "c_args = ['-fPIC', '-D__ANDROID_API__=$api']" "cpp_args = ['-fPIC', '-D__ANDROID_API__=$api']" "c_link_args = ['-L$prefix/lib']" "cpp_link_args = ['-L$prefix/lib']"
    printf '%s\n' '' '[properties]' 'needs_exe_wrapper = true'
  } > "$path"
}

meson_build() {
  local name="$1" source_dir="$2"
  shift 2
  local cross="$root/android-$abi.ini"
  write_cross_file "$cross"
  if [[ -f "$build/$name/meson-private/coredata.dat" ]]; then
    run_meson setup --reconfigure "$build/$name" "$source_dir" \
      --cross-file "$cross" --prefix "$prefix" --libdir lib \
      --buildtype release --default-library static --prefer-static -Db_staticpic=true "$@"
  else
    run_meson setup "$build/$name" "$source_dir" \
      --cross-file "$cross" --prefix "$prefix" --libdir lib \
      --buildtype release --default-library static --prefer-static -Db_staticpic=true "$@"
  fi
  run_meson compile -C "$build/$name" -j "$jobs"
  run_meson install -C "$build/$name"
}

build_zlib() {
  require_source zlib
  [[ -f "$prefix/lib/libz.a" ]] && return
  mkdir -p "$build/zlib"
  (
    cd "$build/zlib"
    CHOST="$host" CFLAGS='-O2 -fPIC' "$source/zlib/configure" \
      --static --prefix="$prefix" --libdir="$prefix/lib"
    make -j "$jobs"
    make install
  )
}

build_openssl() {
  require_source openssl
  [[ -f "$prefix/lib/libcrypto.a" ]] && return
  mkdir -p "$build/openssl-source"
  git -C "$source/openssl" archive HEAD | tar -x -C "$build/openssl-source"
  (
    cd "$build/openssl-source"
    ./Configure "$openssl_target" \
      --prefix="$prefix" --openssldir="$prefix/ssl" --libdir=lib \
      -D__ANDROID_API__="$api" -fPIC \
      no-apps no-docs no-dso no-module no-shared no-tests
    make -j "$jobs"
    make install_sw
  )
}

build_common_network_dependencies() {
  build_zlib
  build_openssl
  require_source expat
  cmake_build expat "$source/expat/expat" \
    -DEXPAT_SHARED_LIBS=OFF -DEXPAT_BUILD_DOCS=OFF \
    -DEXPAT_BUILD_EXAMPLES=OFF -DEXPAT_BUILD_FUZZERS=OFF \
    -DEXPAT_BUILD_TESTS=OFF -DEXPAT_BUILD_TOOLS=OFF
}

build_quickjs() {
  [[ -f "$quickjs_source/quickjs.h" ]] || { echo "missing pinned QuickJS source" >&2; exit 2; }
  cmake -S "$repository_root/native/quickjs-shim" -B "$build/quickjs" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$android_ndk/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$api" \
    -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release \
    -DQUICKJS_SOURCE_DIR="$quickjs_source" \
    -DCANX_QUICKJS_LIBRARY_TYPE=STATIC
  cmake --build "$build/quickjs" --parallel "$jobs"
  cp "$build/quickjs/libcanx_quickjs.a" "$prefix/lib/"
}

build_aria2() {
  for name in aria2 c-ares sqlite libssh2; do require_source "$name"; done
  apply_source_patch "$source/aria2" \
    "$repository_root/native/patches/aria2-runtime-priority-clear.patch"
  build_common_network_dependencies
  cmake_build c-ares "$source/c-ares" \
    -DCARES_STATIC=ON -DCARES_SHARED=OFF \
    -DCARES_BUILD_TOOLS=OFF -DCARES_BUILD_TESTS=OFF
  if [[ ! -f "$prefix/lib/libsqlite3.a" ]]; then
    mkdir -p "$build/sqlite"
    (
      cd "$build/sqlite"
      "$source/sqlite/configure" --host="$host" --prefix="$prefix" \
        --disable-shared --enable-static --disable-tcl --disable-readline \
        CFLAGS='-O2 -fPIC'
      make -j "$jobs"
      make install
    )
  fi
  cmake_build libssh2 "$source/libssh2" \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON \
    -DBUILD_EXAMPLES=OFF -DBUILD_TESTING=OFF -DCRYPTO_BACKEND=OpenSSL \
    -DOPENSSL_ROOT_DIR="$prefix" \
    -DOPENSSL_INCLUDE_DIR="$prefix/include" \
    -DOPENSSL_SSL_LIBRARY="$prefix/lib/libssl.a" \
    -DOPENSSL_CRYPTO_LIBRARY="$prefix/lib/libcrypto.a" \
    -DZLIB_LIBRARY="$prefix/lib/libz.a" \
    -DZLIB_INCLUDE_DIR="$prefix/include" -DCMAKE_PREFIX_PATH="$prefix"

  (cd "$source/aria2" && autoreconf -fi)
  mkdir -p "$build/aria2"
  (
    cd "$build/aria2"
    "$source/aria2/configure" --host="$host" --prefix="$prefix" \
      --enable-libaria2 --disable-aria2c --enable-static --disable-shared \
      --disable-nls --without-libuv --without-gnutls --with-openssl \
      --without-libxml2 --with-libexpat --with-sqlite3 --with-libcares \
      --with-libssh2 --with-libz \
      CPPFLAGS="-I$prefix/include" CFLAGS='-O2 -fPIC' CXXFLAGS='-O2 -fPIC' \
      LDFLAGS="-L$prefix/lib" LIBS='-llog -landroid'
    make -j "$jobs"
    make install
  )
  cmake -S "$repository_root/native/aria2-shim" -B "$build/aria2-shim" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$android_ndk/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$api" \
    -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release \
    -DARIA2_INCLUDE_DIR="$prefix/include" \
    -DARIA2_LIBRARY="$prefix/lib/libaria2.a" \
    -DARIA2_DEPENDENCIES="$prefix/lib/libcares.a;$prefix/lib/libssh2.a;$prefix/lib/libssl.a;$prefix/lib/libcrypto.a;$prefix/lib/libsqlite3.a;$prefix/lib/libexpat.a;$prefix/lib/libz.a;log;android" \
    -DCANX_ARIA2_EXTENDED_API=ON -DCANX_ARIA2_LIBRARY_TYPE=SHARED \
    -DCANX_ARIA2_BUILD_ANDROID_SMOKE=ON
  cmake --build "$build/aria2-shim" --parallel "$jobs"
  cp "$build/aria2-shim/libcanx_aria2.so" "$prefix/lib/"
  mkdir -p "$prefix/bin"
  cp "$build/aria2-shim/canx_aria2_android_smoke" "$prefix/bin/"
}

build_ffmpeg() {
  require_source ffmpeg
  build_zlib
  build_openssl
  [[ -f "$prefix/lib/libavcodec.a" ]] && return
  local architecture_option=""
  if [[ "$ffmpeg_arch" == "x86_64" ]] && ! command -v nasm >/dev/null 2>&1; then
    echo "nasm was not found; building Android x86_64 FFmpeg without hand-written x86 assembly" >&2
    architecture_option=--disable-x86asm
  fi
  mkdir -p "$build/ffmpeg"
  (
    cd "$build/ffmpeg"
    "$source/ffmpeg/configure" \
      --prefix="$prefix" --target-os=android --arch="$ffmpeg_arch" \
      --cc="$cc" --cxx="$cxx" --ar="$ar" --ranlib="$ranlib" --strip="$strip" \
      --sysroot="$sysroot" --enable-cross-compile \
      --enable-static --disable-shared --enable-pic \
      --disable-programs --disable-doc --disable-debug \
      --enable-network --enable-openssl --enable-zlib \
      --enable-jni --enable-mediacodec \
      ${architecture_option:+"$architecture_option"} \
      --extra-cflags="-I$prefix/include -D__ANDROID_API__=$api" \
      --extra-ldflags="-L$prefix/lib" \
      --pkg-config-flags=--static
    make -j "$jobs"
    make install
  )
}

build_subtitles() {
  for name in freetype fribidi harfbuzz libass libunibreak; do require_source "$name"; done
  local libass_architecture_option=""
  if [[ "$ffmpeg_arch" == "x86_64" ]] && ! command -v nasm >/dev/null 2>&1; then
    libass_architecture_option=-Dasm=disabled
  fi
  cmake_build freetype "$source/freetype" \
    -DBUILD_SHARED_LIBS=OFF -DFT_DISABLE_BROTLI=ON -DFT_DISABLE_BZIP2=ON \
    -DFT_DISABLE_HARFBUZZ=ON -DFT_DISABLE_PNG=ON \
    -DZLIB_LIBRARY="$prefix/lib/libz.a" -DZLIB_INCLUDE_DIR="$prefix/include"
  meson_build fribidi "$source/fribidi" -Dbin=false -Ddocs=false -Dtests=false
  meson_build harfbuzz "$source/harfbuzz" \
    -Dtests=disabled -Ddocs=disabled -Dutilities=disabled \
    -Dglib=disabled -Dgobject=disabled -Dcairo=disabled -Dchafa=disabled \
    -Dicu=disabled -Dgraphite2=disabled -Dfreetype=enabled
  if [[ ! -f "$prefix/lib/libunibreak.a" ]]; then
    (cd "$source/libunibreak" && autoreconf -fi)
    mkdir -p "$build/libunibreak"
    (
      cd "$build/libunibreak"
      "$source/libunibreak/configure" --host="$host" --prefix="$prefix" \
        --enable-static --disable-shared CFLAGS='-O2 -fPIC'
      make -j "$jobs"
      make install
    )
  fi
  meson_build libass "$source/libass" \
    -Dtest=disabled -Dcompare=disabled -Dprofile=disabled -Dfuzz=disabled \
    -Dfontconfig=disabled -Ddirectwrite=disabled -Dcoretext=disabled \
    -Drequire-system-font-provider=false -Dlibunibreak=enabled \
    ${libass_architecture_option:+"$libass_architecture_option"}
}

build_mpv() {
  require_source mpv
  require_source libplacebo
  build_common_network_dependencies
  build_ffmpeg
  build_subtitles
  meson_build libplacebo "$source/libplacebo" \
    -Dauto_features=disabled -Ddemos=false -Dtests=false -Dbench=false \
    -Dfuzz=false -Dopengl=enabled -Dgl-proc-addr=enabled \
    -Dvulkan=disabled -Dvk-proc-addr=disabled \
    -Dglslang=disabled -Dshaderc=disabled -Dlcms=disabled \
    -Ddovi=disabled -Dlibdovi=disabled -Dunwind=disabled -Dxxhash=disabled
  meson_build mpv "$source/mpv" \
    -Dcplayer=false -Dlibmpv=true -Dbuild-date=false -Dtests=false -Dfuzzers=false \
    -Djavascript=disabled -Dlua=disabled -Dlibarchive=disabled \
    -Dcdda=disabled -Ddvbin=disabled -Ddvda=disabled -Ddvdnav=disabled \
    -Dlibbluray=disabled -Dlibcurl=disabled -Duchardet=disabled \
    -Dzimg=disabled -Dlcms2=disabled -Djpeg=disabled \
    -Dandroid-media-ndk=enabled -Daudiotrack=enabled \
    -Daaudio=disabled -Dopensles=enabled \
    -Degl-android=enabled -Dplain-gl=enabled -Dgl=enabled \
    -Dvulkan=disabled -Dshaderc=disabled -Dspirv-cross=disabled
}

case "$component" in
  all)
    build_quickjs
    build_aria2
    build_mpv
    ;;
  quickjs) build_quickjs ;;
  aria2) build_aria2 ;;
  mpv) build_mpv ;;
esac

for archive in libcanx_quickjs.a libcanx_aria2.so libmpv.a; do
  if [[ "$component" == all && ! -f "$prefix/lib/$archive" ]]; then
    echo "expected Android archive was not produced: $prefix/lib/$archive" >&2
    exit 1
  fi
done

printf 'CANX_ANDROID_NATIVE_OK abi=%s prefix=%s component=%s\n' "$abi" "$prefix" "$component"
