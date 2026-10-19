#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 BINARY [--allow-needed NAME] [--allow-sidecar NAME]" >&2
  exit 2
fi

binary="$1"
shift
allowed_needed=(
  libc.so.6
  libm.so.6
  libdl.so.2
  libpthread.so.0
  librt.so.1
  ld-linux-x86-64.so.2
  ld-linux-aarch64.so.1
)
allowed_sidecars=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --allow-needed)
      [[ $# -ge 2 ]] || { echo "missing value for --allow-needed" >&2; exit 2; }
      allowed_needed+=("$2")
      shift 2
      ;;
    --allow-sidecar)
      [[ $# -ge 2 ]] || { echo "missing value for --allow-sidecar" >&2; exit 2; }
      allowed_sidecars+=("$2")
      shift 2
      ;;
    *)
      echo "unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

[[ -f "$binary" ]] || { echo "binary not found: $binary" >&2; exit 2; }
command -v readelf >/dev/null 2>&1 || { echo "missing tool: readelf" >&2; exit 2; }

contains() {
  local wanted="$1"
  shift
  local item
  for item in "$@"; do
    [[ "$item" == "$wanted" ]] && return 0
  done
  return 1
}

failure=0
mapfile -t needed < <(readelf -d "$binary" | sed -n 's/.*Shared library: \[\([^]]*\)\].*/\1/p')
for library in "${needed[@]}"; do
  if ! contains "$library" "${allowed_needed[@]}"; then
    echo "unexpected dynamic dependency: $library" >&2
    failure=1
  fi
done

binary_dir="$(cd "$(dirname "$binary")" && pwd)"
while IFS= read -r sidecar; do
  sidecar_name="${sidecar##*/}"
  if ! contains "$sidecar_name" "${allowed_sidecars[@]}"; then
    echo "unexpected native sidecar: $sidecar_name" >&2
    failure=1
  fi
done < <(find "$binary_dir" -maxdepth 1 -type f \
  \( -name '*.so' -o -name '*.so.*' \) -print | sort)

if [[ "$failure" -ne 0 ]]; then
  exit 1
fi

printf 'CANX_NATIVE_DEPENDENCY_AUDIT_OK binary=%s needed=%s sidecars=%s\n' \
  "$binary" "${#needed[@]}" "${#allowed_sidecars[@]}"
