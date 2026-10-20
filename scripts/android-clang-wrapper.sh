#!/usr/bin/env bash
set -euo pipefail

linker="clang"
lld="ld.lld"
if [[ -n "${CANX_ANDROID_NDK_ROOT:-}" ]]; then
  case "$(uname -s)" in
    Linux) ndk_host="linux-x86_64" ;;
    Darwin) ndk_host="darwin-x86_64" ;;
    *) echo "unsupported Android build host: $(uname -s)" >&2; exit 2 ;;
  esac
  ndk_bin="$CANX_ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/$ndk_host/bin"
  ndk_clang="$ndk_bin/clang"
  if [[ ! -x "$ndk_clang" ]]; then
    echo "Android NDK Clang was not found: $ndk_clang" >&2
    exit 2
  fi
  linker="$ndk_clang"
  lld="$ndk_bin/ld.lld"
fi

# The NativeAOT targets probe LLD through Clang without an input file. NDK r27's
# Clang rejects that probe before invoking LLD, although real link commands work.
if [[ "$#" -eq 2 && "$1" == "-fuse-ld=lld" && "$2" == "-Wl,--version" ]]; then
  exec "$lld" --version
fi

if [[ -n "${CANX_ANDROID_LINK_API:-}" ]]; then
  transformed_args=()
  for argument in "$@"; do
    if [[ "$argument" =~ ^--target=([^-]+)-linux-android[0-9]+$ ]]; then
      argument="--target=${BASH_REMATCH[1]}-linux-android${CANX_ANDROID_LINK_API}"
    fi
    transformed_args+=("$argument")
  done
  exec "$linker" "${transformed_args[@]}"
fi

exec "$linker" "$@"
