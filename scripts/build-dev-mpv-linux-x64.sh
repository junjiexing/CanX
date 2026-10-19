#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native/linux-x64}"
prefix_root="$static_root/prefix"
mpv_install="$prefix_root/mpv/lib"
mpv_deps="$prefix_root/mpv-dependencies/lib"
output_dir="${CANX_DEV_MPV_OUTPUT:-$repository_root/artifacts/native/mpv-dev-linux-x64}"
sidecar="$output_dir/libmpv.so"

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
  echo "the development mpv sidecar currently supports only Linux x64" >&2
  exit 2
fi

for tool in cc gcc ldd nm; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing development native build tool: $tool" >&2
    exit 2
  }
done

libstdcpp="$(gcc -print-file-name=libstdc++.a)"
libgcc_eh="$(gcc -print-file-name=libgcc_eh.a)"

archives=(
  "$mpv_install/libmpv.a"
  "$mpv_deps/libarchive.a"
  "$mpv_deps/libasound.a"
  "$mpv_deps/libass.a"
  "$mpv_deps/libfribidi.a"
  "$mpv_deps/libharfbuzz.a"
  "$mpv_deps/libunibreak.a"
  "$mpv_deps/libfontconfig.a"
  "$mpv_deps/libfreetype.a"
  "$mpv_deps/libexpat.a"
  "$mpv_deps/libavfilter.a"
  "$mpv_deps/libavformat.a"
  "$mpv_deps/libavcodec.a"
  "$mpv_deps/libavdevice.a"
  "$mpv_deps/libplacebo.a"
  "$mpv_deps/libglslang-default-resource-limits.a"
  "$mpv_deps/libglslang.a"
  "$mpv_deps/libMachineIndependent.a"
  "$mpv_deps/libOSDependent.a"
  "$mpv_deps/libGenericCodeGen.a"
  "$mpv_deps/libSPIRV.a"
  "$mpv_deps/libSPVRemapper.a"
  "$mpv_deps/libSPIRV-Tools-opt.a"
  "$mpv_deps/libSPIRV-Tools.a"
  "$mpv_deps/libOpenCL.a"
  "$mpv_deps/libvpl.a"
  "$mpv_deps/libva-drm.a"
  "$mpv_deps/libva.a"
  "$mpv_deps/libdisplay-info.a"
  "$mpv_deps/libdrm.a"
  "$mpv_deps/libswresample.a"
  "$mpv_deps/libswscale.a"
  "$mpv_deps/libavutil.a"
  "$mpv_deps/libdav1d.a"
  "$mpv_deps/libjpeg.a"
  "$mpv_deps/liblcms2.a"
  "$mpv_deps/liblua.a"
  "$mpv_deps/libmujs.a"
  "$mpv_deps/libogg.a"
  "$mpv_deps/libopenjp2.a"
  "$mpv_deps/libopus.a"
  "$mpv_deps/libssl.a"
  "$mpv_deps/libcrypto.a"
  "$mpv_deps/libuchardet.a"
  "$mpv_deps/libvorbis.a"
  "$mpv_deps/libvorbisenc.a"
  "$mpv_deps/libvpx.a"
  "$mpv_deps/libz.a"
  "$mpv_deps/libzimg.a"
)

validate_sidecar() {
  local dependency_report
  [[ -f "$sidecar" ]] || {
    echo "development mpv sidecar was not produced: $sidecar" >&2
    exit 1
  }
  nm -D "$sidecar" | grep ' T mpv_create$' >/dev/null || {
    echo "development mpv sidecar does not export mpv_create" >&2
    exit 1
  }
  if ! dependency_report="$(ldd -r "$sidecar" 2>&1)"; then
    printf '%s\n' "$dependency_report" >&2
    echo "could not inspect development mpv sidecar dependencies" >&2
    exit 1
  fi
  if grep -qE 'not found|undefined symbol:' <<<"$dependency_report"; then
    printf '%s\n' "$dependency_report" >&2
    echo "development mpv sidecar contains unresolved dependencies" >&2
    exit 1
  fi
}

newer_archive=""
if [[ -f "$sidecar" ]]; then
  for archive in "${archives[@]}"; do
    if [[ -f "$archive" && "$archive" -nt "$sidecar" ]]; then
      newer_archive="$archive"
      break
    fi
  done
fi
if [[ -f "$sidecar" && -z "$newer_archive" && ! "$0" -nt "$sidecar" && \
      ! "$repository_root/native/sources-linux-x64.lock" -nt "$sidecar" ]]; then
  validate_sidecar
  printf 'CANX_DEV_MPV_SIDECAR_OK platform=linux-x64 output=%s cached=true\n' "$sidecar"
  exit 0
fi

for archive in "${archives[@]}"; do
  if [[ ! -f "$archive" ]]; then
    echo "Building the pinned mpv dependency closure for the development sidecar..."
    CANX_STATIC_ROOT="$static_root" \
      "$repository_root/scripts/build-static-native-linux-x64.sh" mpv
    break
  fi
done

for archive in "${archives[@]}"; do
  [[ -f "$archive" ]] || {
    echo "required development archive was not produced: $archive" >&2
    exit 1
  }
done

mkdir -p "$output_dir"
cc -shared -o "$sidecar" \
  -Wl,-soname,libmpv.so \
  -Wl,-Bsymbolic \
  -Wl,-z,defs \
  -Wl,--whole-archive "${archives[0]}" -Wl,--no-whole-archive \
  -Wl,--start-group "${archives[@]:1}" "$libstdcpp" "$libgcc_eh" -Wl,--end-group \
  -latomic -pthread -ldl -lrt -lm -lEGL -l:libvulkan.so.1

validate_sidecar
printf 'CANX_DEV_MPV_SIDECAR_OK platform=linux-x64 output=%s\n' "$sidecar"
