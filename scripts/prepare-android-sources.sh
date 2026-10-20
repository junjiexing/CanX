#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/native/sources-linux-x64.lock"
source_root="$repository_root/.cache/upstream"
quickjs_lock="$repository_root/spikes/CanX.Spikes.QuickJsInterop/native-source.lock"
quickjs_root="$repository_root/.cache/native/quickjs"
mkdir -p "$source_root" "$quickjs_root/source"

for name in aria2 c-ares sqlite libssh2 expat zlib openssl \
            mpv libplacebo ffmpeg freetype fribidi harfbuzz libass libunibreak; do
  url="$(awk -F '|' -v name="$name" '$1 == name { print $2; exit }' "$lock_file")"
  revision="$(awk -F '|' -v name="$name" '$1 == name { print $3; exit }' "$lock_file")"
  [[ -n "$url" && -n "$revision" ]] || {
    echo "missing pinned Android source: $name" >&2
    exit 1
  }
  source_dir="$source_root/$name"
  if [[ ! -d "$source_dir/.git" ]]; then
    mkdir -p "$source_dir"
    git -C "$source_dir" init --quiet
    git -C "$source_dir" remote add origin "$url"
  fi
  if [[ "$(git -C "$source_dir" rev-parse HEAD 2>/dev/null || true)" != "$revision" ]]; then
    if [[ -n "$(git -C "$source_dir" status --porcelain 2>/dev/null)" ]]; then
      echo "refusing to replace modified cached source: $source_dir" >&2
      exit 1
    fi
    git -C "$source_dir" fetch --depth=1 origin "$revision"
    git -C "$source_dir" -c advice.detachedHead=false checkout --detach FETCH_HEAD
  fi
  if [[ "$name" == libplacebo ]]; then
    git -C "$source_dir" submodule sync -- \
      3rdparty/glad 3rdparty/jinja 3rdparty/markupsafe 3rdparty/fast_float
    git -C "$source_dir" submodule update --init --depth=1 -- \
      3rdparty/glad 3rdparty/jinja 3rdparty/markupsafe 3rdparty/fast_float
  fi
  printf 'CANX_ANDROID_SOURCE_OK name=%s revision=%s\n' "$name" "$revision"
done

source "$quickjs_lock"
archive="$quickjs_root/quickjs.tar.xz"
if [[ ! -f "$archive" ]] || \
   [[ "$(shasum -a 256 "$archive" | awk '{ print $1 }')" != "$sha256" ]]; then
  curl -fsSL "$url" --output "$archive"
fi
[[ "$(shasum -a 256 "$archive" | awk '{ print $1 }')" == "$sha256" ]] || {
  echo 'QuickJS source checksum mismatch' >&2
  exit 1
}
if [[ ! -f "$quickjs_root/source/VERSION" ]] || \
   [[ "$(<"$quickjs_root/source/VERSION")" != "$version" ]]; then
  tar -xJf "$archive" -C "$quickjs_root/source" --strip-components=1
fi
printf 'CANX_ANDROID_SOURCE_OK name=quickjs version=%s\n' "$version"
