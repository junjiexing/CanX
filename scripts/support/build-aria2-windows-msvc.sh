#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

# A non-login MSYS2 bash inherits the Visual Studio PATH verbatim. Make the
# POSIX build frontend available before invoking cygpath/autoreconf/make.
export PATH="/usr/bin:/bin:$PATH"

: "${CANX_ARIA2_SOURCE:?}"
: "${CANX_ARIA2_BUILD:?}"
: "${CANX_ARIA2_PREFIX:?}"
: "${CANX_VCPKG_INSTALLED:?}"
: "${CANX_WINDOWS_COMPAT_INCLUDE:?}"
: "${CANX_LLVM_BIN:?}"

source_dir="$(cygpath -u "$CANX_ARIA2_SOURCE")"
build_dir="$(cygpath -u "$CANX_ARIA2_BUILD")"
install_dir="$(cygpath -u "$CANX_ARIA2_PREFIX")"
vcpkg="$(cygpath -u "$CANX_VCPKG_INSTALLED")"
compat="$(cygpath -u "$CANX_WINDOWS_COMPAT_INCLUDE")"
llvm_bin="$(cygpath -u "$CANX_LLVM_BIN")"
jobs="${CANX_JOBS:-1}"

# Automake cannot detect clang's dependency mode through this MSYS2/MSVC
# cross-toolchain, so header changes would otherwise leave stale objects in
# the archive.  That is especially unsafe when a patched C++ class changes
# size: callers and implementations can then disagree about the ABI.
if [[ -f "$build_dir/Makefile" ]]; then
  make -C "$build_dir" clean
fi
rm -f "$install_dir/lib/aria2.lib"

# MSYS2 supplies Autoconf/Automake/Make only. clang targets the MSVC ABI and
# consumes the MSVC/UCRT headers and libraries inherited from VsDevShell.
export PATH="$llvm_bin:$PATH"
export CC=clang
export CXX=clang++
export AR=llvm-ar
export RANLIB=llvm-ranlib
export NM=llvm-nm
export STRIP=llvm-strip
export PKG_CONFIG=/usr/bin/pkg-config
export PKG_CONFIG_LIBDIR="$vcpkg/lib/pkgconfig:$vcpkg/share/pkgconfig"
export PKG_CONFIG_PATH="$PKG_CONFIG_LIBDIR"
export CPPFLAGS="-I$compat -I$vcpkg/include -D_WIN32_WINNT=0x0A00 -DNOMINMAX -D__MINGW32__=1 -D__MINGW64__=1 -Dstrcasecmp=_stricmp -Dstrncasecmp=_strnicmp -Dtzname=_tzname"
export CFLAGS="-O2 -fms-runtime-lib=static"
export CXXFLAGS="-O2 -fms-runtime-lib=static"
export LDFLAGS="-fuse-ld=lld-link -L$vcpkg/lib"
export ac_cv_header_winsock2_h=yes
export ac_cv_header_ws2tcpip_h=yes
export ac_cv_type_struct_timespec=yes

(
  cd "$source_dir"
  autoreconf -fi
)

mkdir -p "$build_dir" "$install_dir"
cd "$build_dir"

ARIA2_STATIC=yes "$source_dir/configure" \
  --build=x86_64-pc-msys \
  --host=x86_64-w64-mingw32 \
  --prefix="$install_dir" \
  --enable-libaria2 \
  --disable-aria2c \
  --enable-static \
  --disable-shared \
  --disable-nls \
  --without-libuv \
  --without-wintls \
  --without-gnutls \
  --with-openssl \
  --without-libxml2 \
  --with-libexpat \
  --with-sqlite3 \
  --with-libcares \
  --with-libssh2 \
  --with-libz \
  --without-libgmp \
  --without-libgcrypt \
  --without-libnettle

make -j"$jobs"
make install

test -f "$install_dir/lib/aria2.lib"
printf 'CANX_ARIA2_MSVC_OK output=%s\n' "$install_dir/lib/aria2.lib"
