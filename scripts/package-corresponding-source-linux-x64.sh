#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Build the GPL corresponding-source archive for a Linux release. The archive
# combines the CanX source tree at HEAD with the pinned upstream native
# sources published by CanX.deps (canx-deps-source.tar.gz, downloaded by
# scripts/fetch-native-deps.sh source).
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repository_root/scripts/support/linux-architecture.sh"
release_root="${CANX_RELEASE_OUTPUT:-$repository_root/artifacts/release/$linux_runtime}"
deps_source="$repository_root/.cache/deps-source-package"
version="${CANX_VERSION:-}"

if [[ "${1:-}" == "--version" && $# -eq 2 ]]; then
  version="$2"
elif [[ $# -ne 0 ]]; then
  echo "usage: $0 [--version VERSION]" >&2
  exit 2
fi
if [[ -z "$version" ]]; then
  version="0.1.0-dev.$(git -C "$repository_root" rev-parse --short=12 HEAD)"
fi
[[ "$version" =~ ^[0-9A-Za-z][0-9A-Za-z._+-]*$ ]] || {
  echo "invalid release version: $version" >&2
  exit 2
}

for tool in git gzip tar; do
  command -v "$tool" >/dev/null 2>&1 || { echo "missing source packaging tool: $tool" >&2; exit 2; }
done
if [[ "${CANX_ALLOW_DIRTY_RELEASE:-0}" != 1 && -n "$(git -C "$repository_root" status --porcelain)" ]]; then
  echo 'refusing to package source from a dirty worktree; commit changes or set CANX_ALLOW_DIRTY_RELEASE=1 for local verification' >&2
  exit 2
fi

[[ -f "$deps_source/native-sources.lock" && -d "$deps_source/native" ]] || {
  "$repository_root/scripts/fetch-native-deps.sh" source
}
[[ -f "$deps_source/native-sources.lock" && -d "$deps_source/native" ]] || {
  echo "dependency source package is missing: $deps_source" >&2
  exit 2
}

mkdir -p "$release_root"
package_name="CanX-$version-$linux_runtime-source"
archive="$release_root/$package_name.tar.gz"
[[ ! -e "$archive" ]] || { echo "source archive already exists: $archive" >&2; exit 2; }

temporary_root="$(mktemp -d "$release_root/.source-staging.XXXXXX")"
cleanup() { rm -rf -- "$temporary_root"; }
trap cleanup EXIT
archive_temporary="$temporary_root/$package_name.tar.gz"
manifest_directory="$temporary_root/manifest/$package_name"
canx_source_directory="$manifest_directory/CanX"
mkdir -p "$canx_source_directory"

git -C "$repository_root" archive --format=tar HEAD | tar -xf - -C "$canx_source_directory"
# The dependency source package carries native/<name>/ plus the two lock
# files; merge it next to the CanX tree so the archive layout matches the
# historical from-source package.
cp -R "$deps_source/native" "$deps_source/native-sources.lock" \
  "$deps_source/quickjs-source.lock" "$manifest_directory/"

tar --format=gnu --sort=name --owner=0 --group=0 --numeric-owner \
  --mtime="@${SOURCE_DATE_EPOCH:-$(git -C "$repository_root" show -s --format=%ct HEAD)}" \
  --create -C "$temporary_root/manifest" "$package_name" | gzip -n -9 > "$archive_temporary"
mv -- "$archive_temporary" "$archive"
printf 'CANX_CORRESPONDING_SOURCE_OK version=%s output=%s\n' "$version" "$archive"
