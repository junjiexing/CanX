#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
abi="${1:-arm64-v8a}"
skip_native="${2:-}"

case "$abi" in
  arm64-v8a)
    runtime="android-arm64"
    ndk_triple="aarch64-linux-android"
    clang_arch="aarch64"
    ;;
  x86_64)
    runtime="android-x64"
    ndk_triple="x86_64-linux-android"
    clang_arch="x86_64"
    ;;
  *)
    echo "usage: $0 [arm64-v8a|x86_64] [--skip-native]" >&2
    exit 2
    ;;
esac

if [[ -n "$skip_native" && "$skip_native" != "--skip-native" ]]; then
  echo "usage: $0 [arm64-v8a|x86_64] [--skip-native]" >&2
  exit 2
fi

environment_output="$("$repository_root/scripts/prepare-android-workload.sh")"
environment_paths=()
while IFS= read -r environment_path; do
  environment_paths+=("$environment_path")
done < <(printf '%s\n' "$environment_output" | tail -n 4)
dotnet_path="${environment_paths[0]}"
android_sdk="${environment_paths[1]}"
java_sdk="${environment_paths[2]}"
android_ndk="${environment_paths[3]}"
api="${CANX_ANDROID_API:-23}"
android_debuggable="${CANX_ANDROID_DEBUGGABLE:-false}"
android_application_id="${CANX_ANDROID_APPLICATION_ID:-org.canx.app}"
android_application_title="${CANX_ANDROID_APPLICATION_TITLE:-CanX}"
native_root="${CANX_ANDROID_NATIVE_ROOT:-$repository_root/.cache/android-native/$abi}"
native_lib="$native_root/prefix/lib"
case "$(uname -s)" in
  Linux) ndk_host="linux-x86_64" ;;
  Darwin) ndk_host="darwin-x86_64" ;;
  *) echo "unsupported Android build host: $(uname -s)" >&2; exit 2 ;;
esac
ndk_root="$android_ndk/toolchains/llvm/prebuilt/$ndk_host"
ndk_lib="$ndk_root/sysroot/usr/lib/$ndk_triple"
clang_version_dir="$(find "$ndk_root/lib/clang" -mindepth 1 -maxdepth 1 -type d -print | sort | tail -n 1)"
clang_lib="$clang_version_dir/lib/linux/$clang_arch"
case "$android_debuggable" in
  true|false) ;;
  *)
    echo "CANX_ANDROID_DEBUGGABLE must be true or false" >&2
    exit 2
    ;;
esac
output_suffix="full"
if [[ "$android_debuggable" == "true" ]]; then
  output_suffix="debuggable"
fi
output_dir="${CANX_ANDROID_OUTPUT:-$repository_root/artifacts/android/$runtime-$output_suffix}"
symbols_dir="$repository_root/artifacts/symbols/$runtime-$output_suffix"
project="$repository_root/src/CanX.App.Android/CanX.App.Android.csproj"
project_directory="$(dirname "$project")"

if [[ "$skip_native" != "--skip-native" ]]; then
  ANDROID_NDK_ROOT="$android_ndk" \
    "$repository_root/scripts/build-static-native-android.sh" "$abi" all
fi

join_by_pipe() {
  local IFS='|'
  printf '%s' "$*"
}

archives=(
  '-Wl%2C--start-group'
  "$native_lib/libcanx_quickjs.a"
  "$native_lib/libmpv.a"
  "$native_lib/libass.a"
  "$native_lib/libfribidi.a"
  "$native_lib/libharfbuzz.a"
  "$native_lib/libfreetype.a"
  "$native_lib/libunibreak.a"
  "$native_lib/libplacebo.a"
  "$native_lib/libavdevice.a"
  "$native_lib/libavfilter.a"
  "$native_lib/libswscale.a"
  "$native_lib/libavformat.a"
  "$native_lib/libavcodec.a"
  "$native_lib/libswresample.a"
  "$native_lib/libavutil.a"
  "$native_lib/libssl.a"
  "$native_lib/libcrypto.a"
  "$native_lib/libz.a"
  "$clang_lib/libatomic.a"
  '-Wl%2C--end-group'
  "-Wl%2C-Map%2C$native_root/CanX.App.Android.map"
)

for archive in "${archives[@]}"; do
  if [[ "$archive" == /* && ! -f "$archive" ]]; then
    echo "required Android archive was not produced: $archive" >&2
    exit 1
  fi
done

# The Android workload uses generated RID-specific Java sources. Removing only
# this project's selected RID avoids a stale NativeAOT provider stamp.
cmake -E remove_directory "$project_directory/bin/Release/net10.0-android/$runtime"
cmake -E remove_directory "$project_directory/obj/Release/net10.0-android/$runtime"
cmake -E remove_directory "$output_dir"

CANX_ANDROID_LINK_API="$api" CANX_ANDROID_NDK_ROOT="$android_ndk" \
  "$dotnet_path" publish "$project" \
  --configuration Release \
  --framework net10.0-android \
  --runtime "$runtime" \
  --self-contained \
  --output "$output_dir" \
  --property AndroidSdkDirectory="$android_sdk" \
  --property JavaSdkDirectory="$java_sdk" \
  --property AndroidNdkDirectory="$android_ndk" \
  --property ApplicationId="$android_application_id" \
  --property ApplicationTitle="$android_application_title" \
  --property CanXProductionPublish=true \
  --property AndroidIncludeDebugSymbols="$android_debuggable" \
  --property CppLinker="$repository_root/scripts/android-clang-wrapper.sh" \
  --property CanXAndroidAria2Library="$native_lib/libcanx_aria2.so" \
  --property CanXAndroidAbi="$abi" \
  '-p:CanXDirectPInvokeLibraries=mpv|canx_quickjs|avcodec' \
  "-p:CanXStaticLibraries=$(join_by_pipe "${archives[@]}")" \
  '-p:CanXNativeSystemLibraries=android|log|EGL|GLESv2|OpenSLES|mediandk|dl|m'

apk="$output_dir/$android_application_id-Signed.apk"
[[ -f "$apk" ]] || { echo "signed Android APK was not produced: $apk" >&2; exit 1; }

# Android's managed TLS stack calls this conventional JNI export while
# validating remote certificates. Hiding all archive symbols removes it from
# the NativeAOT application DSO even though the implementation is statically
# linked, which makes every HTTPS request fail at the handshake.
android_crypto_callback="Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate"
dynamic_symbols="$(unzip -p "$apk" "lib/$abi/libCanX.App.Android.so" | "$ndk_root/bin/llvm-nm" -D -)"
if [[ "$dynamic_symbols" != *"$android_crypto_callback"* ]]; then
  echo "Android crypto JNI callback was not exported by the NativeAOT application library" >&2
  exit 1
fi

symbols_file="$output_dir/CanX.App.Android.so.dbg"
[[ -f "$symbols_file" ]] || { echo "Android native debug symbols were not produced: $symbols_file" >&2; exit 1; }
mkdir -p "$symbols_dir"
mv -f "$symbols_file" "$symbols_dir/CanX.App.Android.so.dbg"
if [[ "$android_debuggable" == false ]] && \
  unzip -Z1 "$apk" | grep -Eq '[.](pdb|dbg|dSYM)(/|$)'; then
  echo "Android APK contains debug symbols: $apk" >&2
  exit 1
fi

printf 'CANX_STATIC_ANDROID_APP_OK runtime=%s apk=%s symbols=%s\n' "$runtime" "$apk" "$symbols_dir"
