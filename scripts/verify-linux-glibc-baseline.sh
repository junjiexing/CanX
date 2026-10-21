#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: $0 ELF_BINARY [MAX_GLIBC_VERSION]" >&2
  exit 2
fi

binary="$1"
maximum="${2:-2.35}"

[[ -f "$binary" ]] || { echo "binary not found: $binary" >&2; exit 2; }
command -v readelf >/dev/null 2>&1 || { echo "missing tool: readelf" >&2; exit 2; }

machine="$(readelf -h "$binary" | sed -n 's/^[[:space:]]*Machine:[[:space:]]*//p')"
case "$machine" in
  'Advanced Micro Devices X86-64'|AArch64) ;;
  *) echo "unexpected ELF architecture: $machine" >&2; exit 1 ;;
esac

mapfile -t versions < <(
  readelf --version-info "$binary" 2>/dev/null |
    grep -oE 'GLIBC_[0-9]+([.][0-9]+)*' |
    sed 's/^GLIBC_//' |
    sort -Vu
)

[[ "${#versions[@]}" -gt 0 ]] || {
  echo "no GLIBC symbol versions found: $binary" >&2
  exit 1
}

actual="${versions[-1]}"
highest="$(printf '%s\n' "$maximum" "$actual" | sort -V | tail -n 1)"
if [[ "$highest" != "$maximum" ]]; then
  echo "GLIBC baseline exceeded: binary requires GLIBC_$actual, maximum is GLIBC_$maximum" >&2
  echo "Build release artifacts in the pinned Ubuntu 22.04 environment." >&2
  exit 1
fi

printf 'CANX_GLIBC_BASELINE_OK binary=%s maximum=%s actual=%s\n' \
  "$binary" "$maximum" "$actual"
