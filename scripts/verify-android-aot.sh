#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mapfile -t environment_paths < <("$repository_root/scripts/prepare-android-workload.sh" | tail -n 4)
dotnet_path="${environment_paths[0]}"
android_sdk="${environment_paths[1]}"
java_sdk="${environment_paths[2]}"
android_ndk="${environment_paths[3]}"
project="$repository_root/spikes/CanX.Spikes.Mobile.Android/CanX.Spikes.Mobile.Android.csproj"
project_directory="$(dirname "$project")"
output_root="$repository_root/artifacts/spikes/android-aot"
clang_wrapper="$repository_root/scripts/android-clang-wrapper.sh"
verification_dir="$(mktemp -d)"

trap 'rm -rf "$verification_dir"' EXIT

for runtime in android-arm64 android-x64; do
  output_dir="$output_root/$runtime"
  # The Android workload generates NativeAotRuntimeProvider.java as a side
  # effect of a stamp target. Remove only this RID's generated directories so
  # an incremental publish cannot retain the stamp while losing the source.
  rm -rf \
    "$project_directory/bin/Release/net10.0-android/$runtime" \
    "$project_directory/obj/Release/net10.0-android/$runtime" \
    "$output_dir"
  "$dotnet_path" publish "$project" \
    --configuration Release \
    --framework net10.0-android \
    --runtime "$runtime" \
    --self-contained \
    --output "$output_dir" \
    --property AndroidSdkDirectory="$android_sdk" \
    --property JavaSdkDirectory="$java_sdk" \
    --property AndroidNdkDirectory="$android_ndk" \
    --property CppLinker="$clang_wrapper"

  apk="$(find "$output_dir" -maxdepth 1 -name '*.apk' -print -quit)"
  test -n "$apk"
  if [[ "$runtime" == "android-arm64" ]]; then
    archive_abi="arm64-v8a"
    elf_machine="AArch64"
  else
    archive_abi="x86_64"
    elf_machine="Advanced Micro Devices X86-64"
  fi
  archive_listing="$(unzip -l "$apk")"
  grep -q "lib/$archive_abi/.*\.so" <<<"$archive_listing"
  grep -q "lib/$archive_abi/libCanX.Spikes.Mobile.Android\.so" <<<"$archive_listing"
  if grep -Eq 'assemblies/.*\.dll' <<<"$archive_listing"; then
    printf 'Managed assemblies unexpectedly found in NativeAOT APK: %s\n' "$apk" >&2
    exit 1
  fi
  unzip -qq "$apk" "lib/$archive_abi/libCanX.Spikes.Mobile.Android.so" -d "$verification_dir/$runtime"
  unzip -qq "$apk" classes.dex -d "$verification_dir/$runtime"
  grep -aq 'NativeAotRuntimeProvider' "$verification_dir/$runtime/classes.dex"
  readelf -h "$verification_dir/$runtime/lib/$archive_abi/libCanX.Spikes.Mobile.Android.so" \
    | grep -Fq "Machine:                           $elf_machine"
  printf 'CANX_ANDROID_AOT_OK runtime=%s apk=%s\n' "$runtime" "$apk"
done
