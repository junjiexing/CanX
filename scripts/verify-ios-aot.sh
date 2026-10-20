#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repository_root/spikes/CanX.Spikes.Mobile.iOS/CanX.Spikes.Mobile.iOS.csproj"
static_only=false

if [[ "${1:-}" == "--static" ]]; then
  static_only=true
elif [[ "$#" -ne 0 ]]; then
  printf 'Usage: %s [--static]\n' "$0" >&2
  exit 2
fi

for required_file in \
  "$project" \
  "$repository_root/spikes/CanX.Spikes.Mobile.iOS/App.cs" \
  "$repository_root/spikes/CanX.Spikes.Mobile.iOS/AppDelegate.cs" \
  "$repository_root/spikes/CanX.Spikes.Mobile.iOS/Main.cs" \
  "$repository_root/spikes/CanX.Spikes.Mobile.iOS/Info.plist" \
  "$repository_root/spikes/CanX.Spikes.Mobile.iOS/Entitlements.plist" \
  "$repository_root/spikes/CanX.Spikes.Mobile.iOS/Resources/LaunchScreen.xib"; do
  test -f "$required_file"
done
grep -q '<TargetFramework>net10.0-ios</TargetFramework>' "$project"
grep -q '<PublishAot>true</PublishAot>' "$project"
grep -q 'Avalonia.iOS.*12.1.2' "$project"

if command -v xmllint >/dev/null; then
  xmllint --noout \
    "$project" \
    "$repository_root/spikes/CanX.Spikes.Mobile.iOS/Info.plist" \
    "$repository_root/spikes/CanX.Spikes.Mobile.iOS/Entitlements.plist" \
    "$repository_root/spikes/CanX.Spikes.Mobile.iOS/Resources/LaunchScreen.xib"
fi
printf 'CANX_IOS_SCAFFOLD_OK\n'

if [[ "$static_only" == true ]]; then
  exit 0
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
  printf 'CANX_IOS_AOT_BLOCKED reason=requires-macos-and-xcode host=%s\n' "$(uname -s)" >&2
  exit 2
fi

command -v xcodebuild >/dev/null
command -v lipo >/dev/null
xcodebuild -version

dotnet_root="$repository_root/.cache/dotnet"
installer_dir="$repository_root/.cache/dotnet-install"
output_root="$repository_root/artifacts/spikes/ios-aot"

mkdir -p "$dotnet_root" "$installer_dir"
if [[ ! -x "$dotnet_root/dotnet" ]]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh --output "$installer_dir/dotnet-install.sh"
  chmod +x "$installer_dir/dotnet-install.sh"
  "$installer_dir/dotnet-install.sh" --version 10.0.400 --install-dir "$dotnet_root" --no-path
fi

if ! "$dotnet_root/dotnet" workload list | grep -q '^ios[[:space:]]'; then
  "$dotnet_root/dotnet" workload install ios --skip-manifest-update
fi

for runtime in ios-arm64 iossimulator-arm64 iossimulator-x64; do
  output_dir="$output_root/$runtime"
  "$dotnet_root/dotnet" publish "$project" \
    --configuration Release \
    --framework net10.0-ios \
    --runtime "$runtime" \
    --self-contained \
    --output "$output_dir" \
    --property EnableCodeSigning=false

  app_bundle="$(find "$output_dir" -maxdepth 1 -type d -name '*.app' -print -quit)"
  test -n "$app_bundle"
  executable="$app_bundle/CanX.Spikes.Mobile.iOS"
  test -x "$executable"

  if find "$app_bundle" -type f -name '*.dll' -print -quit | grep -q .; then
    printf 'Managed assemblies unexpectedly found in NativeAOT app: %s\n' "$app_bundle" >&2
    exit 1
  fi

  if [[ "$runtime" == *-x64 ]]; then
    expected_arch="x86_64"
  else
    expected_arch="arm64"
  fi
  lipo -archs "$executable" | tr ' ' '\n' | grep -Fxq "$expected_arch"
  printf 'CANX_IOS_AOT_OK runtime=%s app=%s\n' "$runtime" "$app_bundle"
done
