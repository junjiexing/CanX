#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repository_root/scripts/support/linux-architecture.sh"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native-ubuntu22.04/$linux_runtime}"
prefix_root="$static_root/prefix"
include_smoke_tests="${CANX_INCLUDE_SMOKE_TESTS:-false}"
case "$include_smoke_tests" in
  true)
    default_output_dir="$repository_root/artifacts/verify/$linux_runtime"
    default_symbols_dir="$repository_root/artifacts/verify/$linux_runtime-symbols"
    ;;
  false)
    default_output_dir="$repository_root/artifacts/publish/$linux_runtime"
    default_symbols_dir="$repository_root/artifacts/symbols/$linux_runtime"
    ;;
  *) echo "CANX_INCLUDE_SMOKE_TESTS must be true or false" >&2; exit 2 ;;
esac
output_dir="${CANX_APP_OUTPUT:-$default_output_dir}"
symbols_dir="${CANX_APP_SYMBOLS_OUTPUT:-$default_symbols_dir}"
skip_build=false

if [[ "${1:-}" == "--skip-build" ]]; then
  skip_build=true
elif [[ $# -ne 0 ]]; then
  echo "usage: $0 [--skip-build]" >&2
  exit 2
fi

if [[ ! -f "$prefix_root/mpv/lib/libmpv.a" ]]; then
  if [[ "$skip_build" == true ]]; then
    echo "prebuilt native dependencies are missing: $prefix_root" >&2
    echo 'run scripts/fetch-native-deps.sh linux to download them' >&2
    exit 2
  fi
  "$repository_root/scripts/fetch-native-deps.sh" linux
fi

join_by_pipe() {
  local IFS='|'
  printf '%s' "$*"
}

gcc_archive() {
  local name="$1"
  local archive
  archive="$(gcc -print-file-name="$name")"
  [[ "$archive" = /* && -f "$archive" ]] || {
    echo "GCC static runtime archive not found: $name" >&2
    exit 2
  }
  printf '%s' "$archive"
}

for tool in dotnet gcc readelf; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing build tool: $tool" >&2
    exit 2
  }
done

aria_deps="$prefix_root/aria2-dependencies/lib"
aria_install="$prefix_root/aria2"
avalonia_native="$prefix_root/avalonia/lib"
mpv_deps="$prefix_root/mpv-dependencies/lib"
mpv_install="$prefix_root/mpv/lib"
shims="$static_root/shims"
[[ -f "$shims/libcanx_aria2.a" && -f "$shims/libcanx_quickjs.a" ]] || {
  echo "prebuilt shim archives are missing: $shims" >&2
  echo 'run scripts/fetch-native-deps.sh linux to download them' >&2
  exit 2
}

libstdcpp="$(gcc_archive libstdc++.a)"
libgcc_eh="$(gcc_archive libgcc_eh.a)"
libatomic="$(gcc_archive libatomic.a)"
link_group_start='-Wl%2C--start-group'
link_group_end='-Wl%2C--end-group'
hide_archive_symbols='-Wl%2C--exclude-libs%2CALL'
libraries=(
  "$link_group_start"
  "$shims/libcanx_aria2.a"
  "$shims/libcanx_quickjs.a"
  "$aria_install/lib/libaria2.a"
  "$aria_deps/libssh2.a"
  "$aria_deps/libcares.a"
  "$aria_deps/libsqlite3.a"
  "$aria_deps/libssl.a"
  "$aria_deps/libcrypto.a"
  "$aria_deps/libz.a"
  "$avalonia_native/libSkiaSharp.a"
  "$avalonia_native/libskia.a"
  "$avalonia_native/libskottie.a"
  "$avalonia_native/libsksg.a"
  "$avalonia_native/libskresources.a"
  "$avalonia_native/libskshaper.a"
  "$avalonia_native/libdng_sdk.a"
  "$avalonia_native/libpiex.a"
  "$avalonia_native/libjpeg.a"
  "$avalonia_native/libpng.a"
  "$avalonia_native/libwebp.a"
  "$avalonia_native/libwuffs.a"
  "$avalonia_native/libskcms.a"
  "$avalonia_native/libfreetype2.a"
  "$avalonia_native/libexpat.a"
  "$avalonia_native/libzlib.a"
  "$avalonia_native/libHarfBuzzSharp.a"
  "$mpv_deps/libharfbuzz-ft.a"
  "$mpv_install/libmpv.a"
  "$mpv_deps/libarchive.a"
  "$mpv_deps/libasound.a"
  "$mpv_deps/libass.a"
  "$mpv_deps/libfribidi.a"
  # HarfBuzzSharp already embeds the complete HarfBuzz implementation. Reuse
  # it for libass/FFmpeg in the monolithic NativeAOT image to avoid duplicate
  # public HarfBuzz symbols. The standalone development libmpv sidecar still
  # links the dedicated libharfbuzz.a because it does not contain Avalonia.
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
  "$libatomic"
  "$libstdcpp"
  "$libgcc_eh"
)
for archive in "$avalonia_native"/libwebp_*.a; do
  [[ ! -f "$archive" ]] || libraries+=("$archive")
done
if [[ "$linux_architecture" == x64 ]]; then
  libraries+=("$mpv_deps/libvpl.a")
fi
libraries+=("$link_group_end" "$hide_archive_symbols")

dotnet publish "$repository_root/src/CanX.App/CanX.App.csproj" \
  --configuration Release \
  --runtime "$linux_runtime" \
  --self-contained \
  --output "$output_dir" \
  -p:CanXProductionPublish=true \
  "-p:CanXIncludeSmokeTests=$include_smoke_tests" \
  '-p:CanXDirectPInvokeLibraries=canx_aria2|mpv|canx_quickjs|libSkiaSharp|libHarfBuzzSharp' \
  "-p:CanXStaticLibraries=$(join_by_pipe "${libraries[@]}")" \
  '-p:CanXNativeSystemLibraries=EGL|:libvulkan.so.1' \
  "-p:CanXStaticCxxRuntime=$libstdcpp" \
  '-p:CanXStaticNativeSidecars=libcanx_aria2.so|libmpv.so|libcanx_quickjs.so|libSkiaSharp.so|libHarfBuzzSharp.so' \
  -p:LinkStandardCPlusPlusLibrary=true

machine_pattern='Advanced Micro Devices X86-64'
[[ "$linux_architecture" != arm64 ]] || machine_pattern=AArch64
readelf -h "$output_dir/CanX.App" | grep -q "$machine_pattern" || {
  echo "published executable is not $linux_runtime" >&2; exit 1;
}

"$repository_root/scripts/audit-native-dependencies-linux.sh" "$output_dir/CanX.App" \
  --allow-needed libEGL.so.1 --allow-needed libvulkan.so.1
if [[ "${CANX_ALLOW_NEWER_GLIBC:-0}" == 1 ]]; then
  echo 'WARNING: Ubuntu 22.04 GLIBC baseline check was explicitly bypassed.' >&2
else
  "$repository_root/scripts/verify-linux-glibc-baseline.sh" "$output_dir/CanX.App" 2.35
fi
mkdir -p "$symbols_dir"
find "$symbols_dir" -maxdepth 1 -type f \( -name '*.dbg' -o -name '*.pdb' \) -delete
find "$output_dir" -maxdepth 1 -type f \( -name '*.dbg' -o -name '*.pdb' \) \
  -exec mv -f {} "$symbols_dir/" \;
[[ -f "$symbols_dir/CanX.App.dbg" || -f "$symbols_dir/CanX.App.pdb" ]] || {
  echo "Linux application debug symbols were not produced: $symbols_dir" >&2
  exit 1
}
if [[ "$include_smoke_tests" == true ]]; then
  touch "$output_dir/CanX.smoke-build"
else
  rm -f "$output_dir/CanX.smoke-build"
fi
printf 'CANX_STATIC_APP_PUBLISH_OK platform=%s output=%s symbols=%s smoke=%s\n' \
  "$linux_runtime" "$output_dir" "$symbols_dir" "$include_smoke_tests"
