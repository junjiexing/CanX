#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repository_root/scripts/support/linux-architecture.sh"
publish_directory="${CANX_APP_OUTPUT:-$repository_root/artifacts/publish/$linux_runtime}"
symbols_source="${CANX_APP_SYMBOLS_OUTPUT:-$repository_root/artifacts/symbols/$linux_runtime}"
release_root="${CANX_RELEASE_OUTPUT:-$repository_root/artifacts/release/$linux_runtime}"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native-ubuntu22.04/$linux_runtime}"
metadata_root="$static_root/metadata"
version="${CANX_VERSION:-}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || { echo 'missing value for --version' >&2; exit 2; }
      version="$2"
      shift 2
      ;;
    *)
      echo "usage: $0 [--version VERSION]" >&2
      exit 2
      ;;
  esac
done

if [[ -z "$version" ]]; then
  version="0.1.0-dev.$(git -C "$repository_root" rev-parse --short=12 HEAD)"
fi
[[ "$version" =~ ^[0-9A-Za-z][0-9A-Za-z._+-]*$ ]] || {
  echo "invalid release version: $version" >&2
  exit 2
}
[[ -x "$publish_directory/CanX.App" ]] || {
  echo "NativeAOT executable not found: $publish_directory/CanX.App" >&2
  exit 2
}
[[ ! -e "$publish_directory/CanX.smoke-build" ]] || {
  echo "refusing to package a smoke-enabled application: $publish_directory" >&2
  exit 2
}
if [[ "${CANX_ALLOW_DIRTY_RELEASE:-0}" != 1 ]] && [[ -n "$(git -C "$repository_root" status --porcelain)" ]]; then
  echo 'refusing to package a dirty worktree; commit changes or set CANX_ALLOW_DIRTY_RELEASE=1 for local verification' >&2
  exit 2
fi

for tool in git gzip python3 sha256sum tar; do
  command -v "$tool" >/dev/null 2>&1 || { echo "missing packaging tool: $tool" >&2; exit 2; }
done
[[ -f "$metadata_root/sources-linux-x64.lock" ]] || {
  echo "dependency metadata is missing: $metadata_root" >&2
  echo 'run scripts/fetch-native-deps.sh linux to download the prebuilt dependency package' >&2
  exit 2
}

mkdir -p "$release_root"
package_name="CanX-$version-$linux_runtime"
archive="$release_root/$package_name.tar.gz"
symbols_archive="$release_root/$package_name-symbols.tar.gz"
checksums="$release_root/SHA256SUMS"
[[ ! -e "$archive" && ! -e "$symbols_archive" ]] || {
  echo "release output already exists for version $version" >&2
  exit 2
}

staging_root="$(mktemp -d "$release_root/.staging.XXXXXX")"
cleanup() { rm -rf -- "$staging_root"; }
trap cleanup EXIT
package_directory="$staging_root/$package_name"
symbols_directory="$staging_root/$package_name-symbols"
mkdir -p "$package_directory" "$symbols_directory"
install -m 0755 "$publish_directory/CanX.App" "$package_directory/CanX.App"

python3 "$repository_root/scripts/generate-release-metadata.py" \
  --repository-root "$repository_root" \
  --publish-directory "$publish_directory" \
  --output-directory "$package_directory" \
  --rid "$linux_runtime" \
  --version "$version" \
  --native-lock "$metadata_root/sources-linux-x64.lock" \
  --static-source-root "$metadata_root/sources" \
  --quickjs-lock "$metadata_root/quickjs-source.lock" \
  --quickjs-source-root "$metadata_root/sources-quickjs" \
  --assets-file "$repository_root/src/CanX.App/obj/project.assets.json"

find "$symbols_source" -maxdepth 1 -type f \( -name '*.dbg' -o -name '*.pdb' \) -exec cp -t "$symbols_directory" -- {} +
source_date_epoch="${SOURCE_DATE_EPOCH:-$(git -C "$repository_root" show -s --format=%ct HEAD)}"
archive_temporary="$staging_root/${archive##*/}"
symbols_archive_temporary="$staging_root/${symbols_archive##*/}"
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$source_date_epoch" \
  -C "$staging_root" -cf - "$package_name" | gzip -n -9 > "$archive_temporary"
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$source_date_epoch" \
  -C "$staging_root" -cf - "$package_name-symbols" | gzip -n -9 > "$symbols_archive_temporary"

# Move into place only after both archives are fully written so a failed
# run never leaves a partial package next to the previous release.
mv -- "$archive_temporary" "$archive"
mv -- "$symbols_archive_temporary" "$symbols_archive"

checksums_temporary="$staging_root/SHA256SUMS"
(
  cd "$release_root"
  sha256sum -- "${archive##*/}" "${symbols_archive##*/}"
) > "$checksums_temporary"
mv -f -- "$checksums_temporary" "$checksums"

printf 'CANX_RELEASE_PACKAGE_OK version=%s binary=%s symbols=%s checksums=%s\n' \
  "$version" "$archive" "$symbols_archive" "$checksums"
