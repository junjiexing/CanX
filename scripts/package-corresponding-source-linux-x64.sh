#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repository_root/scripts/support/linux-architecture.sh"
release_root="${CANX_RELEASE_OUTPUT:-$repository_root/artifacts/release/$linux_runtime}"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native/$linux_runtime}"
source_root="$static_root/sources"
native_lock="$repository_root/native/sources-linux-x64.lock"
quickjs_lock="$repository_root/spikes/CanX.Spikes.QuickJsInterop/native-source.lock"
quickjs_source="$repository_root/.cache/native/quickjs/source"
quickjs_archive="$repository_root/.cache/native/quickjs/quickjs.tar.xz"
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
for tool in diff sha256sum; do
  command -v "$tool" >/dev/null 2>&1 || { echo "missing source verification tool: $tool" >&2; exit 2; }
done
[[ -f "$native_lock" && -f "$quickjs_lock" ]] || { echo 'source lock file is missing' >&2; exit 2; }
if [[ "${CANX_ALLOW_DIRTY_RELEASE:-0}" != 1 ]] && [[ -n "$(git -C "$repository_root" status --porcelain)" ]]; then
  echo 'refusing to package source from a dirty worktree; commit changes or set CANX_ALLOW_DIRTY_RELEASE=1 for local verification' >&2
  exit 2
fi

mkdir -p "$release_root"
package_name="CanX-$version-$linux_runtime-source"
archive="$release_root/$package_name.tar.gz"
[[ ! -e "$archive" ]] || { echo "source archive already exists: $archive" >&2; exit 2; }

temporary_root="$(mktemp -d "$release_root/.source-staging.XXXXXX")"
cleanup() { rm -rf -- "$temporary_root"; }
trap cleanup EXIT
tar_path="$temporary_root/source.tar"
archive_temporary="$temporary_root/$package_name.tar.gz"
manifest_directory="$temporary_root/manifest/$package_name"
canx_source_directory="$manifest_directory/CanX"
mkdir -p "$canx_source_directory"

git -C "$repository_root" archive --format=tar HEAD | tar -xf - -C "$canx_source_directory"
cp "$native_lock" "$manifest_directory/native-sources.lock"
cp "$quickjs_lock" "$manifest_directory/quickjs-source.lock"
tar --format=gnu --sort=name --owner=0 --group=0 --numeric-owner \
  --mtime="@${SOURCE_DATE_EPOCH:-$(git -C "$repository_root" show -s --format=%ct HEAD)}" \
  --create --file="$tar_path" -C "$temporary_root/manifest" "$package_name"

while IFS='|' read -r name repository revision license; do
  [[ -n "$name" && "${name:0:1}" != '#' ]] || continue
  source_directory="$source_root/$name"
  [[ -d "$source_directory" ]] || { echo "locked source is missing: $source_directory" >&2; exit 1; }
  [[ -e "$source_directory/.git" ]] || {
    echo "locked source has no Git metadata and cannot be verified: $source_directory" >&2
    exit 1
  }
  actual_revision="$(git -C "$source_directory" rev-parse HEAD)"
  [[ "$actual_revision" == "$revision" ]] || {
    echo "source revision mismatch for $name: expected $revision, found $actual_revision" >&2
    exit 1
  }
  tar --format=gnu --sort=name --owner=0 --group=0 --numeric-owner \
    --mtime="@${SOURCE_DATE_EPOCH:-$(git -C "$repository_root" show -s --format=%ct HEAD)}" \
    --exclude-vcs \
    --exclude='./externals/skia/out' \
    --exclude='./externals/skia/out/*' \
    --transform="s#^\./#$package_name/native/$name/#" \
    --append --file="$tar_path" -C "$source_directory" .
done < "$native_lock"

quickjs_version="$(awk -F '=' '$1 == "version" { print $2; found=1; exit } END { if (!found) exit 1 }' "$quickjs_lock")"
quickjs_sha256="$(awk -F '=' '$1 == "sha256" { print $2; found=1; exit } END { if (!found) exit 1 }' "$quickjs_lock")"
[[ -f "$quickjs_source/VERSION" && "$(<"$quickjs_source/VERSION")" == "$quickjs_version" ]] || {
  echo "QuickJS source version mismatch or source is missing: $quickjs_source" >&2
  exit 1
}
[[ -f "$quickjs_archive" ]] || { echo "QuickJS source archive is missing: $quickjs_archive" >&2; exit 1; }
printf '%s  %s\n' "$quickjs_sha256" "$quickjs_archive" | sha256sum --check --status || {
  echo "QuickJS source archive checksum mismatch: $quickjs_archive" >&2
  exit 1
}
verified_quickjs_source="$temporary_root/verified-quickjs"
mkdir -p "$verified_quickjs_source"
tar -xJf "$quickjs_archive" -C "$verified_quickjs_source" --strip-components=1
diff -qr "$verified_quickjs_source" "$quickjs_source" >/dev/null || {
  echo "QuickJS source differs from the locked archive: $quickjs_source" >&2
  exit 1
}
tar --format=gnu --sort=name --owner=0 --group=0 --numeric-owner \
  --mtime="@${SOURCE_DATE_EPOCH:-$(git -C "$repository_root" show -s --format=%ct HEAD)}" \
  --exclude-vcs \
  --transform="s#^\./#$package_name/native/quickjs/#" \
  --append --file="$tar_path" -C "$verified_quickjs_source" .

gzip -n -9 < "$tar_path" > "$archive_temporary"
mv -- "$archive_temporary" "$archive"
printf 'CANX_CORRESPONDING_SOURCE_OK version=%s output=%s\n' "$version" "$archive"
