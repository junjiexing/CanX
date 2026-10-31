#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native-ubuntu22.04/linux-x64}"
prefix_root="$static_root/prefix"
output_root="$repository_root/artifacts/spikes/static-native-aot"
skip_build=false

if [[ "${1:-}" == "--skip-build" ]]; then
  skip_build=true
elif [[ $# -ne 0 ]]; then
  echo "usage: $0 [--skip-build]" >&2
  exit 2
fi

if [[ "$skip_build" == false ]]; then
  "$repository_root/scripts/fetch-native-deps.sh" linux
fi
deps_source="$("$repository_root/scripts/fetch-deps-source.sh")"

for tool in cmake dotnet ffmpeg readelf curl python3; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing verification tool: $tool" >&2
    exit 2
  }
done

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

publish_static() {
  local project="$1"
  local output="$2"
  local direct_pinvoke="$3"
  local static_sidecars="$4"
  local system_libraries="$5"
  shift 5
  local libraries=("$@")

  dotnet publish "$project" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained \
    --output "$output" \
    "-p:CanXDirectPInvokeLibraries=$direct_pinvoke" \
    "-p:CanXStaticLibraries=$(join_by_pipe "${libraries[@]}")" \
    "-p:CanXNativeSystemLibraries=$system_libraries" \
    "-p:CanXStaticCxxRuntime=$libstdcpp" \
    "-p:CanXStaticNativeSidecars=$static_sidecars" \
    -p:LinkStandardCPlusPlusLibrary=true
}

mkdir -p "$output_root"

aria_deps="$prefix_root/aria2-dependencies/lib"
aria_install="$prefix_root/aria2"
aria_shim="$repository_root/artifacts/native/aria2-shim-static-source"
cmake -S "$deps_source/native/aria2-shim" -B "$aria_shim" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCANX_ARIA2_LIBRARY_TYPE=STATIC \
  -DCANX_ARIA2_EXTENDED_API=ON \
  -DARIA2_INCLUDE_DIR="$aria_install/include" \
  -DARIA2_LIBRARY="$aria_install/lib/libaria2.a"
cmake --build "$aria_shim"

link_group_start='-Wl%2C--start-group'
link_group_end='-Wl%2C--end-group'
hide_archive_symbols='-Wl%2C--exclude-libs%2CALL'
libstdcpp="$(gcc_archive libstdc++.a)"
libgcc_eh="$(gcc_archive libgcc_eh.a)"
libatomic="$(gcc_archive libatomic.a)"

aria_output="$output_root/aria2"
publish_static \
  "$repository_root/spikes/CanX.Spikes.Aria2Interop/CanX.Spikes.Aria2Interop.csproj" \
  "$aria_output" canx_aria2 '' '' \
  "$link_group_start" \
  "$aria_shim/libcanx_aria2.a" \
  "$aria_install/lib/libaria2.a" \
  "$aria_deps/libssh2.a" \
  "$aria_deps/libcares.a" \
  "$aria_deps/libexpat.a" \
  "$aria_deps/libsqlite3.a" \
  "$aria_deps/libssl.a" \
  "$aria_deps/libcrypto.a" \
  "$aria_deps/libz.a" \
  "$libstdcpp" "$libgcc_eh" \
  "$link_group_end" "$hide_archive_symbols"

mpv_deps="$prefix_root/mpv-dependencies/lib"
mpv_install="$prefix_root/mpv/lib"
avalonia_native="$prefix_root/avalonia/lib"
mpv_output="$output_root/mpv"
publish_static \
  "$repository_root/spikes/CanX.Spikes.MpvAvalonia/CanX.Spikes.MpvAvalonia.csproj" \
  "$mpv_output" 'mpv|libSkiaSharp|libHarfBuzzSharp' \
  'libSkiaSharp.so|libHarfBuzzSharp.so' 'EGL|:libvulkan.so.1' \
  "$link_group_start" \
  "$avalonia_native/libSkiaSharp.a" \
  "$avalonia_native/libskia.a" \
  "$avalonia_native/libskottie.a" \
  "$avalonia_native/libsksg.a" \
  "$avalonia_native/libskresources.a" \
  "$avalonia_native/libskshaper.a" \
  "$avalonia_native/libdng_sdk.a" \
  "$avalonia_native/libpiex.a" \
  "$avalonia_native/libjpeg.a" \
  "$avalonia_native/libpng.a" \
  "$avalonia_native/libwebp.a" \
  "$avalonia_native/libwebp_avx2.a" \
  "$avalonia_native/libwebp_sse41.a" \
  "$avalonia_native/libwuffs.a" \
  "$avalonia_native/libskcms.a" \
  "$avalonia_native/libfreetype2.a" \
  "$avalonia_native/libexpat.a" \
  "$avalonia_native/libzlib.a" \
  "$avalonia_native/libHarfBuzzSharp.a" \
  "$mpv_deps/libharfbuzz-ft.a" \
  "$mpv_install/libmpv.a" \
  "$mpv_deps/libarchive.a" \
  "$mpv_deps/libasound.a" \
  "$mpv_deps/libass.a" \
  "$mpv_deps/libfribidi.a" \
  "$mpv_deps/libunibreak.a" \
  "$mpv_deps/libfontconfig.a" \
  "$mpv_deps/libfreetype.a" \
  "$mpv_deps/libexpat.a" \
  "$mpv_deps/libavfilter.a" \
  "$mpv_deps/libavformat.a" \
  "$mpv_deps/libavcodec.a" \
  "$mpv_deps/libavdevice.a" \
  "$mpv_deps/libplacebo.a" \
  "$mpv_deps/libglslang-default-resource-limits.a" \
  "$mpv_deps/libglslang.a" \
  "$mpv_deps/libMachineIndependent.a" \
  "$mpv_deps/libOSDependent.a" \
  "$mpv_deps/libGenericCodeGen.a" \
  "$mpv_deps/libSPIRV.a" \
  "$mpv_deps/libSPVRemapper.a" \
  "$mpv_deps/libSPIRV-Tools-opt.a" \
  "$mpv_deps/libSPIRV-Tools.a" \
  "$mpv_deps/libOpenCL.a" \
  "$mpv_deps/libvpl.a" \
  "$mpv_deps/libva-drm.a" \
  "$mpv_deps/libva.a" \
  "$mpv_deps/libdisplay-info.a" \
  "$mpv_deps/libdrm.a" \
  "$mpv_deps/libswresample.a" \
  "$mpv_deps/libswscale.a" \
  "$mpv_deps/libavutil.a" \
  "$mpv_deps/libdav1d.a" \
  "$mpv_deps/libjpeg.a" \
  "$mpv_deps/liblcms2.a" \
  "$mpv_deps/liblua.a" \
  "$mpv_deps/libmujs.a" \
  "$mpv_deps/libogg.a" \
  "$mpv_deps/libopenjp2.a" \
  "$mpv_deps/libopus.a" \
  "$mpv_deps/libssl.a" \
  "$mpv_deps/libcrypto.a" \
  "$mpv_deps/libuchardet.a" \
  "$mpv_deps/libvorbis.a" \
  "$mpv_deps/libvorbisenc.a" \
  "$mpv_deps/libvpx.a" \
  "$mpv_deps/libz.a" \
  "$mpv_deps/libzimg.a" \
  "$libatomic" "$libstdcpp" "$libgcc_eh" \
  "$link_group_end" "$hide_archive_symbols"

quickjs_shim="$(CANX_QUICKJS_LIBRARY_TYPE=STATIC "$repository_root/scripts/prepare-quickjs-spike.sh" | tail -n 1)"
quickjs_output="$output_root/quickjs"
publish_static \
  "$repository_root/spikes/CanX.Spikes.QuickJsInterop/CanX.Spikes.QuickJsInterop.csproj" \
  "$quickjs_output" canx_quickjs '' '' \
  "$link_group_start" \
  "$quickjs_shim/libcanx_quickjs.a" \
  "$libstdcpp" "$libgcc_eh" \
  "$link_group_end" "$hide_archive_symbols"

work_dir="$(mktemp -d /tmp/canx-static-aot.XXXXXX)"
server_pid=""
cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

mkdir -p "$work_dir/source" "$work_dir/download"
dd if=/dev/urandom of="$work_dir/source/payload.bin" bs=1M count=2 status=none
port="$((20000 + RANDOM % 20000))"
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$work_dir/source" \
  >"$work_dir/http.log" 2>&1 &
server_pid="$!"
for _ in {1..50}; do
  curl -fs --output /dev/null "http://127.0.0.1:$port/payload.bin" && break
  sleep 0.1
done

aria_output_text="$(timeout 45s "$aria_output/CanX.Spikes.Aria2Interop" \
  "http://127.0.0.1:$port/payload.bin" "$work_dir/download" payload.bin)"
printf '%s\n' "$aria_output_text"
grep -q 'CANX_ARIA2_SHIM_OK' <<<"$aria_output_text"
cmp "$work_dir/source/payload.bin" "$work_dir/download/payload.bin"

ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -t 3 -c:v ffv1 "$work_dir/test-source.mkv"
mpv_output_text="$(timeout 30s "$mpv_output/CanX.Spikes.MpvAvalonia" \
  --smoke "$work_dir/test-source.mkv")"
printf '%s\n' "$mpv_output_text"
grep -q 'CANX_MPV_RENDER_OK backend=Wayland' <<<"$mpv_output_text"

if [[ -n "${DISPLAY:-}" ]]; then
  x11_output_text="$(WAYLAND_DISPLAY=canx-invalid timeout 30s \
    "$mpv_output/CanX.Spikes.MpvAvalonia" --smoke "$work_dir/test-source.mkv")"
  printf '%s\n' "$x11_output_text"
  grep -q 'CANX_MPV_RENDER_OK backend=XID' <<<"$x11_output_text"
fi

quickjs_output_text="$(timeout 20s "$quickjs_output/CanX.Spikes.QuickJsInterop")"
printf '%s\n' "$quickjs_output_text"
grep -q 'CANX_QUICKJS_OK' <<<"$quickjs_output_text"

"$repository_root/scripts/audit-native-dependencies-linux.sh" \
  "$aria_output/CanX.Spikes.Aria2Interop"
"$repository_root/scripts/audit-native-dependencies-linux.sh" \
  "$quickjs_output/CanX.Spikes.QuickJsInterop"
"$repository_root/scripts/audit-native-dependencies-linux.sh" \
  "$mpv_output/CanX.Spikes.MpvAvalonia" \
  --allow-needed libEGL.so.1 --allow-needed libvulkan.so.1

echo 'CANX_STATIC_NATIVE_AOT_OK platform=linux-x64'
