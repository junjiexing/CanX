#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Download the prebuilt native dependency package pinned by native-deps.lock
# and extract it into the cache layout the publish scripts consume:
#
#   fetch-native-deps.sh linux              -> .cache/static-native-ubuntu22.04/<rid>/
#   fetch-native-deps.sh android <abi>      -> .cache/android-native/<abi>/
#   fetch-native-deps.sh macos              -> .cache/ (mpv frameworks + shims)
#   fetch-native-deps.sh source             -> .cache/deps-source-package/
#
# Extraction is skipped when the marker file already records the pinned tag;
# set CANX_DEPS_FORCE=1 to download and extract again.
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/native-deps.lock"
downloads="$repository_root/.cache/deps-downloads"
platform="${1:-}"
abi="${2:-}"

[[ -f "$lock_file" ]] || { echo "dependency pin is missing: $lock_file" >&2; exit 2; }
deps_repository="$(awk -F '=' '$1 == "repository" { print $2; exit }' "$lock_file")"
deps_tag="$(awk -F '=' '$1 == "tag" { print $2; exit }' "$lock_file")"
[[ -n "$deps_repository" && -n "$deps_tag" ]] || {
  echo "native-deps.lock must set repository and tag" >&2
  exit 2
}
deps_repository="${deps_repository%.git}"

for tool in curl tar; do
  command -v "$tool" >/dev/null 2>&1 || { echo "missing download tool: $tool" >&2; exit 2; }
done
if [[ "$(uname -s)" == Darwin ]]; then
  checksum_candidates=('shasum -a 256')
else
  checksum_candidates=(sha256sum 'shasum -a 256')
fi
sha256_tool=''
for candidate in "${checksum_candidates[@]}"; do
  if command -v "${candidate%% *}" >/dev/null 2>&1; then
    sha256_tool="$candidate"
    break
  fi
done
[[ -n "$sha256_tool" ]] || {
  echo 'missing checksum tool: sha256sum or shasum' >&2
  exit 2
}

mkdir -p "$downloads"

fetch_asset() {
  local asset="$1"
  local archive="$downloads/$asset"
  local checksums="$downloads/SHA256SUMS-$deps_tag"
  local checksum_line
  if [[ ! -f "$checksums" ]]; then
    curl -fsSL --retry 3 --retry-delay 2 \
      "$deps_repository/releases/download/$deps_tag/SHA256SUMS" --output "$checksums"
  fi
  checksum_line="$(awk -v asset="$asset" '$2 == asset || $2 == "./" asset' "$checksums")"
  [[ -n "$checksum_line" ]] || {
    echo "SHA256SUMS for $deps_tag does not list $asset" >&2
    exit 1
  }
  verify_checksum() {
    (cd "$downloads" && printf '%s\n' "$checksum_line" | $sha256_tool --check --status)
  }
  if [[ ! -f "$archive" ]] || ! verify_checksum; then
    curl -fSL --retry 3 --retry-delay 2 \
      "$deps_repository/releases/download/$deps_tag/$asset" --output "$archive.partial"
    mv -- "$archive.partial" "$archive"
  fi
  verify_checksum || {
    echo "checksum mismatch for $asset (release $deps_tag)" >&2
    exit 1
  }
  printf '%s\n' "$archive"
}

current_tag() {
  local marker="$1"
  [[ -f "$marker" ]] && cat "$marker" || true
}

extract_linux() {
  source "$repository_root/scripts/support/linux-architecture.sh"
  local target="$repository_root/.cache/static-native-ubuntu22.04/$linux_runtime"
  local marker="$target/.canx-deps-tag"
  if [[ "${CANX_DEPS_FORCE:-0}" != 1 && "$(current_tag "$marker")" == "$deps_tag" ]]; then
    echo "native dependencies for $linux_runtime already match $deps_tag"
    return
  fi
  local archive
  archive="$(fetch_asset "canx-deps-linux-$linux_architecture.tar.gz")"
  mkdir -p "$target"
  rm -rf -- "$target/prefix" "$target/shims" "$target/metadata"
  tar -xzf "$archive" -C "$target"
  printf '%s\n' "$deps_tag" > "$marker"
  echo "extracted canx-deps-linux-$linux_architecture ($deps_tag) into $target"
}

extract_android() {
  case "$abi" in
    arm64-v8a|x86_64) ;;
    *) echo "usage: $0 android [arm64-v8a|x86_64]" >&2; exit 2 ;;
  esac
  local target="$repository_root/.cache/android-native/$abi"
  local marker="$target/.canx-deps-tag"
  if [[ "${CANX_DEPS_FORCE:-0}" != 1 && "$(current_tag "$marker")" == "$deps_tag" ]]; then
    echo "native dependencies for android-$abi already match $deps_tag"
    return
  fi
  local archive
  archive="$(fetch_asset "canx-deps-android-$abi.tar.gz")"
  mkdir -p "$target"
  rm -rf -- "$target/prefix"
  tar -xzf "$archive" -C "$target"
  printf '%s\n' "$deps_tag" > "$marker"
  echo "extracted canx-deps-android-$abi ($deps_tag) into $target"
}

extract_macos() {
  local target="$repository_root/.cache"
  local marker="$target/.canx-deps-macos-tag"
  if [[ "${CANX_DEPS_FORCE:-0}" != 1 && "$(current_tag "$marker")" == "$deps_tag" ]]; then
    echo "native dependencies for macos-arm64 already match $deps_tag"
    return
  fi
  local archive
  archive="$(fetch_asset canx-deps-macos-arm64.tar.gz)"
  mkdir -p "$target"
  rm -rf -- "$target/macos/mpv-frameworks" \
    "$target/native/quickjs/macos-build" \
    "$target/mac-native/aria2-shim"
  tar -xzf "$archive" -C "$target"
  printf '%s\n' "$deps_tag" > "$marker"
  echo "extracted canx-deps-macos-arm64 ($deps_tag) into $target"
}

extract_source() {
  local target="$repository_root/.cache/deps-source-package"
  local marker="$target/.canx-deps-tag"
  if [[ "${CANX_DEPS_FORCE:-0}" != 1 && "$(current_tag "$marker")" == "$deps_tag" ]]; then
    echo "dependency source package already matches $deps_tag"
    return
  fi
  local archive
  archive="$(fetch_asset canx-deps-source.tar.gz)"
  rm -rf -- "$target"
  mkdir -p "$target"
  tar -xzf "$archive" -C "$target"
  printf '%s\n' "$deps_tag" > "$marker"
  echo "extracted canx-deps-source ($deps_tag) into $target"
}

case "$platform" in
  linux) extract_linux ;;
  android) extract_android ;;
  macos) extract_macos ;;
  source) extract_source ;;
  *) echo "usage: $0 [linux|android|macos|source] [abi]" >&2; exit 2 ;;
esac
