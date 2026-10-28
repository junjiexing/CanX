#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repository_root/scripts/support/linux-architecture.sh"
release_root="${CANX_RELEASE_OUTPUT:-$repository_root/artifacts/release/$linux_runtime}"
version="${CANX_VERSION:-}"

if [[ "${1:-}" == "--version" && $# -eq 2 ]]; then
  version="$2"
elif [[ $# -ne 0 ]]; then
  echo "usage: $0 [--version VERSION]" >&2
  exit 2
fi
[[ -n "$version" ]] || { echo 'release version is required' >&2; exit 2; }
[[ "$version" =~ ^[0-9A-Za-z][0-9A-Za-z._+-]*$ ]] || {
  echo "invalid release version: $version" >&2
  exit 2
}

for tool in gzip grep python3 sha256sum tar; do
  command -v "$tool" >/dev/null 2>&1 || { echo "missing release verification tool: $tool" >&2; exit 2; }
done

package_name="CanX-$version-$linux_runtime"
binary_archive="$release_root/$package_name.tar.gz"
symbols_archive="$release_root/$package_name-symbols.tar.gz"
checksums="$release_root/SHA256SUMS"
for path in "$binary_archive" "$symbols_archive" "$checksums"; do
  [[ -f "$path" ]] || { echo "release artifact is missing: $path" >&2; exit 1; }
done

(
  cd "$release_root"
  sha256sum --check --strict SHA256SUMS
)
gzip -t "$binary_archive"
gzip -t "$symbols_archive"

temporary_root="$(mktemp -d "$release_root/.verify-release.XXXXXX")"
cleanup() { rm -rf -- "$temporary_root"; }
trap cleanup EXIT
binary_members="$temporary_root/binary-members.txt"
symbols_members="$temporary_root/symbols-members.txt"
tar -tzf "$binary_archive" > "$binary_members"
tar -tzf "$symbols_archive" > "$symbols_members"

require_member() {
  local list="$1"
  local member="$2"
  grep -Fxq "$member" "$list" || { echo "required archive member is missing: $member" >&2; exit 1; }
}

require_member "$binary_members" "$package_name/CanX.App"
require_member "$binary_members" "$package_name/LICENSE"
require_member "$binary_members" "$package_name/THIRD-PARTY-NOTICES.md"
require_member "$binary_members" "$package_name/SOURCE-CODE.md"
require_member "$binary_members" "$package_name/sbom.spdx.json"
grep -Fq "$package_name/licenses/" "$binary_members" || { echo 'release license bundle is empty' >&2; exit 1; }
if grep -Eq '\.so([.]|$)' "$binary_members"; then
  echo 'unexpected native sidecar in binary release archive' >&2
  exit 1
fi
grep -Eq "^$package_name-symbols/.+[.](dbg|pdb)$" "$symbols_members" || {
  echo 'release symbols archive has no debug symbols' >&2
  exit 1
}

tar -xOf "$binary_archive" "$package_name/sbom.spdx.json" |
  python3 -c '
import json
import sys

document = json.load(sys.stdin)
assert document["spdxVersion"] == "SPDX-2.3"
assert document["dataLicense"] == "CC0-1.0"
assert any(package.get("name") == "CanX" for package in document["packages"])
assert len(document["relationships"]) == len(document["packages"])
# FFmpeg is configured with --enable-gpl --enable-version3, so its declared license must not claim LGPL.
ffmpeg = [package for package in document["packages"] if package.get("name") == "ffmpeg"]
assert ffmpeg and all(package["licenseDeclared"].startswith("GPL-3.0") for package in ffmpeg), ffmpeg
'

printf 'CANX_RELEASE_PACKAGE_VERIFY_OK version=%s binary=%s symbols=%s\n' \
  "$version" "$binary_archive" "$symbols_archive"
