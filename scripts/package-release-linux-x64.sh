#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_directory="${CANX_APP_OUTPUT:-$repository_root/artifacts/publish/linux-x64}"
symbols_source="${CANX_APP_SYMBOLS_OUTPUT:-$repository_root/artifacts/symbols/linux-x64}"
release_root="${CANX_RELEASE_OUTPUT:-$repository_root/artifacts/release/linux-x64}"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native/linux-x64}"
quickjs_source_root="$repository_root/.cache/native/quickjs/source"
version="${CANX_VERSION:-}"
include_source=true

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || { echo 'missing value for --version' >&2; exit 2; }
      version="$2"
      shift 2
      ;;
    --skip-source)
      include_source=false
      shift
      ;;
    *)
      echo "usage: $0 [--version VERSION] [--skip-source]" >&2
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

mkdir -p "$release_root"
package_name="CanX-$version-linux-x64"
archive="$release_root/$package_name.tar.gz"
symbols_archive="$release_root/$package_name-symbols.tar.gz"
source_archive="$release_root/$package_name-source.tar.gz"
checksums="$release_root/SHA256SUMS"
[[ ! -e "$archive" && ! -e "$symbols_archive" && ( "$include_source" == false || ! -e "$source_archive" ) ]] || {
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
  --rid linux-x64 \
  --version "$version" \
  --native-lock "$repository_root/native/sources-linux-x64.lock" \
  --static-source-root "$static_root/sources" \
  --quickjs-lock "$repository_root/spikes/CanX.Spikes.QuickJsInterop/native-source.lock" \
  --quickjs-source-root "$quickjs_source_root" \
  --assets-file "$repository_root/src/CanX.App/obj/project.assets.json"

find "$symbols_source" -maxdepth 1 -type f \( -name '*.dbg' -o -name '*.pdb' \) -exec cp -t "$symbols_directory" -- {} +
source_date_epoch="${SOURCE_DATE_EPOCH:-$(git -C "$repository_root" show -s --format=%ct HEAD)}"
archive_temporary="$staging_root/${archive##*/}"
symbols_archive_temporary="$staging_root/${symbols_archive##*/}"
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$source_date_epoch" \
  -C "$staging_root" -cf - "$package_name" | gzip -n -9 > "$archive_temporary"
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$source_date_epoch" \
  -C "$staging_root" -cf - "$package_name-symbols" | gzip -n -9 > "$symbols_archive_temporary"

if [[ "$include_source" == true ]]; then
  CANX_VERSION="$version" \
  CANX_RELEASE_OUTPUT="$release_root" \
  CANX_STATIC_ROOT="$static_root" \
    "$repository_root/scripts/package-corresponding-source-linux-x64.sh"
fi

# Do not expose the binary or symbols until the much larger corresponding
# source archive has completed successfully.
mv -- "$archive_temporary" "$archive"
mv -- "$symbols_archive_temporary" "$symbols_archive"

checksums_temporary="$staging_root/SHA256SUMS"
(
  cd "$release_root"
  checksum_files=("${archive##*/}" "${symbols_archive##*/}")
  [[ "$include_source" == false ]] || checksum_files+=("${source_archive##*/}")
  sha256sum "${checksum_files[@]}"
) > "$checksums_temporary"
mv -f -- "$checksums_temporary" "$checksums"

printf 'CANX_RELEASE_PACKAGE_OK version=%s binary=%s symbols=%s source=%s checksums=%s\n' \
  "$version" "$archive" "$symbols_archive" \
  "$([[ "$include_source" == true ]] && printf '%s' "$source_archive" || printf '%s' skipped)" "$checksums"
