#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Ensure a source checkout of the CanX.deps repository exists at the tag
# pinned by native-deps.lock and print its path. Development-side scripts use
# it to reach the shim C sources (native/aria2-shim, native/quickjs-shim),
# which live in CanX.deps while the release flow consumes prebuilt binaries.
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock_file="$repository_root/native-deps.lock"
target="$repository_root/.cache/deps-source"
marker="$target/.canx-deps-tag"

[[ -f "$lock_file" ]] || { echo "dependency pin is missing: $lock_file" >&2; exit 2; }
deps_repository="$(awk -F '=' '$1 == "repository" { print $2; exit }' "$lock_file")"
deps_tag="$(awk -F '=' '$1 == "tag" { print $2; exit }' "$lock_file")"
[[ -n "$deps_repository" && -n "$deps_tag" ]] || {
  echo "native-deps.lock must set repository and tag" >&2
  exit 2
}

command -v git >/dev/null 2>&1 || { echo 'missing download tool: git' >&2; exit 2; }

if [[ -d "$target/.git" && -f "$marker" && "$(<"$marker")" == "$deps_tag" ]]; then
  printf '%s\n' "$target"
  exit 0
fi

rm -rf -- "$target"
git clone --quiet --depth 1 --branch "$deps_tag" "$deps_repository" "$target"
printf '%s\n' "$deps_tag" > "$marker"
printf '%s\n' "$target"
