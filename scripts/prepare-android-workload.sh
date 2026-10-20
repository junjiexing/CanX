#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_root="$repository_root/.cache/dotnet"
installer_dir="$repository_root/.cache/dotnet-install"
android_sdk="$repository_root/.cache/android-sdk"
java_sdk="$repository_root/.cache/jdk"
android_ndk_version="${CANX_ANDROID_NDK_VERSION:-28.2.13676358}"
project="$repository_root/spikes/CanX.Spikes.Mobile.Android/CanX.Spikes.Mobile.Android.csproj"

mkdir -p "$dotnet_root" "$installer_dir" "$android_sdk" "$java_sdk"
if [[ ! -x "$dotnet_root/dotnet" ]]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh --output "$installer_dir/dotnet-install.sh"
  chmod +x "$installer_dir/dotnet-install.sh"
  "$installer_dir/dotnet-install.sh" --version 10.0.400 --install-dir "$dotnet_root" --no-path
fi

if ! "$dotnet_root/dotnet" workload list | grep -q '^android[[:space:]]'; then
  "$dotnet_root/dotnet" workload install android --skip-manifest-update
fi

if [[ ! -f "$android_sdk/platforms/android-36/android.jar" || \
      ! -x "$android_sdk/build-tools/36.0.0/aapt2" || \
      ! -x "$java_sdk/bin/java" ]]; then
  "$dotnet_root/dotnet" build "$project" \
    --target InstallAndroidDependencies \
    --framework net10.0-android \
    --runtime android-arm64 \
    --property EnableLLVM=true \
    --property AndroidSdkDirectory="$android_sdk" \
    --property JavaSdkDirectory="$java_sdk" \
    --property AcceptAndroidSdkLicenses=true
fi

android_ndk="$android_sdk/ndk/$android_ndk_version"
if [[ ! -d "$android_ndk" ]]; then
  sdkmanager="$android_sdk/cmdline-tools/latest/bin/sdkmanager"
  [[ -x "$sdkmanager" ]] || {
    printf 'Android sdkmanager was not installed under %s\n' "$android_sdk" >&2
    exit 1
  }
  JAVA_HOME="$java_sdk" ANDROID_SDK_ROOT="$android_sdk" \
    "$sdkmanager" "ndk;$android_ndk_version"
fi
if [[ ! -d "$android_ndk" ]]; then
  printf 'Android NDK %s was not installed under %s\n' "$android_ndk_version" "$android_sdk" >&2
  exit 1
fi

# InstallAndroidDependencies currently expands NDK symbolic links as tiny text
# files on Linux. Restore those links before Clang/LLD are used by NativeAOT.
ndk_bin="$android_ndk/toolchains/llvm/prebuilt/linux-x86_64/bin"
if [[ -d "$ndk_bin" ]]; then
  while IFS= read -r symlink_stub; do
    link_target="$(<"$symlink_stub")"
    if [[ "$link_target" =~ ^[A-Za-z0-9+_.-]+$ && -e "$ndk_bin/$link_target" ]]; then
      ln -sfn "$link_target" "$symlink_stub"
    fi
  done < <(find "$ndk_bin" -maxdepth 1 -type f -size -128c -print)
fi

printf '%s\n' "$dotnet_root/dotnet" "$android_sdk" "$java_sdk" "$android_ndk"
