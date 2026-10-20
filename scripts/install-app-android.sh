#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
abi="${1:-arm64-v8a}"
serial="${2:-}"
case "$abi" in
  arm64-v8a) runtime="android-arm64" ;;
  x86_64) runtime="android-x64" ;;
  *) echo "usage: $0 [arm64-v8a|x86_64] [device-serial]" >&2; exit 2 ;;
esac

apk="$repository_root/artifacts/android/$runtime-full/org.canx.app-Signed.apk"
adb="${CANX_ANDROID_ADB:-$repository_root/.cache/android-sdk/platform-tools/adb}"
[[ -f "$apk" ]] || { echo "Android release APK not found: $apk" >&2; exit 1; }
[[ -x "$adb" ]] || { echo "ADB not found: $adb" >&2; exit 1; }

# Plain dotnet build produces an APK without the statically linked FFmpeg/mpv
# libraries. Only install the output from publish-static-app-android.sh.
for library in libCanX.App.Android.so libcanx_aria2.so; do
  if ! unzip -Z1 "$apk" | grep -Fx "lib/$abi/$library" >/dev/null; then
    echo "Android release APK is missing $library: $apk" >&2
    exit 1
  fi
done

adb_command=("$adb")
if [[ -n "$serial" ]]; then adb_command+=(-s "$serial"); fi
"${adb_command[@]}" install -r "$apk"
